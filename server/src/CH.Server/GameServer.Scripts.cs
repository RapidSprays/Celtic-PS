using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;

namespace CH.Server;

/// <summary>
/// Monster scripts from Master's Grimoire (mob-scripts.json): timed and conditional actions bosses and other monsters
/// take on top of their AI — Stonecrusher's Body Slam every 5 seconds, skills at low health, switching AI (and with it
/// skill set) in phases, calling for help, dropping aggro, gaining statuses, announcements. Checked once a second while
/// a monster fights; the triggers are in <see cref="MobScriptRules"/>.
/// </summary>
public sealed partial class GameServer
{
    private const double ScriptCheckSeconds = 1;
    private const float AnnouncementRange = 60;

    private readonly MobScriptBook _scripts;

    /// <summary>The AI a monster fights with now: the one a script switched it to, or its own.</summary>
    private MobAi CurrentAi(Mob mob, MobBrain brain) => mob.Scripts.Ai ?? brain.Ai;

    private void RunMobScripts(Zone zone, Mob mob, double now)
    {
        var state = mob.Scripts;
        if (now < state.NextCheckAt) return;
        state.NextCheckAt = now + ScriptCheckSeconds;
        var scripts = _scripts.For(mob.Template.Id);
        if (scripts.Count == 0 || !_brains.TryGet(mob.Template.Id, out var brain))
        {
            state.HitsTaken.Clear();
            return;
        }
        var (mx, mz) = mob.PositionAt(now);
        var view = new ScriptView(now, mob.MaxHealth > 0 ? (double)mob.Health / mob.MaxHealth : 1, state,
            id => mob.Effects.Any(e => e.Info.Id == id && e.ExpiresAt > now),
            id => mob.SkillReadyAt.GetValueOrDefault(id) <= now,
            range => AggroedPlayers(zone, mob).Count(p => Dist2D(p.Position.X, p.Position.Z, mx, mz) <= range),
            (template, range) => zone.Mobs.Values.Count(m => !ReferenceEquals(m, mob) && m.Template.Id == template && m.State != MobState.Dead
                && Dist2D(m.PositionAt(now).X, m.PositionAt(now).Z, mx, mz) <= range));
        foreach (var (aiType, script) in scripts)
        {
            if (mob.State is MobState.Dead or MobState.Returning or MobState.Idle) break;
            if (aiType >= 0 && aiType != CurrentAi(mob, brain).Id) continue;
            if (!MobScriptRules.ShouldRun(script, view)) continue;
            state.Runs[script.Id] = state.Runs.GetValueOrDefault(script.Id) + 1;
            state.LastRunAt[script.Id] = now;
            Announce(zone, mob, script.Say, now);
            foreach (var action in script.Do) RunScriptAction(zone, mob, brain, action, now);
        }
        state.HitsTaken.Clear();
    }

