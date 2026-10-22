using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Microsoft.Extensions.Logging;

namespace CH.Server;

// The owner's boss abilities (custom-monsters.json "abilities", Game/BossAbilities.cs): Denya the Heretic's Venom Coat,
// Morrigan's Embrace, Dark Touch and Priestess's Mezmorize. Each shows under its own skill and status effect, copies of
// existing ones the client mod also adds (CHPrivateMods.extras.txt), so names, icons and animations match.
public sealed partial class GameServer
{
    private readonly Dictionary<int, IReadOnlyList<BossAbility>> _bossAbilities = new();
    private readonly Dictionary<int, float> _customScales = new();

    /// <summary>monster-overrides.json "scale": the game's monsters' size, times the appearance's own (a spawn point's size wins).</summary>
    private readonly Dictionary<int, float> _monsterSizes = new();

    /// <summary>Reads the custom monsters' abilities and adds their skill and effect copies to the balance data.</summary>
    private void LoadBosses(IEnumerable<CustomContent.Monster> monsters)
    {
        foreach (var m in monsters)
        {
            if (m.Scale != 1) _customScales[m.Id] = m.Scale;
            if (m.AbilitiesJson is not { } json) continue;
            var abilities = BossAbility.Parse(json, name => CustomGear.Skill(name, _balance.Skills));
            foreach (var a in abilities)
            {
                if (!_balance.Skills.TryGetValue(a.Look, out var look)) continue;
                if (a.SkillId > 0) _balance.ReplaceSkill(look with { Id = a.SkillId, Name = a.Name });
                if (a.EffectId > 0 && _balance.StatusEffects.TryGetValue(look.StatusEffectId, out var effect))
                    _balance.ReplaceStatusEffect(effect with { Id = a.EffectId, Name = a.Name });
            }
            if (abilities.Count > 0) _bossAbilities[m.Id] = abilities;
        }
    }

    /// <summary>Lines for everyone near the boss ("Denya the Heretic started casting Dark Touch.").</summary>
    private void BossLine(Zone zone, Mob mob, string text, double now)
    {
        var (mx, mz) = mob.PositionAt(now);
        foreach (var p in zone.Players.Where(p => p.InZone && Dist2D(p.Position.X, p.Position.Z, mx, mz) <= MobCastLineRange).ToList())
            SystemLine(p, text);
    }

    /// <summary>The ability's icon on the boss while it lasts (display only; the server applies the ability itself).</summary>
    private void BossIcon(Zone zone, Mob mob, BossAbility a, double until)
    {
        if (!_balance.StatusEffects.TryGetValue(a.EffectId, out var info)) return;
        mob.Effects.RemoveAll(e => e.Info.Id == info.Id);
        mob.Effects.Add(new ActiveEffect { Info = info, LevelIndex = 0, ExpiresAt = until, NextTickAt = double.MaxValue, DisplayOnly = true, Caster = mob });
        BroadcastMobEffects(zone, mob);
    }

