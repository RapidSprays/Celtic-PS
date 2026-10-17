using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Zones, mobs and combat (docs/protocol-notes.md §5). Everything runs on the pump thread: messages and the 10 Hz
/// <see cref="Tick"/>. Times are the server's <see cref="NetTime"/>, which is the clock the client syncs to.
/// Numbers for mobs and damage are approximated (Game/Mob.cs MobStats).
/// </summary>
public sealed partial class GameServer
{
    private const double TickSeconds = 0.1;
    private const double ConcentrationIntervalSeconds = 3;
    private const float MeleeTolerance = 1.5f;
    private const float PlayerRadius = 0.3f;
    // The client shows the kill report (chat line and loot pop-up) only once the death animation ends and the corpse
    // sinks; a corpse removed sooner loses it, which big bosses' long deaths did at 4 s.
    private const double MobCorpseSeconds = 10;

    private sealed class Zone
    {
        public required int Id { get; init; }
        public Dictionary<uint, Mob> Mobs { get; } = new();
        public List<Session> Players { get; } = new();
        public double NextBatchAt { get; set; }
        public Dictionary<int, GroundItem> GroundItems { get; set; } = new();
    }

    private readonly Dictionary<int, Zone> _zones = new();
    private double _nextTick;
    private int _damageId;
    private readonly Dictionary<int, float> _mobScales;
    private readonly Dictionary<int, int> _skillTypes;
    private readonly MobBrains _brains;
    private readonly RegenRules _regen;

    /// <summary>
    /// Whether idle monsters attack players who come within their aggro range (server/content/aggro.json). Off by the
    /// owner's rule: monsters only fight once attacked, or when a monster in the fight calls them for help.
    /// </summary>
    private readonly bool _aggroOnSight;
    private readonly XpBonus _xpBonus;
    private const float DivineLightRange = 15;

    /// <summary>Monster → the monster whose official loot it drops (server/content/loot-links.json).</summary>
    private readonly Dictionary<int, int> _lootLinks;