    /// <summary>One script action, by Master's Grimoire's codes ("Action: …").</summary>
    private void RunScriptAction(Zone zone, Mob mob, MobBrain brain, double[] a, double now)
    {
        double Arg(int i) => i < a.Length ? a[i] : 0;
        var skillId = (int)Arg(1);
        var target = mob.Target as Session;
        switch ((int)Arg(0))
        {
            case 101: // change AI type (and the skill set that goes with it)
                if (_brains.Ai(skillId) is { } ai)
                {
                    mob.Scripts.Ai = ai;
                    mob.Scripts.AiChangedAt = now;
                }
                break;
            case 102: // use skill (on its target)
                if (target is not null) ScriptCastOnPlayers(zone, mob, brain, skillId, [target], now);
                break;
            case 103: // reduce aggro by x
                foreach (var key in mob.Threat.Keys.ToList()) mob.Threat[key] *= Math.Max(0, 1 - Arg(1));
                break;
            case 104: // call enemies within m
                CallForHelp(zone, mob, _callForHelp.Range(mob.Template.Id, (float)Arg(1)), now); // call-for-help.json
                break;
            case 105: // stop attacking target
                if (target is not null) LoseTarget(zone, mob, target, now);
                break;
            case 106: // use skill on player with #n highest aggro (within m)
                if (AggroedPlayers(zone, mob).Where(p => InRange(p, mob, now, (float)Arg(3))).Skip((int)Arg(2) - 1).FirstOrDefault() is { } nth)
                    ScriptCastOnPlayers(zone, mob, brain, skillId, [nth], now);
                break;
            case 107: // on #n closest player to its target (max m)
                if (target is not null && PlayersByDistance(zone, mob, target.Position.X, target.Position.Z, (float)Arg(3)).Skip((int)Arg(2) - 1).FirstOrDefault() is { } near)
                    ScriptCastOnPlayers(zone, mob, brain, skillId, [near], now);
                break;
            case 108: // on #n closest player (max m)
                {
                    var (x, z) = mob.PositionAt(now);
                    if (PlayersByDistance(zone, mob, x, z, (float)Arg(3)).Skip((int)Arg(2) - 1).FirstOrDefault() is { } closest)
                        ScriptCastOnPlayers(zone, mob, brain, skillId, [closest], now);
                    break;
                }
            case 109: // on #n closest of a monster (heals and buffs its allies)
            case 113: // on a random one of the n closest of a monster
                {
                    var allies = MobsByDistance(zone, mob, (int)Arg(3), now).Take(Math.Max(1, (int)Arg(2))).ToList();
                    var ally = (int)Arg(0) == 109 ? allies.Skip((int)Arg(2) - 1).FirstOrDefault() : allies.Count > 0 ? allies[Random.Shared.Next(allies.Count)] : null;
                    if (ally is not null) ScriptCastOnMob(zone, mob, ally, brain, skillId, now);
                    break;
                }
            case 110: // gain status (level index, caster level)
                if (_balance.StatusEffects.TryGetValue(skillId, out var gained)) ApplyMobStatus(zone, mob, gained, (int)Arg(2), now, mob);
                break;
            case 111: // remove status
                if (mob.Effects.RemoveAll(e => e.Info.Id == skillId) > 0) BroadcastMobEffects(zone, mob);
                break;
            case 112: // use skill on self
                ScriptCastOnMob(zone, mob, mob, brain, skillId, now);
                break;
            case 114: // on a random aggro'd player within m (of the top n)
                {
                    var top = AggroedPlayers(zone, mob).Where(p => InRange(p, mob, now, (float)Arg(3))).Take(Math.Max(1, (int)Arg(2))).ToList();
                    if (top.Count > 0) ScriptCastOnPlayers(zone, mob, brain, skillId, [top[Random.Shared.Next(top.Count)]], now);
                    break;
                }
            case 115: // on random enemies within m of its target (n at most)
                if (target is not null)
                    ScriptCastOnPlayers(zone, mob, brain, skillId, RandomPlayers(zone, mob, target.Position.X, target.Position.Z, (float)Arg(3), (int)Arg(2)), now);
                break;
            case 116: // on random enemies within m (n at most)
                {
                    var (x, z) = mob.PositionAt(now);
                    ScriptCastOnPlayers(zone, mob, brain, skillId, RandomPlayers(zone, mob, x, z, (float)Arg(3), (int)Arg(2)), now);
                    break;
                }
            // 121 (warp players) isn't run: players' positions are the client's.
        }
    }

    /// <summary>Players it's fighting, highest threat first (alive, in the zone, not hidden from it).</summary>
    private IEnumerable<Session> AggroedPlayers(Zone zone, Mob mob) =>
        mob.Threat.Where(t => t.Key is Session p && p.InZone && !p.Dead && zone.Players.Contains(p) && !IsHiddenFrom(p, mob))
            .OrderByDescending(t => t.Value).Select(t => (Session)t.Key);

    private static bool InRange(Session p, Mob mob, double now, float range)
    {
        if (range <= 0) return true;
        var (x, z) = mob.PositionAt(now);
        return Dist2D(p.Position.X, p.Position.Z, x, z) <= range;
    }

    private IEnumerable<Session> PlayersByDistance(Zone zone, Mob mob, float x, float z, float range) =>
        zone.Players.Where(p => p.InZone && !p.Dead && !IsHiddenFrom(p, mob))
            .Select(p => (Player: p, Dist: Dist2D(p.Position.X, p.Position.Z, x, z)))
            .Where(p => range <= 0 || p.Dist <= range).OrderBy(p => p.Dist).Select(p => p.Player);

    private List<Session> RandomPlayers(Zone zone, Mob mob, float x, float z, float range, int count) =>
        PlayersByDistance(zone, mob, x, z, range).OrderBy(_ => Random.Shared.Next()).Take(Math.Max(1, count)).ToList();

    private static IEnumerable<Mob> MobsByDistance(Zone zone, Mob mob, int template, double now)
    {
        var (x, z) = mob.PositionAt(now);
        return zone.Mobs.Values.Where(m => m.Template.Id == template && m.State != MobState.Dead)
            .OrderBy(m => Dist2D(m.PositionAt(now).X, m.PositionAt(now).Z, x, z));
    }