    private void BossHeals(Zone zone, Mob mob, int amount)
    {
        mob.Health = Math.Min(mob.MaxHealth, mob.Health + amount);
        Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([], [new HealthUpdate(HealthUpdate.Mob, (int)mob.ServerId, mob.Health)]).Write);
    }

    /// <summary>Runs a fighting boss's abilities; while it casts it does nothing else.</summary>
    private void TickBoss(Zone zone, Mob mob, double now)
    {
        if (mob.Boss is not { } boss || mob.State is not (MobState.Chasing or MobState.Fighting) || mob.Health <= 0) return;
        if (boss.Casting is { } cast)
        {
            if (now < cast.LandsAt) return;
            boss.Casting = null;
            BossHeals(zone, mob, cast.Ability.Heal);
            BossLine(zone, mob, $"{mob.Template.Name}'s {cast.Ability.Name} heals her for {cast.Ability.Heal:N0}.", now);
            return;
        }
        var percent = 100.0 * mob.Health / Math.Max(1, mob.MaxHealth);
        foreach (var a in boss.Abilities)
            switch (a.Kind)
            {
                case "weaponBuff" when boss.ActiveUntil.GetValueOrDefault(a) <= now && now >= boss.ReadyAt.GetValueOrDefault(a):
                    boss.ActiveUntil[a] = now + a.Duration;
                    boss.ReadyAt[a] = now + a.Duration + a.RecastAfter; // recast this long after it wears off
                    BossIcon(zone, mob, a, now + a.Duration);
                    BossLine(zone, mob, $"{mob.Template.Name} cast {a.Name}.", now);
                    break;
                case "selfHot":
                    if (boss.ActiveUntil.GetValueOrDefault(a) <= now)
                    {
                        boss.ActiveUntil[a] = now + a.Duration;
                        boss.NextTickAt[a] = now + a.Every;
                        BossIcon(zone, mob, a, now + a.Duration);
                        BossLine(zone, mob, $"{mob.Template.Name} cast {a.Name}.", now);
                    }
                    else if (now >= boss.NextTickAt.GetValueOrDefault(a))
                    {
                        boss.NextTickAt[a] = now + a.Every;
                        BossHeals(zone, mob, a.Heal);
                    }
                    break;
                case "selfHeal":
                    // Once as health falls below each threshold; thresholds it fell past together count once.
                    var crossed = a.AtHealth.Where(t => percent < t && !boss.HealedAt.Contains((a, t))).ToList();
                    if (crossed.Count == 0) break;
                    foreach (var t in crossed) boss.HealedAt.Add((a, t));
                    boss.Casting = (a, now + a.CastTime);
                    if (mob.IsMoving(now))
                    {
                        mob.StopAt(now);
                        BroadcastLeg(zone, mob);
                    }
                    BossLine(zone, mob, $"{mob.Template.Name} started casting {a.Name}.", now);
                    return; // casting: nothing else this tick
                case "aoeStun" when now >= boss.ReadyAt.GetValueOrDefault(a):
                    boss.ReadyAt[a] = now + a.Cooldown;
                    BossLine(zone, mob, $"{mob.Template.Name} cast {a.Name}.", now);
                    BossStun(zone, mob, a, now);
                    break;
            }
    }

    /// <summary>
    /// The area stun: everyone within the radius rolls its evasion type (as monsters' skills do); one who evades takes
    /// nothing, the rest take the damage (cut by their resist and level) and the stun.
    /// </summary>
    private void BossStun(Zone zone, Mob mob, BossAbility a, double now)
    {
        if (!_balance.Skills.TryGetValue(a.SkillId, out var skill)) return;
        var evadeSkill = skill with { AvoidanceType = a.EvadedBy };
        var (mx, mz) = mob.PositionAt(now);
        foreach (var target in zone.Players.Where(p => p.InZone && !p.Dead && !IsHiddenFrom(p, mob)
                     && Dist2D(p.Position.X, p.Position.Z, mx, mz) <= a.Radius).ToList())
        {
            target.LastCombatAt = now;
            var m = Modifiers(target);
            if (a.EvadedBy >= 0)
            {
                if (EvasionAbility(a.EvadedBy) is >= 0 and var guard) TrainCombatAbility(target, guard);
                var evasion = PlayerEvasion(target, evadeSkill, m);
                var lands = evasion <= 0 ? 1 : Math.Abs(evasion * 0.7 / (evasion + 50.0 * mob.Level) - 1);
                if (Random.Shared.NextDouble() >= lands)
                {
                    SendPlayerDamage(zone, target, new DamageEntry(DamageDirection.MobToPlayer, (int)mob.ServerId, target.CharacterId, AttackKind.Skill, skill.Id,
                        0, target.Health, Reaction.SkillAvoided, ++_damageId, false));
                    continue;
                }
            }
            var amount = a.Damage * (a.DamageType == 9 ? 1 : MobStats.ResistFactor(PlayerResist(target, a.DamageType, m), mob.Level))
                         * IncomingLevelFactor(target.Sheet!.Level, mob.Level);
            var damage = Math.Max(0, (int)Math.Round(amount));
            if (damage > 0) target.Health = Math.Max(0, target.Health - (damage - (damage - AbsorbWithShields(target, damage))));
            SendPlayerDamage(zone, target, new DamageEntry(DamageDirection.MobToPlayer, (int)mob.ServerId, target.CharacterId, AttackKind.Skill, skill.Id,
                damage, target.Health, Reaction.SkillHitAggressive, ++_damageId, false));
            if (target.Health <= 0)
            {
                PlayerDies(zone, target, now);
                continue;
            }
            if (a.StunSeconds > 0 && _balance.StatusEffects.TryGetValue(a.EffectId, out var stun))
            {
                target.Effects.RemoveAll(e => e.Info.Id == stun.Id);
                target.Effects.Add(new ActiveEffect { Info = stun, LevelIndex = 0, ExpiresAt = now + a.StunSeconds, NextTickAt = double.MaxValue, Caster = mob });
                target.Casts.Clear(); // a stun interrupts what they were casting
                SendEffects(target);
            }
        }
    }

    /// <summary>A player's hit that stops the boss's cast (Shield Bash stops Dark Touch).</summary>
    private void BossHitBy(Zone zone, Mob mob, AttackKind kind, int skillId, double now)
    {
        if (kind != AttackKind.Skill || mob.Boss?.Casting is not { } cast || !cast.Ability.InterruptedBy.Contains(skillId)) return;
        mob.Boss.Casting = null;
        BossLine(zone, mob, $"{mob.Template.Name}'s {cast.Ability.Name} was interrupted!", now);
    }

    /// <summary>Whether a player is stunned (Stun, Stun2, Frozen, Stasis: Priestess's Mezmorize and the like): no swings or casts.</summary>
    private static bool IsStunned(Session s, double now) =>
        s.Effects.Any(e => e.ExpiresAt > now && e.Type is EffectType.Stun or EffectType.Stun2 or EffectType.Frozen or EffectType.Stasis);
}
