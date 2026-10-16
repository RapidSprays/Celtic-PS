using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;

namespace CH.Server;

/// <summary>
/// Buffs, debuffs and the stats they produce. The client shows icons and timers from StatusEffectUpdate (15017: the
/// effect list, then a full stats block); the server applies what the effects do.
/// </summary>
public sealed partial class GameServer
{
    private const double DotTickSeconds = 3;

    /// <summary>Seconds between an effect's ticks: its own (most damage over time ticks every 5 s), else 3.</summary>
    private static double TickOf(StatusEffectInfo info) => info.TickSeconds is > 0 and var t ? t : DotTickSeconds;

    private EffectModifiers Modifiers(Session s)
    {
        var sheet = s.Sheet!;
        return EffectModifiers.From(s.Effects.Where(e => !e.DisplayOnly), sheet.MaxHealth, sheet.MaxEnergy, sheet.Armour, sheet.Defence, sheet.Attack);
    }

    /// <summary>
    /// The player's live stats: sheet (class, level, gear) plus active effects, with the session's current HP and energy.
    /// Keeps the session's maxima in step. Always use this rather than the sheet's stats once the player is in the world.
    /// </summary>
    private CharacterStats LiveStats(Session s)
    {
        var sheet = s.Sheet!;
        var m = Modifiers(s);
        s.MaxHealth = Math.Max(1, sheet.MaxHealth + m.MaxHealth);
        s.MaxEnergy = Math.Max(1, sheet.MaxEnergy + m.MaxEnergy);
        s.Health = Math.Min(s.Health, s.MaxHealth);
        s.Energy = Math.Min(s.Energy, s.MaxEnergy);
        var stats = sheet.Stats();
        return stats with
        {
            Health = s.Health,
            MaxHealth = s.MaxHealth,
            Energy = s.Energy,
            MaxEnergy = s.MaxEnergy,
            ArmourValue = Math.Max(0, stats.ArmourValue + m.Armour),
            Defence = Math.Max(0, stats.Defence + m.Defence),
            Attack = Math.Max(0, stats.Attack + m.Attack + (sheet.AttackRange > 3 ? m.RangedAttack : 0)), // Steady Aim: ranged only
            AttackSpeed = AttackSpeedWith(stats.AttackSpeed, m.AttackSpeedPercent + sheet.AttackSpeedPercent),
            // The stats page's Damage is the whole weapon hit, as on live (1,822 for a 190 pierce + 200 heat halberd at
            // 650 Strength; Frenzy raises it). The client also adds it to weapon skills' tooltips.
            WeaponDamage = Math.Max(1, (int)(WeaponHit(s, m, null) + m.ExtraAttackDamage)),
            MaxSpeed = RunSpeedWith(stats.MaxSpeed, m.RunSpeedPercent + sheet.RunSpeedPercent), // riding: the Mounted effect
        };
    }

    /// <summary>
    /// One auto-attack's damage, by Master's Grimoire's formula. Pierce, slash and crush from everything worn are each
    /// multiplied by 1 + √(strength ÷ 40) + √(weapon ability ÷ 280); elemental damage adds as it is. Frenzy (and Steady
    /// Aim with a ranged weapon) add a percentage of the raw physical damage, and Sharpen Weapons, Poison Weapon and
    /// Reapers Touch a flat amount. Against a monster, each type is cut by its resist (flat, percent or immune) and the
    /// Grimoire's level factor applies. Attack doesn't add damage: it decides whether the swing hits.
    /// </summary>
    private int AutoAttackDamage(Session s, Mob? target = null, Session? player = null)
    {
        var sheet = s.Sheet!;
        var m = Modifiers(s);
        var total = WeaponHit(s, m, target, player) + m.ExtraAttackDamage;
        if (target is not null) total *= LevelFactor(sheet.Level, target.Level);
        return Math.Max(1, (int)(total * sheet.AttackDamageFactor));
    }