    /// <summary>Monsters around it join the fight on its target ("Call Enemies Within m").</summary>
    private void CallForHelp(Zone zone, Mob mob, float range, double now)
    {
        if (mob.Target is not Session target) return;
        var (x, z) = mob.PositionAt(now);
        foreach (var other in zone.Mobs.Values.Where(m => !ReferenceEquals(m, mob) && !m.IsNpc && !m.IsFishingSpot
                     && m.State is MobState.Idle && Dist2D(m.PositionAt(now).X, m.PositionAt(now).Z, x, z) <= range).ToList())
            Engage(zone, other, target, now);
    }

    /// <summary>
    /// A scripted skill on players. Skills that do nothing a player would see (teleports, telegraph markers with no
    /// damage or status) are skipped; area skills also hit everyone around each target.
    /// </summary>
    private void ScriptCastOnPlayers(Zone zone, Mob mob, MobBrain brain, int skillId, IReadOnlyList<Session> targets, double now)
    {
        if (!_balance.Skills.TryGetValue(skillId, out var skill) || targets.Count == 0) return;
        var level = skill.Level(brain.RankOf(skillId));
        var heals = level.InitialDamage < 0 || level.BaseDamage < 0;
        if (heals)
        {
            ScriptCastOnMob(zone, mob, mob, brain, skillId, now);
            return;
        }
        if (level.InitialDamage == 0 && level.BaseDamage == 0 && skill.StatusEffectId <= 0 && !skill.IncludesWeaponAttack) return;
        var choice = new MobSkillChoice(skillId, 1, MobBrains.KindAttack, brain.RankOf(skillId));
        StartMobCast(zone, mob, targets, skill, choice, brain, now);
    }

    /// <summary>
    /// A scripted skill on a monster (itself or an ally): heals restore health, statuses land on it. Damage a monster
    /// would deal to itself ("Acknowledge Skill Hit" markers) is ignored.
    /// </summary>
    private void ScriptCastOnMob(Zone zone, Mob caster, Mob target, MobBrain brain, int skillId, double now)
    {
        if (!_balance.Skills.TryGetValue(skillId, out var skill) || target.State == MobState.Dead) return;
        var rank = brain.RankOf(skillId);
        var level = skill.Level(rank);
        caster.SkillReadyAt[skillId] = now + Math.Max(1, level.RechargeTime);
        if (level.InitialDamage < 0 || level.BaseDamage < 0)
        {
            var ability = skill.AbilityId >= 0 ? brain.Abilities.GetValueOrDefault(skill.AbilityId) : 0;
            target.Health = Math.Min(target.MaxHealth, target.Health + (int)SkillInfo.Amount(level, ability, 0));
            Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([], [new HealthUpdate(HealthUpdate.Mob, (int)target.ServerId, target.Health)]).Write);
        }
        if (skill.StatusEffectId > 0 && _balance.StatusEffects.TryGetValue(skill.StatusEffectId, out var info))
            ApplyMobStatus(zone, target, info, rank, now, caster);
    }

    /// <summary>A status on a monster from a monster (its own buffs, auras and charge-ups, or an ally's).</summary>
    private void ApplyMobStatus(Zone zone, Mob mob, StatusEffectInfo info, int levelIndex, double now, Mob caster)
    {
        var index = Math.Clamp(levelIndex, 0, Math.Max(0, info.Levels.Count - 1));
        if (info.Level(index) is not { } level) return;
        mob.Effects.RemoveAll(e => e.Info.Id == info.Id);
        mob.Effects.Add(new ActiveEffect
        {
            Info = info, LevelIndex = index, ExpiresAt = now + (level.Duration > 0 ? level.Duration : PermanentSeconds), NextTickAt = now + TickOf(info),
            Caster = caster,
        });
        BroadcastMobEffects(zone, mob);
    }

    /// <summary>A script's announcement to players around the monster ("&lt;MN&gt;" is its name).</summary>
    private void Announce(Zone zone, Mob mob, string say, double now)
    {
        if (string.IsNullOrWhiteSpace(say) || say.Equals("Not used", StringComparison.OrdinalIgnoreCase)) return;
        var text = say.Replace("<MN>", mob.Template.Name);
        var (x, z) = mob.PositionAt(now);
        foreach (var p in zone.Players.Where(p => p.InZone && Dist2D(p.Position.X, p.Position.Z, x, z) <= AnnouncementRange))
            Send(p, Opcode.GeneralChat, new ChatMessage(ChatChannel.System, "", text).WriteToClient);
    }
}
