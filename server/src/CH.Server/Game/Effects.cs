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

    /// <summary>Shown to the client only (gear regen's permanent icons); the server applies the real amounts itself.</summary>
    public bool DisplayOnly { get; init; }

    /// <summary>Remaining energy-shield absorption, for the shield types.</summary>
    public int ShieldRemaining { get; set; }

    public EffectType Type => (EffectType)Info.Type;
    public StatusEffectLevel? Level => Info.Level(LevelIndex);
    /// <summary>The caster's level in the skill's ability (the client's effect formula scales with it).</summary>
    public float AbilityLevel { get; init; } = CasterAbilityLevel;

    public int Amount => Level?.ModifiedAmount(AbilityLevel, (float)StatRatio) ?? 0;

    /// <summary>The caster's attribute ÷ the skill's divisor when it was cast (<see cref="CharacterSheet.StatRatio"/>); 0 for none.</summary>
    public double StatRatio { get; init; }

    /// <summary>Hide uses the raw level amount as the highest mob level it hides from (CPlayableCharacter.IsHiddenFrom).</summary>
    public float RawAmount => Level?.Amount ?? 0;
}

/// <summary>
/// The summed effect of a player's buffs. Effect meanings follow the client's EffectType names and descriptions; how
/// strongly each one applies (flat vs percent) is <b>approximated</b> where the client doesn't say.
/// </summary>
public sealed record EffectModifiers
{
    /// <summary>Extra kill XP and coins, in percent (Elixir of Knowledge, Jubilation Elixir, gold-drop effects).</summary>
    public int XpBoostPercent { get; init; }
    public int GoldBoostPercent { get; init; }

    public int Armour { get; init; }
    public int Defence { get; init; }

    /// <summary>Added to every skill evasion (Bless: "increasing all your Skill Evasions").</summary>
    public int Evasion { get; init; }
    public int Attack { get; init; }
    public int MaxHealth { get; init; }
    public int MaxEnergy { get; init; }
    public int HealthRegen { get; init; }
    public int EnergyRegen { get; init; }
    public int AttackSpeedPercent { get; init; }
    public int RunSpeedPercent { get; init; }

    /// <summary>An elixir's haste (percent): it replaces haste from gear rather than adding to it (0: none).</summary>
    public int ElixirHaste { get; init; }

    /// <summary>Elixirs of Knowledge and Wisdom (and the combination elixirs): kill XP and ability gain chances times this.</summary>
    public double XpMultiplier { get; init; } = 1;
    public double AbilityGainMultiplier { get; init; } = 1;
    /// <summary>Steady Aim: attack while using a ranged weapon.</summary>
    public int RangedAttack { get; init; }

    /// <summary>Steady Aim: percent added to physical weapon damage with a ranged weapon (its amount ÷ 5, as Master's Grimoire has it).</summary>
    public double RangedDamagePct { get; init; }

    /// <summary>Frenzy: percent added to physical weapon damage (its amount ÷ 10, as Master's Grimoire has it).</summary>
    public double PhysicalDamagePct { get; init; }

    /// <summary>Ice/Fire Attunement: damage added to Ice Shards and Ice Blast, or Fire Bolt and Firestorm.</summary>
    public int IceSkillBoost { get; init; }
    public int FireSkillBoost { get; init; }

    /// <summary>Extra damage on every auto-attack (ChangeDamageType buffs: Reapers Touch "an additional 100 poison damage per attack").</summary>
    public int ExtraAttackDamage { get; init; }
    public int DamageShield { get; init; }
    public int CamouflagePercent { get; init; }
    public float HiddenUpToLevel { get; init; }

