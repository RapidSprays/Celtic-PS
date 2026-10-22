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
            AttackSpeed = AttackSpeedWithElixir(stats.AttackSpeed, m, sheet),
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
        var total = WeaponHit(s, m, target, player) + m.ExtraAttackDamage
            + (target is not null && _spawnTuning.TakesDivine(target.Template.Id) ? m.DivineAttackDamage : 0); // Dragonlord Wrath
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
            total += target?.Combat is { } c ? c.AfterResist(type, part, sheet.Level, MobResistChange(target.Effects, type))
                : player is not null ? part * MobStats.ResistFactor(PlayerResist(player, type, Modifiers(player)), sheet.Level)
                : part;
        }
        return total;
    }

    /// <summary>
    /// What resist-changing effects (ChangeResistance: Lure of Fire, Ice, Magic, Soldiers, Assassins, Giants, Lure of
    /// Death…) add to resist of a damage type. Each lure's type is in status-effects.json (Fire → heat, Giants → crush).
    /// </summary>
    /// <summary>
    /// A monster's resist change: lures and resist debuffs, plus armour effects (Shatter's "reducing their armour") on its
    /// physical resists (pierce, slash, crush), since a monster's armour is those resists.
    /// </summary>
    private static int MobResistChange(IEnumerable<ActiveEffect> effects, int type)
    {
        var now = NetTime.Now;
        var armour = type <= 2 ? effects.Where(e => e.Type == EffectType.ChangeArmour && e.ExpiresAt > now).Sum(e => e.Amount) : 0;
        return ResistChange(effects, type) + armour;
    }

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

    /// <summary>
    /// Attack delay with buffs and haste. An elixir's haste (owner, from live) replaces gear haste instead of adding to it,
    /// and never takes the weapon below the elixir's minimum delay (1,100 ms), unless it was already faster without it.
    /// </summary>
    private static int AttackSpeedWithElixir(int weaponSpeed, EffectModifiers m, CharacterSheet sheet)
    {
        var without = AttackSpeedWith(weaponSpeed, m.AttackSpeedPercent + sheet.AttackSpeedPercent);
        if (m.ElixirHaste <= 0) return without;
        var with = AttackSpeedWith(weaponSpeed, m.AttackSpeedPercent + sheet.AttackSpeedPercent - sheet.GearHastePercent + m.ElixirHaste);
        return Math.Max(with, Math.Min(without, ElixirRules.Current.MinAttackDelay));
    }

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
            b.WriteVariableInt32(e.Info.ClientLevel(e.LevelIndex));
            b.WriteVariableInt32((int)Math.Ceiling(Math.Max(0, e.ExpiresAt - now)));
            b.Write(e.AbilityLevel);
            b.Write((float)e.StatRatio); // the client's GetModifiedAmount(ability, stat), so tooltips show the real amount
        }
    }

    /// <summary>Applies a skill's status effect to the caster (self and friendly buffs). Re-casting refreshes it.</summary>
    private void ApplyEffect(Session s, SkillInfo skill, int skillLevel, double now, float abilityLevel = ActiveEffect.CasterAbilityLevel, double statRatio = 0,
        object? caster = null)
    {
        if (!_balance.StatusEffects.TryGetValue(skill.StatusEffectId, out var info) || info.Level(skillLevel) is not { } level) return;
        if (info.Id == FingalsBaneEffect && HasFingalsShield(s)) return; // scripted casts too
        s.Effects.RemoveAll(e => e.Info.Id == info.Id);
        var effect = new ActiveEffect
        {
            Info = info, LevelIndex = skillLevel, ExpiresAt = now + Math.Max(1, level.Duration),
            NextTickAt = caster is Session && (EffectType)info.Type == EffectType.Dot ? now : now + TickOf(info), // PvP: first tick on the hit
            AbilityLevel = abilityLevel, StatRatio = statRatio, Caster = caster,
        };
        // A player's healing skill that leaves health regen (Nature's Embrace): a heal over time with heal numbers and
        // crits (owner's rule, 2026-10-18), not silent regen. Pet melodies (no ability) stay regen.
        if (caster is Session healer && healer.Sheet is { } hs && (EffectType)info.Type == EffectType.HealthRegen && skill.AbilityId >= 0 && effect.Amount > 0)
            effect = new ActiveEffect
            {
                Info = effect.Info, LevelIndex = effect.LevelIndex, ExpiresAt = effect.ExpiresAt, NextTickAt = effect.NextTickAt,
                AbilityLevel = abilityLevel, StatRatio = statRatio, Caster = caster, HealSkillId = skill.Id,
                HealPerTick = Math.Max(1, (int)(effect.Amount * hs.HealFactor(skill.Id))),
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
        // Damage over time another player's skill left on this one (PvP): the player hit path, so resists, duels (they stop
        // at 1 health) and PvP deaths work as for any hit; each tick can train the caster's Critical Skills.
        foreach (var dot in s.Effects.Where(e => e.Type == EffectType.Dot && e.Caster is Session && now >= e.NextTickAt && now < e.ExpiresAt).ToList())
        {
            dot.NextTickAt = now + TickOf(dot.Info);
            var caster = (Session)dot.Caster!;
            if (!caster.InZone || caster.Position.Zone != s.Position.Zone) continue;
            double tick = Math.Max(1, Math.Abs(dot.Amount)) * _dotDamage.Factor(dot.Info.Id, Random.Shared); // dot-damage.json variance
            if (dot.Info.DamageType is { } dotType) tick *= MobStats.ResistFactor(PlayerResist(s, dotType, Modifiers(s)), caster.Sheet!.Level);
            PlayerHitsPlayer(zone, caster, s, AttackKind.StatusEffect, dot.Info.Id, Math.Max(1, (int)tick), now, allowMiss: false);
            TrainAbility(caster, CriticalSkills, _dotCriticalChance * Modifiers(caster).AbilityGainMultiplier);
            if (s.Dead) return;
        }
        // Heals over time from a player's skill (Nature's Embrace): each tick heals with a heal number, and can crit.
        foreach (var hot in s.Effects.Where(e => e.HealPerTick is not null && now >= e.NextTickAt && now < e.ExpiresAt).ToList())
        {
            hot.NextTickAt = now + TickOf(hot.Info);
            var healer = hot.Caster as Session;
            var heal = hot.HealPerTick!.Value;
            var crit = healer is { Sheet: not null } && Random.Shared.NextDouble() < HealCritChance(healer, s);
            if (crit) heal = (int)(heal * healer!.Sheet!.CritMultiplier);
            var gained = Math.Min(heal, s.MaxHealth - s.Health);
            s.Health = Math.Min(s.MaxHealth, s.Health + heal);
            if (healer?.Character is { } hc) _store.AddStat(hc.Id, "heal", gained);
            // As a status effect's tick (its effect id), like damage over time: the heal number shows, but not the skill's
            // cast animation, which plays only when the skill is cast (owner's report, 2026-10-20).
            SendPlayerDamage(zone, s, new DamageEntry(DamageDirection.PlayerToPlayer, healer?.CharacterId ?? s.CharacterId, s.CharacterId, AttackKind.StatusEffect,
                hot.Info.Id, -heal, s.Health, Reaction.SkillHitPositive, ++_damageId, crit));
            if (crit && healer is not null) PartyLine(healer, HealCritLine(healer, hot.HealSkillId, heal));
            // Like damage-over-time ticks, every heal tick can train the healer's Critical Skills (dotCriticalChance).
            if (healer is { Sheet: not null }) TrainAbility(healer, CriticalSkills, _dotCriticalChance * Modifiers(healer).AbilityGainMultiplier);
        }
        // Damage over time a monster's skill left on the player (bleeds, poisons, burns).
        foreach (var dot in s.Effects.Where(e => e.Type == EffectType.Dot && e.Caster is Mob && now >= e.NextTickAt && now < e.ExpiresAt).ToList())
        {
            dot.NextTickAt = now + TickOf(dot.Info);
            var source = (Mob)dot.Caster!;
            var sheet = s.Sheet!;
            var damage = Math.Abs(dot.Amount);
            if (dot.Info.DamageType is { } type and not 9)
                damage = (int)(damage * MobStats.ResistFactor(PlayerResist(s, type, Modifiers(s)), source.Level));
            damage = Math.Max(1, (int)(damage * IncomingLevelFactor(sheet.Level, source.Level)));
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
            // Owner's rule (2026-10-16): a player's damage over time (Stinging Swarm, Incinerate, Strangling Vines, Rend,
            // Rupture, Frostbite, Barbed Shot…) does its first tick's damage on the hit, then ticks on as usual.
            Info = info, LevelIndex = skillLevel, ExpiresAt = now + Math.Max(1, level.Duration),
            NextTickAt = (EffectType)info.Type == EffectType.Dot ? now : now + TickOf(info), Caster = caster,
            AbilityLevel = abilityLevel, StatRatio = statRatio,
        });
        if (_zones.TryGetValue(caster.Position.Zone, out var zone)) BroadcastMobEffects(zone, mob);
    }

    /// <summary>
    /// skill-overrides.json damageAsDot (Pounce): the hit's damage in equal ticks, the first now, under the status effect
    /// shown on the monster.
    /// </summary>
    private void ApplyDamageAsDot(Mob mob, Session caster, (int Effect, int Ticks, double Seconds) dot, int damage, double now)
    {
        if (!_balance.StatusEffects.TryGetValue(dot.Effect, out var info)) return;
        var every = dot.Seconds / dot.Ticks;
        mob.Effects.RemoveAll(e => e.Info.Id == info.Id && ReferenceEquals(e.Caster, caster));
        mob.Effects.Add(new ActiveEffect
        {
            Info = info, LevelIndex = 0, Caster = caster, NextTickAt = now, TickSeconds = every,
            ExpiresAt = now + (dot.Ticks - 1) * every + every / 2, // the last tick lands before it ends
            FixedAmount = (int)Math.Ceiling(damage / (double)dot.Ticks),
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
            dot.NextTickAt = now + (dot.TickSeconds ?? TickOf(dot.Info));
            if (dot.FixedAmount is { } fixedTick && dot.Caster is Session owner && zone.Players.Contains(owner) && mob.State != MobState.Dead)
            {
                PlayerHits(zone, owner, mob, AttackKind.StatusEffect, dot.Info.Id, Math.Max(1, fixedTick), now, allowMiss: false); // damageAsDot: same total as the hit
                TrainAbility(owner, CriticalSkills, _dotCriticalChance * Modifiers(owner).AbilityGainMultiplier);
                continue;
            }
            if (dot.Caster is Session caster && zone.Players.Contains(caster) && mob.State != MobState.Dead)
            {
                // Dragonlord Spite's own range (divine-strike.json), else the tick with the owner's variance (dot-damage.json).
                double tick = _divineStrike.Offhand(dot.Info.Id, Random.Shared) ?? Math.Max(1, dot.Amount) * _dotDamage.Factor(dot.Info.Id, Random.Shared, mob.Level - caster.Sheet!.Level);
                if (dot.Info.DamageType is { } type && mob.Combat is { } c) tick = c.AfterResist(type, tick, caster.Sheet!.Level, MobResistChange(mob.Effects, type));
                PlayerHits(zone, caster, mob, AttackKind.StatusEffect, dot.Info.Id, Math.Max(1, (int)tick), now, allowMiss: false);
                // Owner's rule (2026-10-15): every damage-over-time tick from a player's skill can train Critical Skills.
                TrainAbility(caster, CriticalSkills, _dotCriticalChance * Modifiers(caster).AbilityGainMultiplier);
            }
        }
    }
}