    /// <summary>
    /// One weapon hit, as Master's Grimoire's DPS solver has it for auto-attacks and for skills that include one: physical
    /// damage × (1 + √(Strength/40) + √(weapon ability/280)), elemental damage as it is, each type cut by the monster's
    /// resist, or by a player target's (armour for pierce, slash and crush, wards and lures included).
    /// </summary>
    private double WeaponHit(Session s, EffectModifiers m, Mob? target, Session? player = null)
    {
        var sheet = s.Sheet!;
        var weaponAbility = sheet.AbilityLevel(WeaponAbility(Weapon(s)));
        var multiplier = 1 + Math.Sqrt(Math.Max(0, sheet.CombinedStrength) / 40.0) + Math.Sqrt(Math.Max(0, weaponAbility) / 280.0);
        var pct = (m.PhysicalDamagePct + (sheet.AttackRange > 3 ? m.RangedDamagePct : 0)) / 100;
        double total = 0;
        foreach (var (type, amount) in sheet.DamageByType)
        {
            var part = type <= 2 ? (int)(amount * multiplier) + amount * pct : amount;
            total += target?.Combat is { } c ? c.AfterResist(type, part, sheet.Level, ResistChange(target.Effects, type))
                : player is not null ? part * MobStats.ResistFactor(PlayerResist(player, type, Modifiers(player)), sheet.Level)
                : part;
        }
        return total;
    }

    /// <summary>
    /// What resist-changing effects (ChangeResistance: Lure of Fire, Ice, Magic, Soldiers, Assassins, Giants, Lure of
    /// Death…) add to resist of a damage type. Each lure's type is in status-effects.json (Fire → heat, Giants → crush).
    /// </summary>
    private static int ResistChange(IEnumerable<ActiveEffect> effects, int type)
    {
        var now = NetTime.Now;
        return effects.Where(e => e.Type == EffectType.ChangeResistance && e.Info.DamageType == type && e.ExpiresAt > now).Sum(e => e.Amount);
    }

    /// <summary>
    /// A player's resist to a damage type when monsters hit them: gear resist, plus armour for pierce, slash and crush,
    /// plus lures on them; never below 0. True damage (9) isn't resisted.
    /// </summary>
    private static int PlayerResist(Session s, int type, EffectModifiers m) => type == 9 ? 0
        : Math.Max(0, s.Sheet!.Resist(type) + (type <= 2 ? Math.Max(0, s.Sheet.Armour + m.Armour) : 0) + ResistChange(s.Effects, type));

    // Owner's rule: no ceilings on attack or run speed. Only slows keep their floors (half attack speed, 20% run speed).
    public static int AttackSpeedWith(int milliseconds, int percent) => Math.Max(1, milliseconds * 100 / (100 + Math.Max(-50, percent)));

    public static float RunSpeedWith(float speed, int percent) => speed * (100 + Math.Max(-80, percent)) / 100f;

    private void SendLiveStats(Session s) => Send(s, Opcode.StatsUpdate, LiveStats(s).Write);

    /// <summary>StatusEffectUpdate: the whole effect list (it replaces the client's) followed by full stats.</summary>
    private void SendEffects(Session s)
    {
        var now = NetTime.Now;
        var stats = LiveStats(s);
        Send(s, Opcode.StatusEffectUpdate, b =>
        {
            WriteEffectList(b, s, now);
            stats.Write(b);
        });
        BroadcastEffects(s);
    }

    /// <summary>The status list shared by StatusEffectUpdate and OthersStatusEffect (NetworkManager.readStatusEffectsFromMessage).</summary>
    private static void WriteEffectList(NetBuffer b, Session s, double now)
    {
        b.WriteVariableInt32(s.Effects.Count);
        foreach (var e in s.Effects)
        {
            b.WriteVariableInt32(e.Info.Id);
            // The level the effect works at. Skill ranks go past an effect's last level (trained rank plus gear ranks,
            // e.g. Steady Aim 49 + 8 = 57 of 50); the client drops an icon whose level it can't find.
            b.WriteVariableInt32(Math.Clamp(e.LevelIndex, 0, Math.Max(0, e.Info.Levels.Count - 1)));
            b.WriteVariableInt32((int)Math.Ceiling(Math.Max(0, e.ExpiresAt - now)));
            b.Write(e.AbilityLevel);
            b.Write((float)e.StatRatio); // the client's GetModifiedAmount(ability, stat), so tooltips show the real amount
        }
    }