    private static Dictionary<int, int> LoadLootLinks(string? path)
    {
        var links = new Dictionary<int, int>();
        if (path is null || !File.Exists(path)) return links;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var e in doc.RootElement.GetProperty("links").EnumerateObject())
            if (int.TryParse(e.Name, out var id) && e.Value.TryGetProperty("from", out var from)) links[id] = from.GetInt32();
        return links;
    }

    private static bool LoadAggroOnSight(string? path)
    {
        if (path is null || !File.Exists(path)) return false;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.TryGetProperty("aggroOnSight", out var on) && on.GetBoolean();
    }
    private readonly Dictionary<int, float[]> _skillAggro;

    /// <summary>Server ids for respawned monsters, above every other range (auto camps 30000+, NPC spawn ids, GM spawns, fishing).</summary>
    private uint _nextRespawnId = 1_000_000_000;

    // ---- zones ------------------------------------------------------------------------------

    private Zone ZoneFor(int zoneId)
    {
        if (_zones.TryGetValue(zoneId, out var zone)) return zone;
        zone = new Zone { Id = zoneId };
        var now = NetTime.Now;
        foreach (var mob in ZonePopulation.Build(zoneId, _balance, now, _generatedCamps, _officialStats, _spawnTuning)) zone.Mobs[mob.ServerId] = mob;
        foreach (var npc in NpcsFor(zoneId, now)) zone.Mobs.TryAdd(npc.ServerId, npc);
        foreach (var spot in FishingSpotsFor(zoneId, now)) zone.Mobs.TryAdd(spot.ServerId, spot);
        zone.GroundItems = GroundItemsFor(zoneId);
        foreach (var spawn in _store.ListSpawns(zoneId))
            if (MobFromSpawn(zone, spawn, now) is { } mob) zone.Mobs[mob.ServerId] = mob;
        _zones[zoneId] = zone;
        _log.LogInformation("Zone {Zone} populated: {Mobs} monsters, {Npcs} NPCs", zoneId,
            zone.Mobs.Values.Count(m => !m.IsNpc), zone.Mobs.Values.Count(m => m.IsNpc));
        return zone;
    }

    /// <summary>GM-placed spawn → live mob. NPCs reuse an official spawn id when one carries their quest/shop icons.</summary>
    private Mob? MobFromSpawn(Zone zone, SpawnRecord spawn, double now)
    {
        if (!_balance.Mobs.TryGetValue(spawn.TemplateId, out var template)) return null;
        var serverId = 20000 + (uint)spawn.Id;
        if (spawn.Opinion >= 50 && _balance.SpawnIconIdsByMob.TryGetValue(spawn.TemplateId, out var iconIds))
            serverId = iconIds.Select(i => (uint)i).FirstOrDefault(i => !zone.Mobs.ContainsKey(i), serverId);
        var mob = new Mob
        {
            ServerId = serverId,
            Template = template,
            Level = spawn.Level,
            HomeX = spawn.X,
            HomeY = spawn.Y,
            HomeZ = spawn.Z,
            Direction = spawn.Direction,
            Opinion = spawn.Opinion,
            ConversationId = spawn.ConversationId,
            AggroRange = spawn.Opinion >= 50 ? 0 : MobStats.AggroRange(template.Power),
            RespawnSeconds = spawn.RespawnSeconds,
            RoamRadius = spawn.RoamRadius,
            SpawnRecordId = spawn.Id,
        };
        mob.Reset(now);
        return mob;
    }

    private MobSpawn ToWire(Mob m, double now)
    {
        var (x, z) = m.PositionAt(now);
        var rad = m.Direction * MathF.PI / 180f;
        return new MobSpawn(m.ServerId, (uint)m.Template.Id, x, m.HomeY, z, MathF.Sin(rad), MathF.Cos(rad), m.Opinion,
            Math.Max(0, m.Health), 0, m.ConversationId, m.IsNpc ? 0 : m.Level, _aggroOnSight ? m.AggroRange : 0, m.Scale ?? _mobScales.GetValueOrDefault(m.Template.TemplateId, 1f));
    }

    /// <summary>
    /// Model size by appearance (server/content/mob-scales.json). The client sizes the model, and places its name at
    /// BaseHeight × size, from the spawn's last field; golems' BaseHeight is 250, so they need 0.01.
    /// </summary>
    private static Dictionary<int, float> LoadMobScales(string? path)
    {
        var scales = new Dictionary<int, float>();
        if (path is null || !File.Exists(path)) return scales;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var a in doc.RootElement.GetProperty("appearances").EnumerateObject())
            scales[int.Parse(a.Name)] = a.Value.GetProperty("scale").GetSingle();
        return scales;
    }

    private void EnterZone(Session s)
    {
        var zone = ZoneFor(s.Position.Zone);
        var wasPresent = zone.Players.Contains(s);
        if (!wasPresent) zone.Players.Add(s);
        s.InZone = true;
        var now = NetTime.Now;
        var (_, x, y, z, _) = s.Position;
        var others = Others(zone, s).Select(Basic).ToList();
        var (available, notYet) = QuestLists(s);
        // Quest lists before monsters: the client works out NPC quest icons as each one is added.
        Send(s, Opcode.EnteringNewZoneUpdate, new EnteringNewZoneUpdate(x, y, z)
        {
            Players = others, AvailableQuests = available, NotYetAvailableQuests = notYet, Friends = FriendEntries(s),
        }.Write);
        var alive = zone.Mobs.Values.Where(m => m.State != MobState.Dead).ToList();
        if (alive.Count > 0) Send(s, Opcode.BatchZoneMonsterUpdate, b => MobSpawn.WriteBatch(b, alive.Select(m => ToWire(m, now)).ToList()));
        SendMobEffects(s, alive.Where(m => m.HasChaosAura || m.Effects.Count > 0).ToList());
        var moving = alive.Where(m => m.IsMoving(now)).Select(m => Leg(m, returning: m.State == MobState.Returning)).ToList();
        if (moving.Count > 0) Send(s, Opcode.mobPatrolUpdate, new MobPatrol(moving).Write);
        SendGroundItems(zone, s);
        Send(s, Opcode.AllZoningMessagesSent, _ => { });
        AnnounceArrival(zone, s, wasPresent);
        RefreshHateList(s);
        RefreshZoneHate(zone.Id);
    }

    /// <summary>Leaves the current zone on disconnect or zone change (buffs survive a zone change).</summary>
    private void LeaveZone(Session s, bool changingZone = false)
    {
        if (s.Trade is { } trade) CancelTrade(trade, s);
        if (!_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        SetAttackTarget(s, null);
        if (zone.Players.Remove(s)) AnnounceDeparture(zone, s);
        s.InZone = false;
        RefreshZoneHate(zone.Id);
        s.Casts.Clear();
        if (!changingZone)
        {
            SavePetFeed(s);
            s.Effects.Clear();
        }
        foreach (var mob in zone.Mobs.Values.Where(m => ReferenceEquals(m.Target, s)).ToList()) LoseTarget(zone, mob, s, NetTime.Now);
        if (s.Character is not null) _store.SaveVitals(s.Character.Id, s.Dead ? -1 : s.Health, s.Energy);
    }

    private void Broadcast(Zone zone, Opcode opcode, Action<NetBuffer> write)
    {
        foreach (var p in zone.Players.ToList()) Send(p, opcode, write);
    }

    private static MobLeg Leg(Mob m, bool returning) => new(m.ServerId, m.LegEta, m.FromX, m.FromZ, m.ToX, m.ToZ, returning);

    private void BroadcastLeg(Zone zone, Mob m, bool returning = false) =>
        Broadcast(zone, Opcode.mobPatrolUpdate, new MobPatrol([Leg(m, returning)]).Write);

    // ---- tick -------------------------------------------------------------------------------

    private void MaybeTick()
    {
        var now = NetTime.Now;
        if (now < _nextTick) return;
        _nextTick = now + TickSeconds;
        foreach (var zone in _zones.Values.Where(z => z.Players.Count > 0).ToList()) Tick(zone, now);
        ExpireAuctions(now);
        ExpireFriendRequests(now);
        TickCrafts(now);
        ExpireClanInvites(now);
        TickDuels(now);
    }

    private void Tick(Zone zone, double now)
    {
        foreach (var mob in zone.Mobs.Values.ToList()) TickMob(zone, mob, now);
        foreach (var s in zone.Players.ToList()) TickPlayer(zone, s, now);
        TickGroundItems(zone, now);
        TickPositions(zone, now);
    }

    private void TickMob(Zone zone, Mob mob, double now)
    {
        if (mob.State == MobState.Dead)
        {
            if (!mob.Removed && now >= mob.RemoveAt)
            {
                mob.Removed = true;
                Broadcast(zone, Opcode.ZoneMonsterDisappeared, b => b.WriteVariableUInt32(mob.ServerId));
            }
            if (now >= mob.RespawnAt)
            {
                if (mob.Variants.Count > 1)
                {
                    // The spawn point may bring back a different monster (Stonefang Wretch, Guard, Doomclaw…). The client
                    // keeps a model per server id, so the newcomer gets an id it has never seen.
                    mob.Reroll(Random.Shared);
                    zone.Mobs.Remove(mob.ServerId);
                    mob.ServerId = _nextRespawnId++;
                    zone.Mobs[mob.ServerId] = mob;
                }
                mob.Reset(now);
                Broadcast(zone, Opcode.ZoneMonsterAppeared, ToWire(mob, now).Write);
                if (mob.HasChaosAura) foreach (var p in zone.Players) SendMobEffects(p, [mob]);
            }
            return;
        }
        if (mob.IsNpc) return;
        if (mob.IsFishingSpot)
        {
            TickFishingSpot(zone, mob, now);
            return;
        }
        TickMobEffects(zone, mob, now);
        if (mob.State == MobState.Dead) return;

        switch (mob.State)
        {
            case MobState.Idle:
                if (_aggroOnSight && mob.AggroRange > 0 && NearestVictim(zone, mob, mob.AggroRange, now) is { } victim)
                {
                    Engage(zone, mob, victim, now);
                    break;
                }
                if (mob.RoamRadius > 0 && now >= mob.NextThinkAt && !mob.IsMoving(now))
                {
                    var angle = Random.Shared.NextDouble() * Math.PI * 2;
                    var dist = Math.Sqrt(Random.Shared.NextDouble()) * mob.RoamRadius;
                    var eta = mob.MoveTo(now, mob.HomeX + (float)(Math.Cos(angle) * dist), mob.HomeZ + (float)(Math.Sin(angle) * dist), Mob.RoamSpeed);
                    mob.NextThinkAt = eta + 4 + Random.Shared.NextDouble() * 8;
                    BroadcastLeg(zone, mob);
                }
                break;

            case MobState.Chasing:
            case MobState.Fighting:
                if (mob.DistanceFromHome(now) > Mob.LeashDistance)
                {
                    StartReturn(zone, mob, now); // dragged too far from home: it gives up on everyone
                    break;
                }
                if (mob.Target is not Session target || !target.InZone || target.Dead || !zone.Players.Contains(target))
                {
                    LoseTarget(zone, mob, mob.Target as Session, now);
                    break;
                }
                if (TopThreat(zone, mob) is { } top && !ReferenceEquals(top, target)
                    && mob.Threat[top] > mob.Threat.GetValueOrDefault(target) * _threat.SwitchMargin) // threat.json
                {
                    // Someone has 10% more threat than its target: it turns on them (a margin, so it doesn't flicker).
                    if (mob.AttackAnnounced)
                        Broadcast(zone, Opcode.AttackState, new AttackState(false, true, (int)mob.ServerId, false, target.CharacterId).Write);
                    mob.Target = top;
                    mob.State = MobState.Chasing;
                    mob.AttackAnnounced = false;
                    break;
                }
                RunMobScripts(zone, mob, now);
                if (mob.State is not (MobState.Chasing or MobState.Fighting) || mob.Target is not Session) break;
                target = (Session)mob.Target;
                if (mob.PendingCast is PendingMobCast casting)
                {
                    if (now < casting.ResolveAt) break; // casting: it stands and doesn't swing
                    mob.PendingCast = null;
                    ResolveMobCast(zone, mob, casting, now);
                    mob.NextAttackAt = Math.Max(mob.NextAttackAt, now + MobStats.AttackInterval(mob.Template));
                    break;
                }
                var (mx, mz) = mob.PositionAt(now);
                var (_, px, _, pz, _) = target.Position;
                var distance = MathF.Sqrt((px - mx) * (px - mx) + (pz - mz) * (pz - mz));
                var reach = mob.Template.AttackRange + mob.Template.Radius + PlayerRadius + 0.5f;
                if (distance > reach)
                {
                    mob.State = MobState.Chasing;
                    if (now >= mob.NextThinkAt)
                    {
                        var stop = Math.Max(0.5f, reach * 0.7f) / Math.Max(distance, 0.01f);
                        mob.MoveTo(now, px - (px - mx) * stop, pz - (pz - mz) * stop, Mob.ChaseSpeed);
                        mob.NextThinkAt = now + 0.5;
                        BroadcastLeg(zone, mob);
                    }
                    break;
                }
                if (mob.State == MobState.Chasing || mob.IsMoving(now))
                {
                    mob.StopAt(now);
                    BroadcastLeg(zone, mob);
                }
                mob.State = MobState.Fighting;
                if (!mob.AttackAnnounced)
                {
                    mob.AttackAnnounced = true;
                    Broadcast(zone, Opcode.AttackState, new AttackState(true, true, (int)mob.ServerId, false, target.CharacterId).Write);
                    mob.NextAttackAt = Math.Max(mob.NextAttackAt, now + 0.5);
                }
                if (now >= mob.NextAttackAt)
                {
                    var debuffs = MobDebuffs(mob);
                    if (debuffs.Stunned)
                    {
                        mob.NextAttackAt = now + 0.5; // stunned: no attack until it wears off
                        break;
                    }
                    mob.NextAttackAt = now + MobStats.AttackInterval(mob.Template) * 100 / (100 + Math.Max(-90, debuffs.AttackSpeedPercent));
                    MobHits(zone, mob, target, now);
                }
                break;

            case MobState.Returning:
                if (now >= mob.LegEta)
                {
                    mob.State = MobState.Idle;
                    mob.Health = mob.MaxHealth;
                    SetMobOwner(zone, mob, null); // back home: anyone may tag it again
                    mob.NextThinkAt = now + 3;
                    Broadcast(zone, Opcode.NewEntitiesOfInterest, b =>
                    {
                        b.WriteVariableInt32(1);
                        b.WriteVariableInt32(1);
                        b.WriteVariableInt32((int)mob.ServerId);
                        b.WriteVariableInt32(mob.Health);
                        b.Write((byte)0);
                        b.Write(mob.HomeX);
                        b.Write(mob.HomeY);
                        b.Write(mob.HomeZ);
                    });
                }
                break;
        }
    }

    private Session? NearestVictim(Zone zone, Mob mob, float range, double now)
    {
        var (mx, mz) = mob.PositionAt(now);
        return zone.Players
            .Where(p => p.InZone && !p.Dead && !IsHiddenFrom(p, mob))
            .Select(p => (Player: p, Dist: MathF.Sqrt((p.Position.X - mx) * (p.Position.X - mx) + (p.Position.Z - mz) * (p.Position.Z - mz))))
            .Where(p => p.Dist <= range * (100 - Modifiers(p.Player).CamouflagePercent) / 100f)
            .OrderBy(p => p.Dist)
            .Select(p => p.Player)
            .FirstOrDefault();
    }

    private void Engage(Zone zone, Mob mob, Session target, double now)
    {
        if (mob.IsNpc || mob.State == MobState.Dead) return;
        if (mob.State is MobState.Chasing or MobState.Fighting && ReferenceEquals(mob.Target, target)) return;
        if (mob.State is MobState.Idle or MobState.Returning) mob.Scripts.Start(now); // a new fight: its scripts' clocks start
        AddThreat(mob, target, 1); // being noticed puts you on its list (a token amount)
        mob.Target = target;
        mob.State = MobState.Chasing;
        mob.AttackAnnounced = false;
        mob.NextThinkAt = now;
        Broadcast(zone, Opcode.EntityCombatStateChanged, new CombatStateChanged([(1, (int)mob.ServerId, true)]).Write);
    }

    /// <summary>A skill's own aggro at a rank (skill-aggro.json from the Grimoire import; the last rank's past its table).</summary>
    private double SkillAggro(int skillId, int rank) =>
        _threat.SkillFlat(skillId, _skillAggro.TryGetValue(skillId, out var ranks) && ranks.Length > 0 ? ranks[Math.Clamp(rank, 0, ranks.Length - 1)] : 0); // threat.json

    private static Dictionary<int, float[]> LoadSkillAggro(string? path)
    {
        var aggro = new Dictionary<int, float[]>();
        if (path is null || !File.Exists(path)) return aggro;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var e in doc.RootElement.GetProperty("skills").EnumerateObject())
            aggro[int.Parse(e.Name)] = e.Value.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        return aggro;
    }

    /// <summary>
    /// Master's Grimoire's aggro factor: a player more than 50 levels above the monster makes a tenth of the threat; a
    /// monster more than 10 levels above the player takes 20% more threat per level beyond that.
    /// </summary>
    private static double AggroFactor(int playerLevel, int mobLevel) =>
        playerLevel - mobLevel > 50 ? 0.1 : mobLevel - playerLevel > 10 ? 1 + (mobLevel - playerLevel - 10) * 0.2 : 1;

    /// <summary>Adds threat towards a player.</summary>
    private static void AddThreat(Mob mob, Session s, double amount)
    {
        if (amount <= 0 || mob.IsNpc || mob.State == MobState.Dead) return;
        mob.Threat[s] = mob.Threat.GetValueOrDefault(s) + amount;
    }

    /// <summary>The player with the most threat who can still be fought (alive, in the zone, not hidden from it).</summary>
    private Session? TopThreat(Zone zone, Mob mob) =>
        mob.Threat.Where(t => t.Key is Session p && p.InZone && !p.Dead && zone.Players.Contains(p) && !IsHiddenFrom(p, mob))
            .OrderByDescending(t => t.Value).Select(t => (Session)t.Key).FirstOrDefault();

    /// <summary>
    /// A heal or buff on <paramref name="helped"/>: every monster fighting them (their target, or on its list) takes the
    /// skill's aggro against the helper, as in the live game.
    /// </summary>
    private void HelpThreat(Zone zone, Session helper, Session helped, SkillInfo skill, int rank)
    {
        var aggro = SkillAggro(skill.Id, rank);
        if (aggro <= 0) return;
        foreach (var mob in zone.Mobs.Values.Where(m => m.State is MobState.Chasing or MobState.Fighting
                     && (ReferenceEquals(m.Target, helped) || m.Threat.ContainsKey(helped))))
            AddThreat(mob, helper, aggro * AggroFactor(helper.Sheet!.Level, mob.Level));
    }

    /// <summary>
    /// A monster's target died, left or went away: it turns on whoever has hurt it most among the players still alive
    /// in the zone (and not hidden from it), and only goes home when there's nobody left.
    /// </summary>
    private void LoseTarget(Zone zone, Mob mob, Session? lost, double now)
    {
        if (lost is not null) mob.Threat.Remove(lost);
        var next = TopThreat(zone, mob);
        if (next is null)
        {
            StartReturn(zone, mob, now);
            return;
        }
        if (mob.AttackAnnounced && lost is not null)
            Broadcast(zone, Opcode.AttackState, new AttackState(false, true, (int)mob.ServerId, false, lost.CharacterId).Write);
        mob.Target = next;
        mob.State = MobState.Chasing;
        mob.AttackAnnounced = false;
        mob.NextThinkAt = now;
    }

    private void StartReturn(Zone zone, Mob mob, double now)
    {
        if (mob.AttackAnnounced && mob.Target is Session t)
            Broadcast(zone, Opcode.AttackState, new AttackState(false, true, (int)mob.ServerId, false, t.CharacterId).Write);
        mob.Target = null;
        mob.PendingCast = null; // giving up: the cast is dropped
        mob.AttackAnnounced = false;
        mob.State = MobState.Returning;
        mob.Damage.Clear();
        mob.Threat.Clear();
        mob.Scripts.Clear();
        mob.MoveTo(now, mob.HomeX, mob.HomeZ, Mob.ChaseSpeed);
        BroadcastLeg(zone, mob, returning: true);
        Broadcast(zone, Opcode.EntityCombatStateChanged, new CombatStateChanged([(1, (int)mob.ServerId, false)]).Write);
    }

    // ---- player combat ----------------------------------------------------------------------

    private void TickPlayer(Zone zone, Session s, double now)
    {
        TickAggroMeter(zone, s, now);
        if (s.Dead) return;

        TickPvpAttack(zone, s, now);
        if (s.AttackTarget is { } target)
        {
            if (target.State == MobState.Dead || !zone.Mobs.ContainsKey(target.ServerId)) SetAttackTarget(s, null);
            else if (now >= s.NextSwingAt)
            {
                var sheet = s.Sheet!;
                if (InReach(s, target, sheet.AttackRange + MeleeTolerance, now))
                {
                    var stats = LiveStats(s);
                    PlayerHits(zone, s, target, AttackKind.Attack, 0, AutoAttackDamage(s, target), now);
                    s.NextSwingAt = now + stats.AttackSpeed / 1000.0;
                }
                else
                {
                    s.NextSwingAt = now + 0.25;
                }
            }
        }

        foreach (var cast in s.Casts.Where(c => now >= c.ResolveAt).ToList())
        {
            s.Casts.Remove(cast);
            ResolveCast(zone, s, cast, now);
        }

        TickEffects(zone, s, now);

        if (now >= s.NextConcentrationAt)
        {
            s.NextConcentrationAt = now + ConcentrationIntervalSeconds;
            RegenConcentration(s, now);
        }
        if (now >= s.NextRegenAt)
        {
            // Owner's rules (server/content/regen.json): a flat base every tick (2 health, 2 energy every 5 s), not
            // scaled by level; out of combat, sigils and out-of-combat gear add theirs. Gear regen and buffs always add.
            s.NextRegenAt = now + _regen.TickSeconds;
            var outOfCombat = now - s.LastCombatAt > _regen.OutOfCombatSeconds;
            var m = Modifiers(s);
            var tree = s.Sheet!.Tree;
            var gear = s.Sheet.GearRegenPerTick;
            var baseOn = outOfCombat || _regen.BaseInCombat;
            var restHealth = tree.Has(PassiveTree.LifeTap) ? 0
                : (baseOn ? _regen.BaseHealth : 0) + (outOfCombat ? s.SigilsOfHealth * _regen.PerSigil + gear.HealthOutOfCombat : 0);
            var restEnergy = (baseOn ? _regen.BaseEnergy : 0) + (outOfCombat ? s.SigilsOfEnergy * _regen.PerSigil + gear.EnergyOutOfCombat : 0);
            var health = Math.Min(s.MaxHealth, s.Health + restHealth + gear.Health + m.HealthRegen + (int)(s.MaxHealth * tree["healthRegenPct"] / 100));
            var energy = Math.Min(s.MaxEnergy, s.Energy + restEnergy + gear.Energy + m.EnergyRegen + (int)(s.MaxEnergy * tree["energyRegenPct"] / 100));
            if (health != s.Health || energy != s.Energy)
            {
                s.Health = health;
                s.Energy = energy;
                Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.Health, s.Health), (StatKey.Energy, s.Energy)]).Write);
                ShareHealth(zone, s);
            }
        }
    }

    /// <summary>Regeneration rules (server/content/regen.json).</summary>
    private sealed record RegenRules(double TickSeconds, int BaseHealth, int BaseEnergy, bool BaseInCombat, double OutOfCombatSeconds, int PerSigil)
    {
        public static RegenRules Load(string? path)
        {
            var rules = new RegenRules(5, 2, 2, true, 6, Sigils.RegenPerSigil);
            if (path is null || !File.Exists(path)) return rules;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            double D(string n, double d) => r.TryGetProperty(n, out var v) ? v.GetDouble() : d;
            return new RegenRules(Math.Max(0.5, D("tickSeconds", rules.TickSeconds)), (int)D("baseHealth", rules.BaseHealth), (int)D("baseEnergy", rules.BaseEnergy),
                r.TryGetProperty("baseInCombat", out var b) ? b.GetBoolean() : rules.BaseInCombat, D("outOfCombatSeconds", rules.OutOfCombatSeconds), (int)D("perSigil", rules.PerSigil));
        }
    }

    private static bool InReach(Session s, Mob mob, float range, double now)
    {
        var (mx, mz) = mob.PositionAt(now);
        var dx = s.Position.X - mx;
        var dz = s.Position.Z - mz;
        return MathF.Sqrt(dx * dx + dz * dz) - mob.Template.Radius - PlayerRadius <= range;
    }

    private void OnStartAttacking(Session s, StartAttacking req)
    {
        if (req.TargetType == TargetType.OtherPlayer)
        {
            if (Enemy(s, (int)req.TargetId) is not { } enemy) return;
            SetAttackTarget(s, null);
            SetPvpTarget(s, enemy);
            s.NextSwingAt = Math.Max(s.NextSwingAt, NetTime.Now + 0.3);
            BreakStealth(s);
            return;
        }
        if (req.TargetType != TargetType.Mob || !_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        if (!zone.Mobs.TryGetValue(req.TargetId, out var mob) || mob.IsNpc || mob.State == MobState.Dead) return;
        if (mob.IsFishingSpot ? !StartFishing(zone, s, mob, NetTime.Now) : Rod(s) is not null) return; // rods only fish
        SetPvpTarget(s, null);
        SetAttackTarget(s, mob);
        s.NextSwingAt = Math.Max(s.NextSwingAt, NetTime.Now + 0.3);
        BreakStealth(s);
    }

    private void OnUseSkill(Session s, UseSkill req)
    {
        var now = NetTime.Now;
        var known = s.Sheet!.UsableSkills.FirstOrDefault(k => k.SkillId == (int)req.SkillId);
        var greyedOut = (EquippedPet(s)?.EquipSkillId == (int)req.SkillId && !PetFed(s)) || (s.Sheet.Mount?.EquipSkillId == (int)req.SkillId && Dismounted(s));
        if (known == default || greyedOut || !_balance.Skills.TryGetValue((int)req.SkillId, out var skill) || s.Dead)
        {
            Send(s, Opcode.SkillUpdate, new SkillUpdate((int)req.SkillId, ShownRank(s, (int)req.SkillId), CooldownLeft(s, (int)req.SkillId, now)).Write);
            return;
        }
        var rank = s.Sheet.EffectiveRank(skill.Id, known.Level); // trained rank plus gear and passive ranks
        var level = skill.Level(rank);
        var cost = s.Sheet.EnergyCost(skill.Id, level.EnergyCost);
        Mob? target = null;
        Session? enemy = null;
        if (skill.CastTarget == "Enemy" && req.TargetType == TargetType.OtherPlayer)
        {
            if (Enemy(s, (int)req.TargetId) is { } e
                && Dist2D(s.Position.X, s.Position.Z, e.Position.X, e.Position.Z) - 2 * PlayerRadius <= Math.Max(skill.CastRange, s.Sheet.AttackRange) + MeleeTolerance + 1f)
                enemy = e;
        }
        else if (skill.CastTarget is "Enemy" or "Fish")
        {
            if (_zones.TryGetValue(s.Position.Zone, out var zone) && zone.Mobs.TryGetValue(req.TargetId, out var mob)
                && !mob.IsNpc && mob.State != MobState.Dead && mob.IsFishingSpot == (skill.CastTarget == "Fish")
                && InReach(s, mob, Math.Max(skill.CastRange, s.Sheet.AttackRange) + MeleeTolerance + 1f, now))
                target = mob;
        }
        // Heals and buffs on another player: anyone in the zone within cast range (the client sends their character id).
        Session? friend = null;
        var friendMissing = false;
        if (skill.CastTarget == "Friendly" && req.TargetType == TargetType.OtherPlayer && req.TargetId != (uint)s.CharacterId)
        {
            friend = OnlineByCharacter((int)req.TargetId) is { InZone: true, Dead: false } f && f.Position.Zone == s.Position.Zone
                     && Dist2D(s.Position.X, s.Position.Z, f.Position.X, f.Position.Z) - 2 * PlayerRadius <= Math.Max(skill.CastRange, 2f) + MeleeTolerance + 1f
                ? f : null;
            friendMissing = friend is null;
        }
        var onCooldown = s.Cooldowns.TryGetValue(skill.Id, out var readyAt) && now < readyAt - 0.25;
        if (!s.Sheet.CanUse(skill))
        {
            Send(s, Opcode.SkillUpdate, new SkillUpdate(skill.Id, rank, CooldownLeft(s, skill.Id, now)).Write);
            Tell(s, $"{skill.Name} needs {string.Join(" or ", skill.WeaponRequirements.Select(WeaponName))}.");
            return;
        }
        if ((skill.CastTarget == "Enemy" && target is null && enemy is null) || friendMissing || onCooldown || s.Energy < cost)
        {
            // Tells the client the skill failed. It takes the cooldown sent as the time left, so a refused press keeps the
            // skill's real remaining cooldown on the bar instead of clearing it (0 would show the skill as ready).
            Send(s, Opcode.SkillUpdate, new SkillUpdate(skill.Id, rank, CooldownLeft(s, skill.Id, now)).Write);
            return;
        }
        // Weapon-delay skills (castWithWeaponDelay) take the weapon's attack delay, haste and elixirs included, on top.
        var castTime = level.CastingTime + (skill.CastWithWeaponDelay ? LiveStats(s).AttackSpeed / 1000.0 : 0);
        // Owner's rule (2026-10-17): skills queue. One pressed while another is still casting starts when that one lands,
        // so Pummel pressed during Shatter's cast lands after Shatter.
        var start = s.Casts.Count > 0 ? Math.Max(now, s.Casts.Max(c => c.ResolveAt)) : now;
        s.Casts.Add(new PendingCast(skill, rank, target, start + castTime, enemy, friend));
    }

    /// <summary>
    /// Tells the client mod which skills take the weapon's delay on top of their cast time (skill-overrides.json
    /// "castWithWeaponDelay"), so its casting circle matches the server without a new mod build.
    /// </summary>
    private void SendWeaponDelaySkills(Session s)
    {
        var ids = _balance.Skills.Values.Where(k => k.CastWithWeaponDelay).Select(k => k.Id).OrderBy(i => i).ToList();
        Send(s, PrivateOpcode.WeaponDelaySkills, b =>
        {
            b.WriteVariableInt32(ids.Count);
            foreach (var id in ids) b.WriteVariableInt32(id);
        });
    }

    /// <summary>Seconds until a skill can be cast again (0 when it's ready).</summary>
    private static float CooldownLeft(Session s, int skillId, double now) =>
        s.Cooldowns.TryGetValue(skillId, out var readyAt) && readyAt > now ? (float)(readyAt - now) : 0f;

    /// <summary>
    /// The rank a SkillUpdate carries. The client stores it as the skill's rank and reads that rank's recharge time for the
    /// cooldown sweep, so it must be the real one (0 would show rank 0 and the wrong sweep).
    /// </summary>
    private static int ShownRank(Session s, int skillId)
    {
        var known = s.Sheet!.UsableSkills.FirstOrDefault(k => k.SkillId == skillId);
        return known == default ? 0 : s.Sheet.EffectiveRank(skillId, known.Level);
    }

    /// <summary>The skill list with each skill's cooldown left.</summary>
    private static IReadOnlyList<SkillEntry> SkillEntries(Session s, CharacterSheet sheet)
    {
        var now = NetTime.Now;
        return sheet.SkillEntries(id => CooldownLeft(s, id, now));
    }

    private const int DoubleAttackSkill = 212, DoubleShotSkill = 252;
    private const int FireBoltSkill = 11, FirestormSkill = 126, IceShardsSkill = 42, IceBlastSkill = 221;

    /// <summary>
    /// A damage skill's hit, by Master's Grimoire's formula: ⌈base + initial × (1 + √(ability ÷ 100) + √(attribute ÷
    /// divisor))⌉ (Sharp Shot: ranged combat ability and dexterity ÷ 6), plus one weapon hit for skills that include it,
    /// plus Fire/Ice Attunement for their skills, times the passive tree's skill damage.
    /// </summary>
    private int SkillDamage(Session s, PendingCast cast, EffectModifiers buffs, int targetLevel, bool againstPlayer, Mob? mob = null, Session? player = null)
    {
        var sheet = s.Sheet!;
        var level = againstPlayer ? cast.Skill.PvpLevel(cast.Level) : cast.Skill.Level(cast.Level);
        var amount = SkillInfo.Amount(level, FormulaAbility(s, cast.Skill), sheet.StatRatio(cast.Skill));
        amount += sheet.SkillBonusDamage(cast.Skill.Id); // "increases the damage from your Sharp Shot skill by up to 1013"
        amount += cast.Skill.Id switch
        {
            IceShardsSkill or IceBlastSkill => buffs.IceSkillBoost,
            FireBoltSkill or FirestormSkill => buffs.FireSkillBoost,
            _ => 0,
        };
        // Against a monster the skill's own damage type meets its resist (Master's Grimoire's skill list gives the type;
        // skills without one aren't resisted), and a weapon hit included in the skill is resisted type by type.
        if (mob?.Combat is { } combat && _skillTypes.TryGetValue(cast.Skill.Id, out var type)) amount = combat.AfterResist(type, amount, sheet.Level, MobResistChange(mob.Effects, type));
        else if (player is not null && _skillTypes.TryGetValue(cast.Skill.Id, out var playerType))
            amount *= MobStats.ResistFactor(PlayerResist(player, playerType, Modifiers(player)), sheet.Level); // PvP: the target's resist
        if (cast.Skill.IncludesWeaponAttack) amount += WeaponHit(s, buffs, mob, player); // the auto-attack's weapon hit, Strength and weapon ability included
        return (int)(amount * sheet.SkillDamageFactor(cast.Skill.Id) * (againstPlayer ? 1 : LevelFactor(sheet.Level, targetLevel)));
    }

    /// <summary>
    /// The share of a hit's maximum (Master's Grimoire's damage figure) that lands, evenly between two-thirds and all of
    /// it. Measured on live (2026-10-15): a level 200 warrior's auto-attacks on Akais' Spawn, whose formula maximum is
    /// 483, landed 332-467 (69-97%), averaging 389. Skills take the same roll until they're measured; damage over time
    /// isn't rolled.
    /// </summary>
    private const double DamageRollFloor = 2.0 / 3;

    private static double DamageRoll(Session s, AttackKind kind) =>
        kind is AttackKind.Attack or AttackKind.Skill ? DamageRollFloor + Random.Shared.NextDouble() * (1 - DamageRollFloor) : 1;

    /// <summary>
    /// Whether a monster evades a player's skill (Master's Grimoire): it lands with |E × m ÷ (E + 50 × player level) − 1|,
    /// where E is the monster's evasion to the skill's avoidance type and m depends on the caster's class (0.95 Warrior and
    /// Rogue, 0.7 the others). Skills without an avoidance type always land.
    /// </summary>
    private static bool MobEvadesSkill(Session s, Mob mob, SkillInfo skill)
    {
        if (skill.AvoidanceType < 0 || mob.Combat is not { } c) return false;
        var now = NetTime.Now;
        var evade = c.Evade.GetValueOrDefault(skill.AvoidanceType)
            + mob.Effects.Where(e => e.Type == EffectType.ChangeEvasions && e.ExpiresAt > now).Sum(e => e.Amount);
        if (evade <= 0) return false;
        var lands = Math.Abs(evade * SkillMissMultiplier(s) / (evade + 50.0 * s.Sheet!.Level) - 1);
        return Random.Shared.NextDouble() >= lands;
    }

    /// <summary>Master's Grimoire's "Skill Miss Multiplier" for the caster's class: 0.95 Warrior and Rogue, 0.7 the others.</summary>
    private static double SkillMissMultiplier(Session caster) =>
        (CharacterClass)caster.Character!.Class is CharacterClass.Warrior or CharacterClass.Rogue ? 0.95 : 0.7;

    /// <summary>
    /// A player's evasion against a skill: the defensive ability for its avoidance type (Fortitude, Warding, Reflex,
    /// Vigour, Willpower), gear avoidance, and evasion buffs (Bless). 0 for skills without an avoidance type.
    /// </summary>
    private static int PlayerEvasion(Session target, SkillInfo skill, EffectModifiers m)
    {
        var ability = EvasionAbility(skill.AvoidanceType);
        return ability < 0 ? 0 : Math.Max(0, target.Sheet!.AbilityLevel(ability) + target.Sheet.Avoidance(skill.AvoidanceType) + m.Evasion);
    }

    /// <summary>
    /// Whether a player evades another player's skill (Master's Grimoire, PvP): lands with |E × m ÷ (E + 50 × caster
    /// level) − 1|, never under 70%, E being the target's evasion (<see cref="PlayerEvasion"/>) and m the caster's class
    /// multiplier.
    /// </summary>
    private bool PlayerEvadesSkill(Session caster, Session target, SkillInfo skill)
    {
        var evasion = PlayerEvasion(target, skill, Modifiers(target));
        if (evasion <= 0) return false;
        var lands = Math.Max(0.7, Math.Abs(evasion * SkillMissMultiplier(caster) / (evasion + 50.0 * caster.Sheet!.Level) - 1));
        return Random.Shared.NextDouble() >= lands;
    }

    /// <summary>Skill id → damage type (scripts/import-grimoire.py writes skill-types.json beside the balance JSON).</summary>
    private static Dictionary<int, int> LoadSkillTypes(string? path)
    {
        var types = new Dictionary<int, int>();
        if (path is null || !File.Exists(path)) return types;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var e in doc.RootElement.GetProperty("skills").EnumerateObject()) types[int.Parse(e.Name)] = e.Value.GetInt32();
        return types;
    }

    /// <summary>A skill with no damage of its own, no weapon hit and no gear damage bonus: it only applies its effect.</summary>
    /// <summary>Owner's rule (2026-10-17): Taunt and Warcry only build threat; they never damage the monster.</summary>
    private const int TauntSkill = 33, WarcrySkill = 128;

    private static bool DealsNoDamage(Session s, PendingCast cast)
    {
        if (cast.Skill.Id is TauntSkill or WarcrySkill) return true;
        var level = cast.Skill.Level(cast.Level);
        return level.InitialDamage == 0 && level.BaseDamage == 0 && !cast.Skill.IncludesWeaponAttack
            && cast.Skill.Id is not (DoubleAttackSkill or DoubleShotSkill) && s.Sheet!.SkillBonusDamage(cast.Skill.Id) == 0;
    }

    /// <summary>The ability level in the skill formula: the skill's ability, or 0 for skills without one.</summary>
    private float FormulaAbility(Session s, SkillInfo skill) => skill.AbilityId >= 0 ? AbilityLevelFor(s, skill) : 0;

    /// <summary>
    /// The level factor on a player's auto-attacks and skills against a monster. A higher monster takes 4% less per level it
    /// is above the player, down to 10% (Master's Grimoire). A lower one, by the owner's table (2026-10-16): full damage up
    /// to 50 levels above it, 5% less at 51-70, 10% less at 71-100 and 15% less from 101. Damage over time doesn't use it:
    /// only resists cut it.
    /// </summary>
    /// <summary>
    /// Owner's rule (2026-10-16): damage a monster deals to a player well below it in level. Within 15 levels it's full;
    /// 16-69 levels above the monster, the player takes 10% less; 70 or more, 20% less (the most it goes). Monsters at or
    /// above the player's level are unchanged. Applies to auto-attacks, monster skills and their damage over time.
    /// </summary>
    public static double IncomingLevelFactor(int playerLevel, int mobLevel) => (playerLevel - mobLevel) switch
    {
        <= 15 => 1,
        <= 69 => 0.90,
        _ => 0.80,
    };

    public static double LevelFactor(int playerLevel, int mobLevel)
    {
        if (playerLevel < mobLevel) return Math.Max(0.1, 1 - 0.04 * (mobLevel - playerLevel));
        return (playerLevel - mobLevel) switch
        {
            <= 50 => 1,
            <= 70 => 0.95,
            <= 100 => 0.90,
            _ => 0.85,
        };
    }

    /// <summary>
    /// Double Attack and Double Shot: one weapon hit (the auto-attack formula), doubled against targets up to the rank's
    /// level cap and normal above it. The cap is the rank's base amount, the &lt;IDA&gt; in "Opponents above level &lt;IDA&gt;
    /// will only take normal damage" (15 at rank 1, +5 a rank). Skills never miss here, as the description asks.
    /// Null for every other skill.
    /// </summary>
    private int? DoubleHitDamage(Session s, PendingCast cast, int targetLevel)
    {
        if (cast.Skill.Id is not (DoubleAttackSkill or DoubleShotSkill)) return null;
        double hit = AutoAttackDamage(s, cast.Target, cast.PlayerTarget);
        var level = cast.Skill.Level(cast.Level);
        var cap = (int)Math.Round(Math.Abs(level.InitialDamage) + Math.Abs(level.BaseDamage));
        return (int)(hit * (targetLevel <= cap ? 2 : 1));
    }

    /// <summary>"SwordTwoHanded" → "a two-handed sword", for "Double Shot needs a bow."</summary>
    private static string WeaponName(string subtype) => subtype switch
    {
        "None" => "no weapon",
        "HandToHand" => "a hand-to-hand weapon",
        "SwordTwoHanded" => "a two-handed sword",
        "AxeTwoHanded" => "a two-handed axe",
        "BluntTwoHanded" => "a two-handed blunt weapon",
        "Axe" => "an axe",
        "NoveltyLute" => "a lute",
        "NoveltyFlute" => "a flute",
        "FishingRod" => "a fishing rod",
        _ => "a " + subtype.ToLowerInvariant(),
    };

    private void OnCancelSkill(Session s, uint skillId)
    {
        s.Casts.RemoveAll(c => c.Skill.Id == (int)skillId);
        Send(s, Opcode.SkillUpdate, new SkillUpdate((int)skillId, ShownRank(s, (int)skillId), CooldownLeft(s, (int)skillId, NetTime.Now)).Write);
    }

    private void ResolveCast(Zone zone, Session s, PendingCast cast, double now)
    {
        var level = cast.Skill.Level(cast.Level);
        var sheet = s.Sheet!;
        var cost = sheet.EnergyCost(cast.Skill.Id, level.EnergyCost);
        if (s.Energy < cost || (cast.Target is { } t && t.State == MobState.Dead))
        {
            Send(s, Opcode.SkillUpdate, new SkillUpdate(cast.Skill.Id, cast.Level, CooldownLeft(s, cast.Skill.Id, now)).Write);
            return;
        }
        s.Energy -= cost;
        var recharge = (float)(level.RechargeTime * sheet.RechargeFactor(cast.Skill.Id)); // passives can shorten it
        s.Cooldowns[cast.Skill.Id] = now + recharge;
        Send(s, Opcode.SkillUpdate, new SkillUpdate(cast.Skill.Id, cast.Level, Math.Max(0.5f, recharge)).Write);
        Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.Energy, s.Energy)]).Write);

        var amount = Math.Abs(level.InitialDamage) + Math.Abs(level.BaseDamage);
        TrainCombatAbility(s, cast.Skill.AbilityId); // the skill's own ability (Ranged Combat, Nature Magic, Fire Magic…)
        var abilityLevel = AbilityLevelFor(s, cast.Skill);
        var statRatio = sheet.StatRatio(cast.Skill);
        // Buffs scale with ability and attribute too; skills without an ability use none (Reapers Touch stays 100).
        var buffAbility = cast.Skill.AbilityId >= 0 ? abilityLevel : 0f;
        var buffs = Modifiers(s);
        if (cast.PlayerTarget is { } enemy)
        {
            var damage = DoubleHitDamage(s, cast, enemy.Sheet!.Level) ?? SkillDamage(s, cast, buffs, enemy.Sheet!.Level, againstPlayer: true, player: enemy);
            if (Enemy(s, enemy.CharacterId) is not null)
            {
                // Another player's skill trains the defensive ability for its avoidance type, as monsters' skills do.
                if (EvasionAbility(cast.Skill.AvoidanceType) is >= 0 and var guard) TrainCombatAbility(enemy, guard);
                if (PlayerEvadesSkill(s, enemy, cast.Skill))
                    SendPlayerDamage(zone, enemy, new DamageEntry(DamageDirection.PlayerToPlayer, s.CharacterId, enemy.CharacterId, AttackKind.Skill,
                        cast.Skill.Id, 0, enemy.Health, Reaction.SkillAvoided, ++_damageId, false));
                else
                {
                    if (!PlayerHitsPlayer(zone, s, enemy, AttackKind.Skill, cast.Skill.Id, Math.Max(1, damage), now, allowMiss: false))
                        SystemLine(s, $"{cast.Skill.Name} Succeeded");
                    // The skill's effect lands on them too: bleeds, poisons and burns (ticking as PvP damage), slows,
                    // stuns, lures and other debuffs.
                    if (cast.Skill.StatusEffectId > 0 && !enemy.Dead) ApplyEffect(enemy, cast.Skill, cast.Level, now, buffAbility, statRatio, caster: s);
                }
            }
            return;
        }
        if (cast.Target is { } mob)
        {
            QuestCast(s, cast.Skill.Id, mob);
            // Area skills hit every hostile monster around the target too (the template's AreaOfEffect radius).
            var victims = new List<Mob> { mob };
            if (cast.Skill.AreaOfEffect > 0)
            {
                var (cx, cz) = mob.PositionAt(now);
                victims.AddRange(zone.Mobs.Values.Where(m => !ReferenceEquals(m, mob) && !m.IsNpc && m.State != MobState.Dead && m.Opinion < 50)
                    .Select(m => (Mob: m, Pos: m.PositionAt(now))).Where(m => Dist2D(m.Pos.X, m.Pos.Z, cx, cz) <= cast.Skill.AreaOfEffect)
                    .Select(m => m.Mob).Take(MaxAreaTargets - 1));
            }
            var (landed, crit) = (false, false);
            foreach (var victim in victims)
            {
                if (victim.State == MobState.Dead) continue;
                if (MobEvadesSkill(s, victim, cast.Skill))
                {
                    SystemLine(s, $"{victim.Template.Name} withstands {cast.Skill.Name}"); // live wording (2026-10-16)
                    if (!OwnsOrShares(s, victim) && !OwnerStillHere(zone, victim)) SetMobOwner(zone, victim, s);
                    if (victim.State is MobState.Idle or MobState.Returning) Engage(zone, victim, s, now);
                    Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([new DamageEntry(DamageDirection.PlayerToMob, s.CharacterId, (int)victim.ServerId,
                        AttackKind.Skill, cast.Skill.Id, 0, victim.Health, Reaction.SkillAvoided, ++_damageId, false)], []).Write);
                    continue;
                }
                landed = true;
                // Each monster resists for itself (an area skill can hit a mix).
                if (DealsNoDamage(s, cast))
                {
                    // Howling Wind and other debuff-only skills: the debuff below, aggro, and no damage number.
                    if (!OwnsOrShares(s, victim) && !OwnerStillHere(zone, victim)) SetMobOwner(zone, victim, s);
                    if (victim.State is MobState.Idle or MobState.Returning) Engage(zone, victim, s, now);
                    // Taunt and Warcry: what would have been their damage is threat instead (the owner's rule, 2026-10-17).
                    if (cast.Skill.Id is TauntSkill or WarcrySkill)
                        AddThreat(victim, s, SkillDamage(s, cast, buffs, victim.Level, againstPlayer: false, victim) * AggroFactor(sheet.Level, victim.Level)
                            * _threat.ClassMultiplier(s.Character!.Class) * _threat.SkillDamageMultiplier(cast.Skill.Id));
                    Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([new DamageEntry(DamageDirection.PlayerToMob, s.CharacterId, (int)victim.ServerId,
                        AttackKind.Skill, cast.Skill.Id, 0, victim.Health, Reaction.SkillHitAggressive, ++_damageId, false)], []).Write);
                }
                else
                {
                    var damage = DoubleHitDamage(s, cast, victim.Level) ?? SkillDamage(s, cast, buffs, victim.Level, againstPlayer: false, victim);
                    if (_divineStrike.Offhand(cast.Skill.Id, Random.Shared) is { } fury) damage = fury; // Dragonlord Fury: divine-strike.json
                    if (DivineStrikeDamage.DivineOnlySkills.Contains(cast.Skill.Id) && !_spawnTuning.TakesDivine(victim.Template.Id)) damage = 0;
                    if (damage > 0) crit |= PlayerHits(zone, s, victim, AttackKind.Skill, cast.Skill.Id, damage, now, allowMiss: false);
                }
                AddThreat(victim, s, SkillAggro(cast.Skill.Id, cast.Level) * AggroFactor(sheet.Level, victim.Level)); // Shield Bash, Taunt…
                if (cast.Skill.StatusEffectId > 0 && victim.State != MobState.Dead
                    && (!DivineStrikeDamage.DivineOnlySkills.Contains(cast.Skill.Id) || _spawnTuning.TakesDivine(victim.Template.Id))) // Spite: divine monsters only
                    ApplyMobEffect(victim, s, cast.Skill, cast.Level, now, buffAbility, statRatio);
            }
            if (landed && !crit) SystemLine(s, $"{cast.Skill.Name} Succeeded");
            return;
        }
        QuestCast(s, cast.Skill.Id, null);
        // Who a friendly skill lands on: the chosen player, the caster, or (group skills) the caster and party members in reach.
        var recipients = new List<Session>();
        if (cast.Friend is { } friend)
        {
            if (friend.InZone && !friend.Dead && friend.Position.Zone == s.Position.Zone) recipients.Add(friend);
        }
        else
        {
            recipients.Add(s);
            if (cast.Skill.CastTarget == "Group" && s.Party is { } party)
            {
                var reach = Math.Max(cast.Skill.AreaOfEffect, GroupSkillMinReach);
                recipients.AddRange(party.Members.Where(p => !ReferenceEquals(p, s) && p.InZone && !p.Dead && p.Position.Zone == s.Position.Zone
                    && Dist2D(p.Position.X, p.Position.Z, s.Position.X, s.Position.Z) <= reach));
            }
        }
        var heals = level.InitialDamage < 0 || level.BaseDamage < 0;
        var healed = false;
        foreach (var r in recipients)
        {
            HelpThreat(zone, s, r, cast.Skill, cast.Level);
            if (cast.Skill.StatusEffectId > 0) ApplyEffect(r, cast.Skill, cast.Level, now, buffAbility, statRatio);
            if (!heals) continue;
            // Gear healing bonuses add to the heal (Bracelets of Natures Touch, Light Heal…).
            var heal = Math.Max(1, (int)((SkillInfo.Amount(level, buffAbility, statRatio) + sheet.SkillBonusDamage(cast.Skill.Id)) * sheet.HealFactor(cast.Skill.Id)));
            _store.AddStat(s.Character!.Id, "heal", Math.Min(heal, r.MaxHealth - r.Health));
            r.Health = Math.Min(r.MaxHealth, r.Health + heal);
            var entry = new DamageEntry(DamageDirection.PlayerToPlayer, s.CharacterId, r.CharacterId, AttackKind.Skill, cast.Skill.Id,
                -heal, r.Health, Reaction.SkillHitPositive, ++_damageId, false);
            SendPlayerDamage(zone, r, entry);
            healed = true;
        }
        if (healed) TrainCombatAbility(s, CriticalSkills); // a heal on yourself or others can train Critical Skills, once a cast
        if (recipients.Count > 0) SystemLine(s, $"{cast.Skill.Name} Succeeded");
    }

    /// <summary>At most this many monsters take an area skill (the target included).</summary>
    private const int MaxAreaTargets = 10;

    /// <summary>Group skills without a radius still reach party members standing close by.</summary>
    private const float GroupSkillMinReach = 8f;

    private bool PlayerHits(Zone zone, Session s, Mob mob, AttackKind kind, int skillId, int baseDamage, double now, bool allowMiss = true)
    {
        if (mob.IsFishingSpot)
        {
            if (kind is AttackKind.Attack or AttackKind.Skill) FishHit(zone, s, mob, kind, skillId, now);
            return false;
        }
        s.LastCombatAt = now;
        BreakStealth(s);
        var roll = Random.Shared.NextDouble();
        var reaction = kind is AttackKind.Skill or AttackKind.StatusEffect ? Reaction.SkillHitAggressive : Reaction.Hit;
        var critical = false;
        int damage;
        if (allowMiss && kind == AttackKind.Attack && roll >= HitChance(s, mob))
        {
            reaction = Reaction.Miss;
            damage = 0;
        }
        else
        {
            damage = (int)Math.Round(baseDamage * DamageRoll(s, kind));
            if (Random.Shared.NextDouble() < CritChance(s, mob, kind, skillId))
            {
                critical = true;
                damage = (int)(damage * s.Sheet.CritMultiplier);
            }
            damage = Math.Max(1, damage);
            if (critical) PartyLine(s, CritLine(s, kind, skillId, damage));
        }
        mob.Health -= damage;
        if (mob.Scripts.HitsTaken.Count < 64) mob.Scripts.HitsTaken.Add((kind == AttackKind.Attack ? 0 : skillId, damage));
        LifeTap(s, damage);
        if (!OwnsOrShares(s, mob) && !OwnerStillHere(zone, mob)) SetMobOwner(zone, mob, s);
        // An auto-attack that lands trains the weapon's ability (Bow, Sword…); Melee and Ranged Combat come from the skills
        // that list them.
        if (damage > 0 && kind == AttackKind.Attack) TrainCombatAbility(s, WeaponAbility(Weapon(s)));
        // Owner's rule (2026-10-15): every auto-attack that lands can train Critical Strike and every skill hit Critical
        // Skills, crit or not (misses, evades and damage-over-time ticks don't count).
        if (damage > 0 && kind is AttackKind.Attack or AttackKind.Skill) TrainCombatAbility(s, kind == AttackKind.Attack ? CriticalStrike : CriticalSkills);
        mob.Damage[s] = mob.Damage.GetValueOrDefault(s) + damage;
        // threat.json: threat per point of damage, times the class's and (for skills and their DoTs) the skill's multiplier.
        AddThreat(mob, s, damage * _threat.DamageMultiplier * _threat.ClassMultiplier(s.Character!.Class)
            * (kind == AttackKind.Attack ? 1 : _threat.SkillDamageMultiplier(skillId)) * AggroFactor(s.Sheet!.Level, mob.Level));
        if (damage > 0 && kind is AttackKind.Attack or AttackKind.Skill) _store.AddStat(s.Character!.Id, kind == AttackKind.Attack ? "melee_hit" : "skills_hit", damage);
        // Divine Strike, the weapons' proc and the auras', only lands on monsters spawn-tuning.json marks "divine".
        var takesDivine = _spawnTuning.TakesDivine(mob.Template.Id);
        var procs = kind == AttackKind.Attack && damage > 0 && mob.Health > 0
            ? RollProcs(s).Where(p => takesDivine || p.Skill.Id != SetBonusCatalog.DivineStrikeSkill).ToList() : [];
        var divineStrike = kind == AttackKind.Attack && damage > 0 && s.Sheet!.DivineDamage > 0 && takesDivine
            && Random.Shared.NextDouble() < _balance.SetBonuses.DivineStrikeChance;
        var entry = new DamageEntry(DamageDirection.PlayerToMob, s.CharacterId, (int)mob.ServerId, kind, skillId, damage, mob.Health, reaction, ++_damageId, critical);
        Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([entry], [new HealthUpdate(HealthUpdate.Mob, (int)mob.ServerId, Math.Max(0, mob.Health))]).Write);
        if (mob.Health <= 0) KillMob(zone, mob, s, now);
        else if (mob.State is MobState.Idle or MobState.Returning) Engage(zone, mob, s, now);
        // Armour set auras: Divine Strike for the aura's divine damage (server/content/set-bonuses.json).
        if (divineStrike && mob.State != MobState.Dead)
        {
            // A system chat line for everyone within 15 m of the striker, the striker included; no pop-up box, which
            // interrupted play (owner's rules, 2026-10-15/16).
            // Live wording (2026-10-16): the striker also gets "Divine Strike Succeeded".
            SystemLine(s, "Divine Strike Succeeded");
            var glow = $"{s.Name}'s weapon glows with a Divine light.";
            foreach (var p in zone.Players.Where(p => p.InZone && Dist2D(p.Position.X, p.Position.Z, s.Position.X, s.Position.Z) <= DivineLightRange))
                SystemLine(p, glow);
            // divine-strike.json: a random amount between the aura's min and max; else set-bonuses.json's divine.
            var auraDamage = _divineStrike.Aura(s.Sheet!.SetBonuses, Random.Shared) ?? s.Sheet!.DivineDamage;
            PlayerHits(zone, s, mob, AttackKind.AttackTriggeredSkill, SetBonusCatalog.DivineStrikeSkill, auraDamage, now, allowMiss: false);
        }
        foreach (var (proc, level) in procs)
        {
            if (mob.State == MobState.Dead) break;
            var lvl = proc.Level(level);
            var procDamage = (int)(Math.Abs(lvl.InitialDamage) + Math.Abs(lvl.BaseDamage));
            if (proc.Id == SetBonusCatalog.DivineStrikeSkill && _divineStrike.Weapon(level, Random.Shared) is { } divine) procDamage = divine; // divine-strike.json
            if (procDamage > 0) PlayerHits(zone, s, mob, AttackKind.AttackTriggeredSkill, proc.Id, procDamage, now, allowMiss: false);
            if (proc.StatusEffectId > 0 && mob.State != MobState.Dead)
            {
                if (proc.CastTarget == "Enemy") ApplyMobEffect(mob, s, proc, level, now);
                else ApplyEffect(s, proc, level, now);
            }
        }
        return critical;
    }

    /// <summary>Life Tap keystone: 5% of damage dealt heals the attacker.</summary>
    private void LifeTap(Session s, int damage)
    {
        if (damage <= 0 || s.Dead || !s.Sheet!.Tree.Has(PassiveTree.LifeTap) || s.Health >= s.MaxHealth) return;
        s.Health = Math.Min(s.MaxHealth, s.Health + Math.Max(1, damage / 20));
        Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.Health, s.Health)]).Write);
    }

    /// <summary>Equipment procs (ProcSkillID at ProcSkillChance) that trigger on this hit.</summary>
    private List<(SkillInfo Skill, int Level)> RollProcs(Session s) =>
        s.Sheet!.Gear.Where(g => g.ProcSkillId > 0 && g.ProcSkillChance > 0 && Random.Shared.NextDouble() < g.ProcSkillChance)
            .Select(g => (Skill: _balance.Skills.GetValueOrDefault(g.ProcSkillId), g.ProcSkillLevel))
            .Where(p => p.Skill is not null).Select(p => (p.Skill!, p.ProcSkillLevel)).ToList();

    private void MobHits(Zone zone, Mob mob, Session target, double now)
    {
        if (_brains.TryGet(mob.Template.Id, out var brain))
        {
            if (CurrentAi(mob, brain).Decisions.Count == 0) return; // "none" and "inanimate" AI: only its scripts act
            if (ChooseMobSkill(mob, brain, now) is { } choice && _balance.Skills.TryGetValue(choice.SkillId, out var mobSkill))
            {
                StartMobCast(zone, mob, [target], mobSkill, choice, brain, now);
                return;
            }
        }
        target.LastCombatAt = now;
        BreakStealth(target);
        var m = Modifiers(target);
        var sheet = target.Sheet!;
        var miss = !sheet.Tree.Has(PassiveTree.IronHide) && Random.Shared.NextDouble() < MobMissChance(mob, sheet, m);
        var damage = miss ? 0 : MobHitDamage(mob, target, m);
        // Monsters never crit (owner's rule, 2026-10-15). Those without Grimoire skills only auto-attack, as the data has it.
        var absorbed = damage - AbsorbWithShields(target, damage);
        target.Health = Math.Max(0, target.Health - (damage - absorbed));
        var entry = new DamageEntry(DamageDirection.MobToPlayer, (int)mob.ServerId, target.CharacterId, AttackKind.Attack, 0, damage, target.Health,
            miss ? AvoidedSwing() : Reaction.Hit, ++_damageId, false);
        SendPlayerDamage(zone, target, entry);
        if (!miss && damage > 0) MaybeInterrupt(target, now);
        if (target.Health <= 0)
        {
            PlayerDies(zone, target, now);
            return;
        }
        if (!miss && m.DamageShield > 0 && mob.State != MobState.Dead)
        {
            var shield = target.Effects.First(e => e.Type == EffectType.DamageShield);
            PlayerHits(zone, target, mob, AttackKind.StatusEffect, shield.Info.Id, m.DamageShield, now, allowMiss: false);
        }
        // Dragonlord/Eldritch Vengeance: strikes back only at monsters vulnerable to Divine, for the owner's range.
        if (!miss && m.DivineDamageShield > 0 && mob.State != MobState.Dead && _spawnTuning.TakesDivine(mob.Template.Id))
        {
            var vengeance = target.Effects.First(e => e.Type == EffectType.DamageShield && DivineStrikeDamage.DivineOnlyEffects.Contains(e.Info.Id));
            PlayerHits(zone, target, mob, AttackKind.StatusEffect, vengeance.Info.Id, _divineStrike.Offhand(vengeance.Info.Id, Random.Shared) ?? m.DivineDamageShield, now, allowMiss: false);
        }
    }

    /// <summary>
    /// How an avoided monster swing shows: miss, dodge or block, evenly. They're one roll (attack against defence); live
    /// (2026-10-15, 66 swings from a level 140 Dragoncult Sorceror on a 2,650-defence warrior) landed 33% against the
    /// formula's 34.5%, and the rest split 13 miss, 16 dodge, 15 block.
    /// </summary>
    private static Reaction AvoidedSwing() => Random.Shared.Next(3) switch { 0 => Reaction.Miss, 1 => Reaction.Dodge, _ => Reaction.Block };

    /// <summary>
    /// The monster's next action by its AI weights (Master's Grimoire): auto-attack (null), or a ready skill of the kind
    /// chosen. A kind only counts when the monster has a ready skill of it; healing itself only below its threshold.
    /// Buffs and healing others aren't modelled yet, so those weights are left out.
    /// </summary>
    private MobSkillChoice? ChooseMobSkill(Mob mob, MobBrain brain, double now)
    {
        var ai = CurrentAi(mob, brain);
        var known = brain.SkillsFor(ai);
        List<MobSkillChoice> Ready(int kind) => known
            .Where(s => s.Kind == kind && s.Weight > 0 && mob.SkillReadyAt.GetValueOrDefault(s.SkillId) <= now).ToList();
        var options = new List<(int Weight, List<MobSkillChoice>? Skills)>();
        foreach (var (kind, weight) in ai.Decisions)
        {
            if (weight <= 0) continue;
            switch (kind)
            {
                case MobBrains.AutoAttack: options.Add((weight, null)); break;
                case MobBrains.AttackSkill when Ready(MobBrains.KindAttack) is { Count: > 0 } a: options.Add((weight, a)); break;
                case MobBrains.DebuffSkill when Ready(MobBrains.KindDebuff) is { Count: > 0 } d: options.Add((weight, d)); break;
                case MobBrains.HealSelf when mob.Health * 100 < ai.SelfHealBelow * mob.MaxHealth && Ready(MobBrains.KindHeal) is { Count: > 0 } h:
                    options.Add((weight, h));
                    break;
            }
        }
        if (options.Count == 0) return null;
        var roll = Random.Shared.NextDouble() * options.Sum(o => o.Weight);
        foreach (var (weight, skills) in options)
        {
            if ((roll -= weight) >= 0) continue;
            if (skills is null) return null;
            var pick = Random.Shared.NextDouble() * skills.Sum(s => s.Weight);
            foreach (var s in skills)
                if ((pick -= s.Weight) < 0) return s;
            return skills[^1];
        }
        return null;
    }

    /// <summary>
    /// A monster skill on a player, and on everyone else within its area (Caustic Spray's 7 m) around them. Heals only
    /// go on itself once.
    /// </summary>
    /// <summary>A monster skill being cast: it lands on its targets at <see cref="ResolveAt"/>.</summary>
    private sealed record PendingMobCast(SkillInfo Skill, MobSkillChoice Choice, MobBrain Brain, IReadOnlyList<Session> Targets, double ResolveAt);

    /// <summary>
    /// Seconds between a monster's "started casting" line and its skill landing (live, 2026-10-16: about a second and a
    /// half from the pop-up to the cast and its damage).
    /// </summary>
    public const double MobCastSeconds = 1.5;

    /// <summary>
    /// A monster starts casting: "Witchfinder Conrad started casting Shackles." for everyone nearby (live wording), and
    /// <see cref="MobCastSeconds"/> later the skill lands with "The Far-Seeing One cast Swift Tackle on you" for each player
    /// it hits. The skill's recharge starts now so it isn't chosen again meanwhile. A dead monster's scripted skills
    /// (death throes) land at once.
    /// </summary>
    private void StartMobCast(Zone zone, Mob mob, IReadOnlyList<Session> targets, SkillInfo skill, MobSkillChoice choice, MobBrain brain, double now)
    {
        var cast = new PendingMobCast(skill, choice, brain, targets, now + MobCastSeconds);
        if (mob.State == MobState.Dead || mob.PendingCast is not null)
        {
            ResolveMobCast(zone, mob, cast, now); // no time to cast (or already casting): it lands now
            return;
        }
        mob.SkillReadyAt[skill.Id] = now + Math.Max(1, skill.Level(choice.Rank).RechargeTime);
        var started = $"{mob.Template.Name} started casting {skill.Name}.";
        var (mx, mz) = mob.PositionAt(now);
        foreach (var p in zone.Players.Where(p => p.InZone && Dist2D(p.Position.X, p.Position.Z, mx, mz) <= MobCastLineRange).ToList())
            SystemLine(p, started);
        if (mob.IsMoving(now))
        {
            mob.StopAt(now);
            BroadcastLeg(zone, mob);
        }
        mob.PendingCast = cast;
    }

    /// <summary>The cast lands on its targets still in the zone and alive.</summary>
    private void ResolveMobCast(Zone zone, Mob mob, PendingMobCast cast, double now)
    {
        foreach (var t in cast.Targets.Where(t => t.InZone && !t.Dead && zone.Players.Contains(t)).ToList())
            MobCastsSkillArea(zone, mob, t, cast.Skill, cast.Choice, cast.Brain, now);
    }

    private void MobCastsSkillArea(Zone zone, Mob mob, Session target, SkillInfo skill, MobSkillChoice choice, MobBrain brain, double now)
    {
        MobCastsSkill(zone, mob, target, skill, choice, brain, now);
        if (skill.AreaOfEffect <= 0 || choice.Kind == MobBrains.KindHeal) return;
        foreach (var other in zone.Players.Where(p => !ReferenceEquals(p, target) && p.InZone && !p.Dead && !IsHiddenFrom(p, mob)
                     && Dist2D(p.Position.X, p.Position.Z, target.Position.X, target.Position.Z) <= skill.AreaOfEffect).ToList())
            MobCastsSkill(zone, mob, other, skill, choice, brain, now);
    }

    /// <summary>How far from a casting monster players see its "started casting" line (metres).</summary>
    private const float MobCastLineRange = 40f;

    /// <summary>Defensive ability that evades (and trains from) each avoidance type, by the owner's mapping.</summary>
    private static int EvasionAbility(int avoidanceType) => avoidanceType switch
    {
        0 => Fortitude,   // physical
        1 => Warding,     // spell
        2 => Reflex,      // movement
        3 => Vigour,      // wounding (damage over time)
        4 or 5 => Willpower, // weakening (and the rare mental)
        _ => -1,
    };

    /// <summary>
    /// A monster casts one of its skills on a player. Heals restore its own health. Attack and debuff skills can be
    /// evaded: they land with |E × 0.7 ÷ (E + 50 × monster level) − 1| (Master's Grimoire), where E is the player's
    /// matching defensive ability plus gear avoidance; every cast also has the combat chance to train that ability.
    /// Damage is the skill formula at the monster's own rank and ability level (plus its weapon hit for skills that
    /// include one), cut by the player's resist to the skill's type; the skill's status effect (slows, roots, damage
    /// over time, weakening) lands with it.
    /// </summary>
    private void MobCastsSkill(Zone zone, Mob mob, Session target, SkillInfo skill, MobSkillChoice choice, MobBrain brain, double now)
    {
        var level = skill.Level(choice.Rank);
        var ability = skill.AbilityId >= 0 ? brain.Abilities.GetValueOrDefault(skill.AbilityId) : 0;
        mob.SkillReadyAt[skill.Id] = now + Math.Max(1, level.RechargeTime);
        if (choice.Kind == MobBrains.KindHeal)
        {
            mob.Health = Math.Min(mob.MaxHealth, mob.Health + (int)SkillInfo.Amount(level, ability, 0));
            Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([], [new HealthUpdate(HealthUpdate.Mob, (int)mob.ServerId, mob.Health)]).Write);
            return;
        }
        target.LastCombatAt = now;
        BreakStealth(target);
        SystemLine(target, $"{mob.Template.Name} cast {skill.Name} on you");
        if ((skill.Id == FingalsBaneSkill || skill.StatusEffectId == FingalsBaneEffect) && HasFingalsShield(target))
        {
            SystemLine(target, "Fingals Shield protects you from Fingals Bane.");
            return;
        }
        var sheet = target.Sheet!;
        var m = Modifiers(target);
        var defence = EvasionAbility(skill.AvoidanceType);
        if (defence >= 0) TrainCombatAbility(target, defence);
        var evasion = PlayerEvasion(target, skill, m);
        var lands = evasion <= 0 ? 1 : Math.Abs(evasion * 0.7 / (evasion + 50.0 * mob.Level) - 1);
        if (Random.Shared.NextDouble() >= lands)
        {
            SendPlayerDamage(zone, target, new DamageEntry(DamageDirection.MobToPlayer, (int)mob.ServerId, target.CharacterId, AttackKind.Skill, skill.Id,
                0, target.Health, Reaction.SkillAvoided, ++_damageId, false));
            return;
        }
        // Rolled like players' skills: live Stonecrusher (2026-10-15) hit Death Wave for 804 and 664 of its 917, Furious
        // Sweep for 627 of 682.
        var amount = SkillInfo.Amount(level, ability, 0) * (DamageRollFloor + Random.Shared.NextDouble() * (1 - DamageRollFloor));
        if (amount > 0 && _skillTypes.TryGetValue(skill.Id, out var type) && type != 9)
            amount *= MobStats.ResistFactor(PlayerResist(target, type, m), mob.Level);
        amount *= IncomingLevelFactor(target.Sheet!.Level, mob.Level); // the weapon part below has it already
        if (skill.IncludesWeaponAttack) amount += MobHitDamage(mob, target, m);
        var damage = (int)Math.Round(amount);
        if (damage > 0)
        {
            var absorbed = damage - AbsorbWithShields(target, damage);
            target.Health = Math.Max(0, target.Health - (damage - absorbed));
        }
        SendPlayerDamage(zone, target, new DamageEntry(DamageDirection.MobToPlayer, (int)mob.ServerId, target.CharacterId, AttackKind.Skill, skill.Id,
            damage, target.Health, Reaction.SkillHitAggressive, ++_damageId, false));
        if (damage > 0) MaybeInterrupt(target, now);
        if (target.Health <= 0)
        {
            PlayerDies(zone, target, now);
            return;
        }
        if (skill.StatusEffectId > 0) ApplyEffect(target, skill, choice.Rank, now, ability, caster: mob);
    }

    /// <summary>
    /// Chance a monster's swing misses: it hits with attack ÷ (attack + defence), Master's Grimoire's formula, so the
    /// player's defence (gear, level, dexterity, buffs) decides it. Evasion from the passive tree adds to the miss chance.
    /// Monsters without official numbers use an approximated attack.
    /// </summary>
    private static double MobMissChance(Mob mob, CharacterSheet sheet, EffectModifiers m)
    {
        double attack = Math.Max(0, (mob.Combat is { Attack: > 0 } c ? c.Attack : MobStats.Attack(mob.Level, mob.Template.Power)) + MobDebuffs(mob).Attack);
        var defence = Math.Max(0, sheet.Defence + m.Defence);
        var hit = attack / (attack + defence);
        return Math.Clamp(1 - hit + sheet.EvadeChance, 0, 0.95);
    }

    /// <summary>
    /// A monster hit's damage: each of its official damage types (up to its listed amount, from half upwards), cut by the
    /// player's resist to that type with the Grimoire's 1 − R / (R + 6 + 3 × monster level). Armour counts as resist to
    /// physical damage (pierce, slash, crush). Monsters without official numbers deal the approximated amount as crush.
    /// </summary>
    private static int MobHitDamage(Mob mob, Session target, EffectModifiers m)
    {
        IReadOnlyList<(int Type, int Amount)> parts = mob.Combat is { Damage.Count: > 0 } c
            ? c.Damage : [(2, MobStats.HitDamage(mob.Level, mob.Template.Power))];
        var total = 0.0;
        foreach (var (type, amount) in parts)
        {
            var resist = PlayerResist(target, type, m);
            total += amount * (0.5 + Random.Shared.NextDouble() * 0.5) * MobStats.ResistFactor(resist, mob.Level);
        }
        return Math.Max(1, (int)Math.Round(total * IncomingLevelFactor(target.Sheet!.Level, mob.Level)));
    }

    /// <summary>
    /// Shows Chaos Aura on these monsters: OthersStatusEffect with the mob flag (byte 1, server id, status list), the
    /// same message that carries other players' buffs. Display only; the server checks <see cref="Mob.HasChaosAura"/>.
    /// </summary>
    private void SendMobEffects(Session viewer, IReadOnlyList<Mob> mobs)
    {
        if (mobs.Count == 0) return;
        var now = NetTime.Now;
        Send(viewer, Opcode.OthersStatusEffect, b =>
        {
            b.WriteVariableInt32(mobs.Count);
            foreach (var mob in mobs)
            {
                b.Write((byte)1);
                b.WriteVariableUInt32(mob.ServerId);
                var effects = mob.Effects.Where(e => e.ExpiresAt > now).ToList();
                b.WriteVariableInt32(effects.Count + (mob.HasChaosAura ? 1 : 0));
                if (mob.HasChaosAura)
                {
                    b.WriteVariableInt32(Mob.ChaosAuraEffect);
                    b.WriteVariableInt32(0);
                    b.WriteVariableInt32((int)PermanentSeconds);
                    b.Write(ActiveEffect.CasterAbilityLevel);
                    b.Write(0f);
                }
                foreach (var e in effects)
                {
                    b.WriteVariableInt32(e.Info.Id);
                    b.WriteVariableInt32(e.Info.ClientLevel(e.LevelIndex));
                    b.WriteVariableInt32((int)Math.Ceiling(e.ExpiresAt - now));
                    b.Write(e.AbilityLevel);
                    b.Write((float)e.StatRatio);
                }
            }
            b.WriteVariableInt32(0); // none hidden
        });
    }

    /// <summary>
    /// Chance an auto-attack lands on a monster: attack ÷ (attack + the monster's defence), Master's Grimoire's formula
    /// (Steady Aim's attack counts with a ranged weapon). Monsters without official numbers keep the old 92%.
    /// </summary>
    private double HitChance(Session s, Mob mob)
    {
        if (mob.Combat is not { } c) return 0.92;
        var defence = Math.Max(0, c.Defence + MobDebuffs(mob).Defence);
        if (defence <= 0) return 1;
        double attack = Math.Max(0, LiveStats(s).Attack);
        return attack / (attack + defence);
    }

    /// <summary>A line in the player's system chat, where the client also says "Ability increased to…".</summary>
    private void SystemLine(Session s, string text) => Send(s, Opcode.GeneralChat, new ChatMessage(ChatChannel.System, "", text).WriteToClient);

    /// <summary>A system line for the player and their party members in the same zone (crits show on both clients on live).</summary>
    private void PartyLine(Session s, string text)
    {
        SystemLine(s, text);
        if (s.Party is not { } party) return;
        foreach (var m in party.Members.Where(m => !ReferenceEquals(m, s) && m.InZone && m.Position.Zone == s.Position.Zone).ToList()) SystemLine(m, text);
    }

    /// <summary>
    /// The chat line for a critical hit, as live (2026-10-16): "BowJob lands a critical hit for 1908 damage!", "BowJob lands
    /// a critical Sharp Shot for 8670 damage!", and the damage-over-time effect's name for a crit tick.
    /// </summary>
    private string CritLine(Session s, AttackKind kind, int skillId, int damage)
    {
        var what = kind switch
        {
            AttackKind.Attack => "hit",
            AttackKind.Skill => _balance.Skills.TryGetValue(skillId, out var skill) ? skill.Name : "skill",
            _ => _balance.StatusEffects.TryGetValue(skillId, out var effect) ? effect.Name : "hit",
        };
        return $"{s.Name} lands a critical {what} for {damage} damage!";
    }

    /// <summary>A real damage-over-time tick (bleed, poison, burn), not a damage shield's reflect.</summary>
    private bool IsDotTick(int effectId) => _balance.StatusEffects.TryGetValue(effectId, out var info) && (EffectType)info.Type == EffectType.Dot;

    /// <summary>
    /// Critical hit chance against a monster (Master's Grimoire): auto-attacks 8% × (critical strike + 40) ÷ (critical
    /// strike + 40 + 10 × (monster level + 3)), skills and damage-over-time ticks the same with 12% and critical skills
    /// (owner's rule for ticks; damage-shield reflects don't). Procs don't crit. The passive tree's crit bonuses (when it's on) add on top.
    /// </summary>
    private double CritChance(Session s, Mob mob, AttackKind kind, int skillId = 0)
    {
        var sheet = s.Sheet!;
        var (baseChance, ability) = kind switch
        {
            AttackKind.Attack => (0.08, CriticalStrike),
            AttackKind.Skill => (0.12, CriticalSkills),
            AttackKind.StatusEffect when IsDotTick(skillId) => (0.12, CriticalSkills), // owner's rule (2026-10-15): damage-over-time ticks crit like skills
            _ => (0.0, -1),
        };
        if (ability < 0) return 0;
        var level = Math.Max(0, sheet.AbilityLevel(ability));
        var chance = baseChance * (level + 40.0) / (level + 40.0 + 10 * (mob.Level + 3));
        return (chance + sheet.Tree["critChance"] / 100) * (sheet.Tree.Has(PassiveTree.AssassinsMark) ? 2 : 1);
    }

    private void KillMob(Zone zone, Mob mob, Session killer, double now)
    {
        if (mob.AttackAnnounced && mob.Target is Session t)
            Broadcast(zone, Opcode.AttackState, new AttackState(false, true, (int)mob.ServerId, false, t.CharacterId).Write);
        mob.State = MobState.Dead;
        mob.Target = null;
        mob.PendingCast = null;
        mob.AttackAnnounced = false;
        mob.StopAt(now);
        mob.RemoveAt = now + MobCorpseSeconds;
        // Generated spawn points: spawn-tuning.json (per spawn point, per monster, live times or the default); GM spawns keep theirs.
        var respawn = mob.GeneratedCamp ? _spawnTuning.RespawnSeconds(mob.Template.Id, mob.SpawnPointId, Random.Shared) : mob.RespawnSeconds;
        mob.RespawnAt = now + Math.Max(MobCorpseSeconds + 1, respawn);
        foreach (var p in zone.Players.Where(p => ReferenceEquals(p.AttackTarget, mob)).ToList()) SetAttackTarget(p, null);
        // The tagger gets the kill (their party shares it as usual); a mob nobody tagged goes to whoever finished it.
        var rewarded = mob.Owner is Session owner && OwnerStillHere(zone, mob) ? owner : killer;
        mob.Owner = null;
        Reward(rewarded, mob);
    }

    private void Reward(Session s, Mob mob)
    {
        if (mob.IsFishingSpot)
        {
            FishingReward(s, mob);
            return;
        }
        _store.AddStat(s.Character!.Id, "kills", 1);
        if (mob.Template.Power is >= 1 and <= 6) _store.AddStat(s.Character.Id, $"star_{mob.Template.Power}", 1);
        // One gold roll; everyone sharing the kill gets it (with their own boosts), their own XP and their own dealt items,
        // each in their own kill pop-up, as on live (2026-10-16: both party members saw "0xp 100 Gold" and their own drop).
        var baseCoins = mob.Combat is { } combat
            ? Random.Shared.Next(Math.Min(combat.Gold.Min, combat.Gold.Max), Math.Max(combat.Gold.Min, combat.Gold.Max) + 1)
            : MobStats.Coins(mob.Level, mob.Template.Power);
        // Official loot only (Master's Grimoire), or another monster's through server/content/loot-links.json. Monsters with
        // neither drop no items (owner's rule, 2026-10-15: no generated gear like a Silverweb Helm from a Bloodguard).
        var lootFrom = _lootLinks.GetValueOrDefault(mob.Template.Id, mob.Template.Id);
        var drops = (_officialLoot.Has(lootFrom) ? _officialLoot.Roll(lootFrom, _balance, Random.Shared) : [])
            .Where(d => !_spawnTuning.Removed(mob.Template.Id, d.Item)) // spawn-tuning.json removeDrops (owner's rule: discs only from the Warden bosses)
            .Concat(_spawnTuning.RollExtraDrops(mob.Template.Id, Random.Shared).Where(d => _balance.Items.ContainsKey(d.Item))).ToList(); // spawn-tuning.json extraDrops
        // Grouped: the drops are dealt out among the party (owner's rule, 2026-10-16).
        var group = KillGroup(s);
        var loot = group.ToDictionary(m => m, _ => new List<(int, int)>());
        foreach (var (owner, drop, quantity) in DealLoot(s, drops))
        {
            GrantItem(owner.Character!.Id, drop, quantity);
            loot[owner].Add((drop, quantity));
        }
        if (RollChestDrop() is { } chestDrop)
        {
            loot[s].Add((chestDrop, 1));
            GrantItem(s.Character!.Id, chestDrop, 1);
        }
        // spawn-tuning.json chestChance: the owner's event-chest chance (the Stonevale 6* legacy bosses: 10%).
        if (_spawnTuning.RollChest(mob.Template.Id, Random.Shared) && RandomEventChest() is { } eventChest)
        {
            loot[s].Add((eventChest, 1));
            GrantItem(s.Character!.Id, eventChest, 1);
        }
        foreach (var m in group)
        {
            loot[m].AddRange(QuestKill(m, mob)); // party members' kill and loot stages count too
            KillReward(m, mob, s.CharacterId, baseCoins, loot[m]);
        }
    }

    /// <summary>
    /// One player's share of a kill: their XP and gold, saved, and their kill report (WonCombat) with their items. The
    /// report names the killer, so party members read "skorrt defeated The All-Seeing One" and the killer "You defeated…".
    /// </summary>
    private void KillReward(Session s, Mob mob, int killerId, int baseCoins, List<(int, int)> loot)
    {
        var c = s.Character!;
        var progress = _store.GetProgress(c.Id);
        var boosts = Modifiers(s); // Elixir of Knowledge, Jubilation and gold-drop effects
        var tree = s.Sheet!.Tree;
        // Official XP (Master's Grimoire) for monsters that have it, with the owner's bonus for monsters above your level
        // and for stars (server/content/xp-bonus.json); the old formula for the rest.
        var baseXp = mob.Combat is { Xp: > 0 } rewards
            ? (int)Math.Round(rewards.XpFor(s.Sheet.Level) * _xpBonus.Multiplier(s.Sheet.Level, mob.Level, mob.Template.Power))
            : MobStats.Xp(mob.Level, mob.Template.Power);
        var xp = (int)Math.Round(baseXp * boosts.XpMultiplier * (100 + Math.Max(0, boosts.XpBoostPercent) + tree["xpPct"]) / 100); // Elixir of Knowledge: x2 / x4 / x5
        var coins = baseCoins * (100 + Math.Max(0, boosts.GoldBoostPercent) + (int)tree["goldPct"]) / 100;
        _store.SaveProgress(c.Id, progress.Level, progress.Xp, progress.Coins + coins);
        var newLevel = AddXp(s, xp, out var sheet);
        Send(s, Opcode.WonCombat, new WonCombat(mob.Template.Id, (int)mob.ServerId, killerId, xp, sheet.XpPercent, coins, loot, sheet.InventoryWithMoney()).Write);
        if (newLevel > progress.Level) AnnounceLevel(s, newLevel);
    }

    /// <summary>Adds XP, levelling up as many times as it covers. Returns the new level.</summary>
    private int AddXp(Session s, int xp, out CharacterSheet sheet)
    {
        var progress = _store.GetProgress(s.Character!.Id);
        var level = Math.Max(1, progress.Level);
        var totalXp = Math.Max(0, progress.Xp + xp);
        while (level < Progression.MaxLevel && totalXp >= Progression.XpToNextLevel(level))
        {
            totalXp -= Progression.XpToNextLevel(level);
            level++;
        }
        _store.SaveProgress(s.Character.Id, level, totalXp, progress.Coins);
        sheet = RefreshSheet(s);
        return level;
    }

    /// <summary>Tells the client about a new level and refills health and energy.</summary>
    private void AnnounceLevel(Session s, int level)
    {
        s.Health = int.MaxValue;
        s.Energy = int.MaxValue;
        var (available, notYet) = QuestLists(s);
        Send(s, Opcode.LevelUp, new LevelUp(level) { AvailableQuests = available, NotYetAvailableQuests = notYet }.Write);
        SendProgress(s); // new maximums, attribute and skill points, raised skill rank caps
        SendQuestData(s); // the client recomputes NPC quest icons on this, not on LevelUp
        BroadcastAppearance(s); // the level shown to others
        if (s.Party is { } party) SendRoster(party);
        _log.LogInformation("{Name} is now level {Level}", s.Name, level);
    }

    /// <summary>Reloads the sheet after progress or gear changes, carrying current HP and energy over.</summary>
    private CharacterSheet RefreshSheet(Session s)
    {
        var sheet = LoadSheet(s.Character!);
        s.Sheet = sheet;
        _ = LiveStats(s); // recomputes maxima with active effects and clamps current values
        RecountQuestItems(s); // items from trades, mail, shops, chests, normal loot… count for "collect" stages
        return sheet;
    }

    private void PlayerDies(Zone zone, Session s, double now)
    {
        s.Dead = true;
        s.AttackTarget = null;
        s.Casts.Clear();
        // Death ends buffs, not the pet's meal, the ride, (owner's rule) elixirs or the gear's own icons (regen, haste and
        // set auras: the gear is still worn). Owner's rule (2026-10-15): you stay on your mount when you die, with all its
        // bonuses; there's no Dismounted effect (live moves the mount to your bags).
        s.Effects.RemoveAll(e => !e.DisplayOnly && !Pets.AllPetEffects.Contains(e.Info.Id) && !Pets.MountEffects.Contains(e.Info.Id)
            && !(ElixirRules.Current.KeepOnDeath && ElixirRules.Current.IsElixir(e.Info.Id)));
        foreach (var mob in zone.Mobs.Values.Where(m => ReferenceEquals(m.Target, s)).ToList()) LoseTarget(zone, mob, s, now);
        SendEffects(s);
        SendSkillToggles(s);
        _store.AddStat(s.Character!.Id, "deaths", 1);
        RefreshZoneHate(zone.Id);
        _log.LogInformation("{Name} died", s.Name);
    }

    /// <summary>
    /// Resurrection Idols (36759, 58553): "Resurrect on the spot where you die … and regain up to 50% of your health and
    /// energy". The Ancient Resurrection Idol (20) promises the spot only, so it brings you back at full strength.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, double> ResurrectionIdols = new Dictionary<int, double> { [20] = 1.0, [36759] = 0.5, [58553] = 0.5 };

    /// <summary>
    /// RessurectionRequest: with an idol, revive where you fell (one idol is used up); otherwise at the nearest leystone
    /// with full health.
    /// </summary>
    private void OnResurrect(Session s, ResurrectionRequest req)
    {
        if (!s.Dead) return;
        var (zoneId, x, y, z, dir) = s.Position;
        var share = 1.0;
        // The idol the client chose, or any idol in the backpack if its id doesn't match.
        var idol = !req.UseItem ? null
            : Sheet(s).Backpack.FirstOrDefault(i => i.Id == req.ItemInventoryId && ResurrectionIdols.ContainsKey(i.TemplateId))
              ?? Sheet(s).Backpack.FirstOrDefault(i => ResurrectionIdols.ContainsKey(i.TemplateId));
        if (idol is not null)
        {
            share = ResurrectionIdols[idol.TemplateId];
            _store.SetItemQuantity(idol.Id, idol.Quantity - 1);
            var sheet = RefreshSheet(s);
            Send(s, Opcode.InventoryUpdate, sheet.InventoryWithMoney().Write);
        }
        else if (_balance.Zones.TryGetValue(zoneId, out var zone) && zone.PlayerSpawns.Count > 0)
        {
            var nearest = zone.PlayerSpawns.OrderBy(p => (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z)).First();
            (x, y, z) = (nearest.X, nearest.Y, nearest.Z);
        }
        else if (IsPvpZone(zoneId))
        {
            var entry = _travel.Entry(zoneId); // the arena has no leystone: back at its entrance
            (x, y, z) = (entry.X, entry.Y, entry.Z);
        }
        s.Dead = false;
        s.Health = Math.Max(1, (int)(s.MaxHealth * share));
        s.Energy = Math.Max(1, (int)(s.MaxEnergy * share));
        s.Position = (zoneId, x, y, z, dir);
        _store.SavePosition(s.Character!.Id, zoneId, x, y, z, dir);
        _store.SaveVitals(s.Character.Id, s.Health, s.Energy);
        Send(s, Opcode.RespawnPlayer, new RespawnPlayer([((uint)s.CharacterId, x, y, z, s.Health)]).Write);
        // Other clients hold other players' health out of 50.
        if (_zones.TryGetValue(zoneId, out var here))
        {
            SendToOthers(here, s, Opcode.RespawnPlayer, new RespawnPlayer([((uint)s.CharacterId, x, y, z, 50)]).Write);
            here.NextBatchAt = 0;
            RefreshZoneHate(zoneId);
        }
        SendEffects(s); // clears buff icons and resends full stats
    }
}