    public static EffectModifiers From(IEnumerable<ActiveEffect> effects, int baseMaxHealth, int baseMaxEnergy, int baseArmour, int baseDefence, int baseAttack)
    {
        var m = new EffectModifiers();
        var list = effects.ToList();
        m = WithElixirs(m, list, baseMaxHealth, baseMaxEnergy, baseArmour, baseDefence, baseAttack);
        foreach (var e in list)
        {
            if (ElixirRules.Current.TryGet(e.Info.Id, e.LevelIndex, out _, out _)) continue; // applied above
            var a = e.Amount;
            m = e.Type switch
            {
                EffectType.ChangeArmour => m with { Armour = m.Armour + a },
                EffectType.ArmourPercent => m with { Armour = m.Armour + baseArmour * a / 100 },
                EffectType.ChangeDefence => m with { Defence = m.Defence + a },
                EffectType.ChangeEvasions => m with { Evasion = m.Evasion + a },
                EffectType.DefencePercent => m with { Defence = m.Defence + baseDefence * a / 100 },
                EffectType.ChangeAttack => m with { Attack = m.Attack + a },
                EffectType.AttackPercent => m with { Attack = m.Attack + baseAttack * a / 100 },
                // Frenzy: attack, and (amount ÷ 10)% of physical weapon damage (Master's Grimoire). Despite the enum's name
                // it costs no armour in the game.
                EffectType.IncAttackDecArmour => m with { Attack = m.Attack + a, PhysicalDamagePct = m.PhysicalDamagePct + a / 10.0 },
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
                EffectType.BuffRangeAttackAndDamage => m with { RangedAttack = m.RangedAttack + a, RangedDamagePct = m.RangedDamagePct + a / 5.0 },
                // Sharpen Weapons, Poison Weapon, Reapers Touch: flat damage on every auto-attack.
                EffectType.ChangeDamageType => m with { ExtraAttackDamage = m.ExtraAttackDamage + a },
                EffectType.IceSkillBoost => m with { IceSkillBoost = m.IceSkillBoost + a },
                EffectType.FireSkillBoost => m with { FireSkillBoost = m.FireSkillBoost + a },
                EffectType.DamageShield => m with { DamageShield = m.DamageShield + Math.Max(1, a) },
                EffectType.Camouflage or EffectType.Camouflage2 => m with { CamouflagePercent = Math.Min(90, m.CamouflagePercent + a) },
                EffectType.BuffXpGain => m with { XpBoostPercent = m.XpBoostPercent + a },
                EffectType.GoldDrop => m with { GoldBoostPercent = m.GoldBoostPercent + a },
                EffectType.CombinationGoldDrop => m with { GoldBoostPercent = m.GoldBoostPercent + a, XpBoostPercent = m.XpBoostPercent + a },
                // Combination potions/elixirs raise attack, defence and armour together (approximated split).
                EffectType.Combination => m with
                {
                    Attack = m.Attack + baseAttack * a / 100, Defence = m.Defence + baseDefence * a / 100, Armour = m.Armour + baseArmour * a / 100,
                },
                EffectType.Hide => m with { HiddenUpToLevel = Math.Max(m.HiddenUpToLevel, e.RawAmount) },
                EffectType.Invisibility or EffectType.PlayingDead => m with { HiddenUpToLevel = float.MaxValue },
                _ => m,
            };
        }
        return m;
    }

    /// <summary>
    /// The elixirs' boosts (server/content/elixirs.json): for each stat the strongest active elixir, since elixirs don't
    /// stack. Flat + percent of the character's own stat; regen per tick from maximum health and energy.
    /// </summary>
    private static EffectModifiers WithElixirs(EffectModifiers m, List<ActiveEffect> effects, int maxHealth, int maxEnergy, int armour, int defence, int attack)
    {
        var rules = ElixirRules.Current;
        var best = new Dictionary<string, double>();
        void Keep(string stat, double value) => best[stat] = Math.Max(best.GetValueOrDefault(stat), value);
        foreach (var e in effects)
        {
            if (!rules.TryGet(e.Info.Id, e.LevelIndex, out var tier, out var gives)) continue;
            foreach (var stat in gives)
            {
                double Of(int own) => rules.BoostOf(tier, stat) is var b ? b.Flat + own * b.Percent / 100 : 0;
                switch (stat)
                {
                    case "attack": Keep(stat, Of(attack)); break;
                    case "defence": Keep(stat, Of(defence)); break;
                    case "armour": Keep(stat, Of(armour)); break;
                    case "maxHealth" or "healthRegen": Keep(stat, Of(maxHealth)); break;
                    case "maxEnergy" or "energyRegen": Keep(stat, Of(maxEnergy)); break;
                    case "haste" or "runSpeed": Keep(stat, rules.ValueOf(tier, stat, 0)); break;
                    case "xp" or "abilityGain": Keep(stat, rules.ValueOf(tier, stat, 1)); break;
                }
            }
        }
        int Get(string stat) => (int)Math.Round(best.GetValueOrDefault(stat));
        return m with
        {
            Attack = m.Attack + Get("attack"), Defence = m.Defence + Get("defence"), Armour = m.Armour + Get("armour"),
            MaxHealth = m.MaxHealth + Get("maxHealth"), MaxEnergy = m.MaxEnergy + Get("maxEnergy"),
            HealthRegen = m.HealthRegen + Get("healthRegen"), EnergyRegen = m.EnergyRegen + Get("energyRegen"),
            RunSpeedPercent = m.RunSpeedPercent + Get("runSpeed"), ElixirHaste = Get("haste"),
            XpMultiplier = Math.Max(1, best.GetValueOrDefault("xp", 1)), AbilityGainMultiplier = Math.Max(1, best.GetValueOrDefault("abilityGain", 1)),
        };
    }

    public static bool IsShield(EffectType t) => t is EffectType.EnergyShield or EffectType.EnergyShield2;

    /// <summary>Effects that end as soon as the player fights ("Entering combat will cancel this effect").</summary>
    public static bool BreaksOnCombat(EffectType t) => t is EffectType.Hide or EffectType.Invisibility or EffectType.PlayingDead;
}