    /// <summary>Applies a skill's status effect to the caster (self and friendly buffs). Re-casting refreshes it.</summary>
    private void ApplyEffect(Session s, SkillInfo skill, int skillLevel, double now, float abilityLevel = ActiveEffect.CasterAbilityLevel, double statRatio = 0,
        Mob? caster = null)
    {
        if (!_balance.StatusEffects.TryGetValue(skill.StatusEffectId, out var info) || info.Level(skillLevel) is not { } level) return;
        s.Effects.RemoveAll(e => e.Info.Id == info.Id);
        var effect = new ActiveEffect
        {
            Info = info, LevelIndex = skillLevel, ExpiresAt = now + Math.Max(1, level.Duration), NextTickAt = now + TickOf(info),
            AbilityLevel = abilityLevel, StatRatio = statRatio, Caster = caster,
        };
        if (EffectModifiers.IsShield(effect.Type)) effect.ShieldRemaining = Math.Max(1, effect.Amount);
        s.Effects.Add(effect);
        // Rapid Shot and other attack-speed buffs take effect on the very next swing, not the one after.
        if (effect.Type is EffectType.ChangeAttackSpeed) s.NextSwingAt = Math.Min(s.NextSwingAt, now + LiveStats(s).AttackSpeed / 1000.0);
        SendEffects(s);
    }

    /// <summary>Hide and the like end when the player fights.</summary>
    private void BreakStealth(Session s)
    {
        if (s.Effects.RemoveAll(e => EffectModifiers.BreaksOnCombat(e.Type)) > 0) SendEffects(s);
    }

    private bool IsHiddenFrom(Session s, Mob mob) => mob.Level <= Modifiers(s).HiddenUpToLevel;

    /// <summary>Expires effects and runs damage/heal over time. Called from the player tick.</summary>
    private void TickEffects(Zone zone, Session s, double now)
    {
        // Damage over time a monster's skill left on the player (bleeds, poisons, burns).
        foreach (var dot in s.Effects.Where(e => e.Type == EffectType.Dot && e.Caster is Mob && now >= e.NextTickAt && now < e.ExpiresAt).ToList())
        {
            dot.NextTickAt = now + TickOf(dot.Info);
            var source = (Mob)dot.Caster!;
            var sheet = s.Sheet!;
            var damage = Math.Abs(dot.Amount);
            if (dot.Info.DamageType is { } type and not 9)
                damage = (int)(damage * MobStats.ResistFactor(PlayerResist(s, type, Modifiers(s)), source.Level));
            damage = Math.Max(1, damage);
            s.Health = Math.Max(0, s.Health - damage);
            SendPlayerDamage(zone, s, new DamageEntry(DamageDirection.MobToPlayer, (int)source.ServerId, s.CharacterId, AttackKind.StatusEffect, dot.Info.Id,
                damage, s.Health, Reaction.SkillHitAggressive, ++_damageId, false));
            if (s.Health <= 0)
            {
                PlayerDies(zone, s, now);
                return;
            }
        }
        if (s.Effects.RemoveAll(e => now >= e.ExpiresAt || (EffectModifiers.IsShield(e.Type) && e.ShieldRemaining <= 0)) > 0)
        {
            UpdatePetEffects(s, send: false); // a meal that ran out makes the pet hungry
            SendEffects(s);
            SendSkillToggles(s);
        }
    }

    /// <summary>Energy shields soak damage first. Returns the damage left for health.</summary>
    private int AbsorbWithShields(Session s, int damage)
    {
        foreach (var shield in s.Effects.Where(e => EffectModifiers.IsShield(e.Type) && e.ShieldRemaining > 0))
        {
            var soaked = Math.Min(damage, shield.ShieldRemaining);
            shield.ShieldRemaining -= soaked;
            damage -= soaked;
            if (damage == 0) break;
        }
        return damage;
    }

