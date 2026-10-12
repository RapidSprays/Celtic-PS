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

    private EffectModifiers Modifiers(Session s)
    {
        var sheet = s.Sheet!;
        return EffectModifiers.From(s.Effects, sheet.MaxHealth, sheet.MaxEnergy, sheet.Armour, sheet.Defence, sheet.Attack);
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
            Attack = Math.Max(0, stats.Attack + m.Attack),
            AttackSpeed = Math.Max(300, stats.AttackSpeed * 100 / (100 + Math.Max(-50, m.AttackSpeedPercent))),
            MaxSpeed = stats.MaxSpeed * (100 + Math.Clamp(m.RunSpeedPercent, -80, 200)) / 100f,
        };
    }

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
            b.WriteVariableInt32(e.LevelIndex);
            b.WriteVariableInt32((int)Math.Ceiling(Math.Max(0, e.ExpiresAt - now)));
            b.Write(ActiveEffect.CasterAbilityLevel);
            b.Write(0f);
        }
    }

    /// <summary>Applies a skill's status effect to the caster (self and friendly buffs). Re-casting refreshes it.</summary>
    private void ApplyEffect(Session s, SkillInfo skill, int skillLevel, double now)
    {
        if (!_balance.StatusEffects.TryGetValue(skill.StatusEffectId, out var info) || info.Level(skillLevel) is not { } level) return;
        s.Effects.RemoveAll(e => e.Info.Id == info.Id);
        var effect = new ActiveEffect { Info = info, LevelIndex = skillLevel, ExpiresAt = now + Math.Max(1, level.Duration), NextTickAt = now + DotTickSeconds };
        if (EffectModifiers.IsShield(effect.Type)) effect.ShieldRemaining = Math.Max(1, effect.Amount);
        s.Effects.Add(effect);
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
        if (s.Effects.RemoveAll(e => now >= e.ExpiresAt || (EffectModifiers.IsShield(e.Type) && e.ShieldRemaining <= 0)) > 0) SendEffects(s);
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

    private void ApplyMobEffect(Mob mob, Session caster, SkillInfo skill, int skillLevel, double now)
    {
        if (!_balance.StatusEffects.TryGetValue(skill.StatusEffectId, out var info) || info.Level(skillLevel) is not { } level) return;
        if ((EffectType)info.Type != EffectType.Dot) return;
        mob.Effects.RemoveAll(e => e.Info.Id == info.Id);
        mob.Effects.Add(new ActiveEffect { Info = info, LevelIndex = skillLevel, ExpiresAt = now + Math.Max(1, level.Duration), NextTickAt = now + DotTickSeconds, Caster = caster });
    }

    private void TickMobEffects(Zone zone, Mob mob, double now)
    {
        if (mob.Effects.Count == 0) return;
        mob.Effects.RemoveAll(e => now >= e.ExpiresAt);
        foreach (var dot in mob.Effects.Where(e => now >= e.NextTickAt).ToList())
        {
            dot.NextTickAt = now + DotTickSeconds;
            if (dot.Caster is Session caster && zone.Players.Contains(caster) && mob.State != MobState.Dead)
                PlayerHits(zone, caster, mob, AttackKind.StatusEffect, dot.Info.Id, Math.Max(1, dot.Amount), now, allowMiss: false);
        }
    }
}
