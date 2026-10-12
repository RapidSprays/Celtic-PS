namespace CH.Server.Game;

/// <summary>Client Balance.Model.Templates.Status.EffectType (order matters: values are the enum positions).</summary>
public enum EffectType
{
    ChangeArmour, HealthRegen, EnergyRegen, Dot, ChangeAttack, Root, DamageShield, BuffRangeAttackAndDamage, ChangeRunSpeed, Hide,
    ChangeMaxHealth, ChangeMaxEnergy, ChangeResistance, EnergyShield, IncAttackDecArmour, ChangeDefence, ChangeDamageType,
    ChangeAttackSpeed, Stun, Invisibility, ChangeSize, BuffXpGain, Camouflage, Combination, ChangeAbilityGain,
    ChangeRunSpeedLevelBased, Camouflage2, EnergyShield2, Frozen, PlayingDead, HalfAttackSpeed, Stun2, ChangeFortitude, Silenced,
    Stasis, HealthAndEnergyRegen, HealthAndEnergyPercent, MaxHealthPercent, MaxEnergyPercent, ArmourPercent, DefencePercent,
    AttackPercent, HealthRegenPercentOfMax, EnergyRegenPercentOfMax, ChangeEvasions, IceSkillBoost, FireSkillBoost,
    DamageMultiNoPlayers, PetHungry, BuffFishingXpGain, ConcentrationRegen, Dismounted, Aura, Reverse, ChangeReductionType,
    CombinationGoldDrop, GoldDrop,
}

/// <summary>An effect on a player or mob. <see cref="Amount"/> uses the client's formula, so tooltips match.</summary>
public sealed class ActiveEffect
{
    public const float CasterAbilityLevel = 1f; // abilities start at level 1 (ClassKit)

    public required StatusEffectInfo Info { get; init; }
    public required int LevelIndex { get; init; }
    public required double ExpiresAt { get; init; }
    public double NextTickAt { get; set; }
    public object? Caster { get; init; }

    /// <summary>Remaining energy-shield absorption, for the shield types.</summary>
    public int ShieldRemaining { get; set; }

    public EffectType Type => (EffectType)Info.Type;
    public StatusEffectLevel? Level => Info.Level(LevelIndex);
    public int Amount => Level?.ModifiedAmount(CasterAbilityLevel) ?? 0;

    /// <summary>Hide uses the raw level amount as the highest mob level it hides from (CPlayableCharacter.IsHiddenFrom).</summary>
    public float RawAmount => Level?.Amount ?? 0;
}

/// <summary>
/// The summed effect of a player's buffs. Effect meanings follow the client's EffectType names and descriptions; how
/// strongly each one applies (flat vs percent) is <b>approximated</b> where the client doesn't say.
/// </summary>
public sealed record EffectModifiers
{
    public int Armour { get; init; }
    public int Defence { get; init; }
    public int Attack { get; init; }
    public int MaxHealth { get; init; }
    public int MaxEnergy { get; init; }
    public int HealthRegen { get; init; }
    public int EnergyRegen { get; init; }
    public int AttackSpeedPercent { get; init; }
    public int RunSpeedPercent { get; init; }
    public int RangedDamage { get; init; }
    public int DamageShield { get; init; }
    public int CamouflagePercent { get; init; }
    public float HiddenUpToLevel { get; init; }

    public static EffectModifiers From(IEnumerable<ActiveEffect> effects, int baseMaxHealth, int baseMaxEnergy, int baseArmour, int baseDefence, int baseAttack)
    {
        var m = new EffectModifiers();
        foreach (var e in effects)
        {
            var a = e.Amount;
            m = e.Type switch
            {
                EffectType.ChangeArmour => m with { Armour = m.Armour + a },
                EffectType.ArmourPercent => m with { Armour = m.Armour + baseArmour * a / 100 },
                EffectType.ChangeDefence or EffectType.ChangeEvasions => m with { Defence = m.Defence + a },
                EffectType.DefencePercent => m with { Defence = m.Defence + baseDefence * a / 100 },
                EffectType.ChangeAttack => m with { Attack = m.Attack + a },
                EffectType.AttackPercent => m with { Attack = m.Attack + baseAttack * a / 100 },
                EffectType.IncAttackDecArmour => m with { Attack = m.Attack + a, Armour = m.Armour - a },
                EffectType.ChangeMaxHealth => m with { MaxHealth = m.MaxHealth + a },
                EffectType.MaxHealthPercent => m with { MaxHealth = m.MaxHealth + baseMaxHealth * a / 100 },
                EffectType.ChangeMaxEnergy => m with { MaxEnergy = m.MaxEnergy + a },
                EffectType.MaxEnergyPercent => m with { MaxEnergy = m.MaxEnergy + baseMaxEnergy * a / 100 },
                EffectType.HealthRegen => m with { HealthRegen = m.HealthRegen + a },
                EffectType.HealthRegenPercentOfMax => m with { HealthRegen = m.HealthRegen + Math.Max(1, baseMaxHealth * a / 100) },
                EffectType.EnergyRegen => m with { EnergyRegen = m.EnergyRegen + a },
                EffectType.EnergyRegenPercentOfMax => m with { EnergyRegen = m.EnergyRegen + Math.Max(1, baseMaxEnergy * a / 100) },
                EffectType.HealthAndEnergyRegen => m with { HealthRegen = m.HealthRegen + a, EnergyRegen = m.EnergyRegen + a },
                EffectType.ChangeAttackSpeed => m with { AttackSpeedPercent = m.AttackSpeedPercent + a },
                EffectType.ChangeRunSpeed or EffectType.ChangeRunSpeedLevelBased => m with { RunSpeedPercent = m.RunSpeedPercent + a },
                EffectType.BuffRangeAttackAndDamage => m with { RangedDamage = m.RangedDamage + a },
                EffectType.DamageShield => m with { DamageShield = m.DamageShield + Math.Max(1, a) },
                EffectType.Camouflage or EffectType.Camouflage2 => m with { CamouflagePercent = Math.Min(90, m.CamouflagePercent + a) },
                EffectType.Hide => m with { HiddenUpToLevel = Math.Max(m.HiddenUpToLevel, e.RawAmount) },
                EffectType.Invisibility or EffectType.PlayingDead => m with { HiddenUpToLevel = float.MaxValue },
                _ => m,
            };
        }
        return m;
    }

    public static bool IsShield(EffectType t) => t is EffectType.EnergyShield or EffectType.EnergyShield2;

    /// <summary>Effects that end as soon as the player fights ("Entering combat will cancel this effect").</summary>
    public static bool BreaksOnCombat(EffectType t) => t is EffectType.Hide or EffectType.Invisibility or EffectType.PlayingDead;
}