    // ---- debuffs on mobs (damage over time) ---------------------------------------------------

    private void ApplyMobEffect(Mob mob, Session caster, SkillInfo skill, int skillLevel, double now, float abilityLevel = ActiveEffect.CasterAbilityLevel, double statRatio = 0)
    {
        if (!_balance.StatusEffects.TryGetValue(skill.StatusEffectId, out var info) || info.Level(skillLevel) is not { } level) return;
        mob.Effects.RemoveAll(e => e.Info.Id == info.Id);
        mob.Effects.Add(new ActiveEffect
        {
            Info = info, LevelIndex = skillLevel, ExpiresAt = now + Math.Max(1, level.Duration), NextTickAt = now + TickOf(info), Caster = caster,
            AbilityLevel = abilityLevel, StatRatio = statRatio,
        });
        if (_zones.TryGetValue(caster.Position.Zone, out var zone)) BroadcastMobEffects(zone, mob);
    }

    /// <summary>
    /// What players' debuffs do to a monster: attack (Howling Wind and other ChangeAttack/AttackPercent effects, which
    /// lower its chance to hit), defence (ChangeDefence, DefencePercent: raise players' chance to hit it),
    /// attack speed (any slow halves its attack rate; see below) and stuns (Stun, Stun2, Frozen, Stasis: no attacks).
    /// </summary>
    private static (int Attack, int Defence, int AttackSpeedPercent, bool Stunned) MobDebuffs(Mob mob)
    {
        var baseAttack = mob.Combat is { Attack: > 0 } c ? c.Attack : MobStats.Attack(mob.Level, mob.Template.Power);
        var baseDefence = mob.Combat?.Defence ?? 0;
        var (attack, defence, speed, stunned, slowed) = (0, 0, 0, false, false);
        foreach (var e in mob.Effects)
        {
            var a = e.Amount;
            switch (e.Type)
            {
                case EffectType.ChangeAttack: attack += a; break;
                case EffectType.AttackPercent: attack += baseAttack * a / 100; break;
                case EffectType.ChangeDefence: defence += a; break;
                case EffectType.DefencePercent: defence += baseDefence * a / 100; break;
                // Owner's rule: any attack-speed-down on a monster halves its attack rate (attacks take twice as long),
                // whatever the effect's own number; they don't stack. Speed-ups (rare on monsters) add as they are.
                case EffectType.ChangeAttackSpeed when a < 0:
                case EffectType.HalfAttackSpeed:
                    slowed = true;
                    break;
                case EffectType.ChangeAttackSpeed: speed += a; break;
                case EffectType.Stun or EffectType.Stun2 or EffectType.Frozen or EffectType.Stasis: stunned = true; break;
            }
        }
        return (attack, defence, slowed ? -50 : speed, stunned);
    }

    /// <summary>Tells everyone in the zone a monster's status icons changed (its debuffs, plus Chaos Aura).</summary>
    private void BroadcastMobEffects(Zone zone, Mob mob)
    {
        foreach (var p in zone.Players.ToList()) SendMobEffects(p, [mob]);
    }

    private void TickMobEffects(Zone zone, Mob mob, double now)
    {
        if (mob.Effects.Count == 0) return;
        if (mob.Effects.RemoveAll(e => now >= e.ExpiresAt) > 0) BroadcastMobEffects(zone, mob);
        foreach (var dot in mob.Effects.Where(e => e.Type == EffectType.Dot && now >= e.NextTickAt).ToList())
        {
            dot.NextTickAt = now + TickOf(dot.Info);
            if (dot.Caster is Session caster && zone.Players.Contains(caster) && mob.State != MobState.Dead)
            {
                double tick = Math.Max(1, dot.Amount);
                if (dot.Info.DamageType is { } type && mob.Combat is { } c) tick = c.AfterResist(type, tick, caster.Sheet!.Level, ResistChange(mob.Effects, type));
                PlayerHits(zone, caster, mob, AttackKind.StatusEffect, dot.Info.Id, Math.Max(1, (int)tick), now, allowMiss: false);
            }
        }
    }
}
