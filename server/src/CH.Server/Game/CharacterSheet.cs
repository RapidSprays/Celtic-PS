using System.Text.Json;
using CH.Persistence;
using CH.Protocol.Messages;

namespace CH.Server.Game;

/// <summary>
/// A character's full state for the wire: stats from class kit, level and equipped gear, item lists, skills and the
/// action bar. Stat values are <b>approximated</b> (docs/decisions.md); gear numbers (damage, armour, speed, range,
/// weight) come from the client's item templates.
/// </summary>
public sealed class CharacterSheet
{
    public const int SkillMaxLevelAtStart = 4; // template levels 0-4 need no minimum character level
    public const int HudSlotsPerSet = 8;
    private const int HealthPerLevel = 8;
    private const int EnergyPerLevel = 3;

    private readonly Balance _balance;

    public CharacterSheet(CharacterRecord character, CharacterProgress progress, IReadOnlyList<ItemRecord> items,
        IReadOnlyList<(int SkillId, int Level)> skills, Balance balance, TreeBonuses? tree = null)
    {
        Tree = tree ?? TreeBonuses.None;
        var t = Tree;
        Character = character;
        Progress = progress;
        Items = items;
        Skills = skills;
        _balance = balance;
        Kit = ClassKit.For(character.Class);

        var level = Math.Max(1, progress.Level);
        var (baseVit, baseStr, baseFoc, baseDex) = ClassKit.BaseAttributes;
        Strength = progress.Strength < 0 ? baseStr : progress.Strength;
        Dexterity = progress.Dexterity < 0 ? baseDex : progress.Dexterity;
        Focus = progress.Focus < 0 ? baseFoc : progress.Focus;
        Vitality = progress.Vitality < 0 ? baseVit : progress.Vitality;
        var spentAttributes = Strength + Dexterity + Focus + Vitality - (baseStr + baseDex + baseFoc + baseVit);
        AttributePoints = Math.Max(0, Progression.AttributePointsPerLevel * (level - 1) - spentAttributes);
        SkillPoints = Math.Max(0, Progression.SkillPointsPerLevel * (level - 1) - skills.Sum(k => k.Level));

        var gear = Equipped.Select(i => _balance.Items.GetValueOrDefault(i.TemplateId)).OfType<ItemInfo>().ToList();
        Gear = gear;
        // Passive-tree attributes count as bonuses (combined), never as spent base points.
        CombinedStrength = Strength + gear.Sum(g => g.StatBonus("Strength")) + (int)t["str"];
        CombinedDexterity = Dexterity + gear.Sum(g => g.StatBonus("Dexterity")) + (int)t["dex"];
        CombinedFocus = Focus + gear.Sum(g => g.StatBonus("Focus")) + (int)t["foc"];
        CombinedVitality = Vitality + gear.Sum(g => g.StatBonus("Vitality")) + (int)t["vit"];

        var healthKeystones = (t.Has(PassiveTree.AssassinsMark) ? 0.75 : 1) * (t.Has(PassiveTree.GlassCannon) ? 0.7 : 1);
        MaxHealth = Math.Max(1, (int)((Kit.Health + (level - 1) * HealthPerLevel + (CombinedVitality - baseVit) * 6 + gear.Sum(g => g.Bonus("HealthBonus")) + t["health"])
            * (1 + t["healthPct"] / 100) * healthKeystones));
        MaxEnergy = Math.Max(1, (int)((Kit.Energy + (level - 1) * EnergyPerLevel + (CombinedFocus - baseFoc) * 4 + gear.Sum(g => g.Bonus("EnergyBonus")) + t["energy"])
            * (1 + t["energyPct"] / 100)));
        Health = progress.Health < 0 ? MaxHealth : Math.Clamp(progress.Health, 0, MaxHealth);
        Energy = progress.Energy < 0 ? MaxEnergy : Math.Clamp(progress.Energy, 0, MaxEnergy);

        var equipped = Equipped.Select(i => _balance.Items.GetValueOrDefault(i.TemplateId)).Where(i => i is not null).Select(i => i!).ToList();
        var weapon = Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Weapon) is { } w ? _balance.Items.GetValueOrDefault(w.TemplateId) : null;
        var offhand = Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Offhand) is { } o ? _balance.Items.GetValueOrDefault(o.TemplateId) : null;
        WeaponDamage = weapon?.Damage ?? 1;
        AttackSpeed = weapon is { Speed: > 0 } ? weapon.Speed : 1000;
        AttackRange = weapon is { Range: > 0 } ? weapon.Range + (offhand is { Subtype: "Arrow" } ? offhand.Range : 0) : 1f;
        Armour = (int)((equipped.Sum(i => i.Armour) + t["armour"]) * (1 + t["armourPct"] / 100)
            * (t.Has(PassiveTree.IronHide) ? 1.5 : 1) * (t.Has(PassiveTree.Windrunner) ? 0.7 : 1));
        Encumbrance = equipped.Sum(i => i.Weight);
        var ranged = weapon is { Range: > 3 };
        Attack = (int)((Kit.Attack + level + (ranged ? CombinedDexterity - baseDex : CombinedStrength - baseStr) + gear.Sum(g => g.Bonus("AttackBonus")) + t["attack"])
            * (1 + t["attackPct"] / 100));
        Defence = (int)((Kit.Defence + level + Armour / 2 + (CombinedDexterity - baseDex) / 2 + gear.Sum(g => g.Bonus("DefenceBonus")) + t["defence"])
            * (1 + t["defencePct"] / 100));
    }

    /// <summary>Regen per tick from everything equipped (mount and pet included): always, and out of combat only.</summary>
    public (int Health, int Energy, int HealthOutOfCombat, int EnergyOutOfCombat) GearRegenPerTick
    {
        get
        {
            var all = Gear.Select(GearRegen.Of).Where(r => r.Any).ToList();
            return (all.Where(r => !r.OutOfCombatOnly).Sum(r => r.Health), all.Where(r => !r.OutOfCombatOnly).Sum(r => r.Energy),
                all.Where(r => r.OutOfCombatOnly).Sum(r => r.Health), all.Where(r => r.OutOfCombatOnly).Sum(r => r.Energy));
        }
    }

    /// <summary>The character's passive-tree bonuses (docs/passive-tree.md).</summary>
    public TreeBonuses Tree { get; }

    /// <summary>Base 5% critical strike chance, plus passives; Assassin's Mark doubles it.</summary>
    public double CritChance => (0.05 + Tree["critChance"] / 100) * (Tree.Has(PassiveTree.AssassinsMark) ? 2 : 1);

    /// <summary>Base ×1.5 on a critical strike; passives add to the extra, Assassin's Mark doubles the extra.</summary>
    public double CritMultiplier => 1 + (0.5 + Tree["critMulti"] / 100) * (Tree.Has(PassiveTree.AssassinsMark) ? 2 : 1);

    public double AttackDamageFactor => (1 + (Tree["damagePct"] + Tree["attackDamagePct"]) / 100) * (Tree.Has(PassiveTree.GlassCannon) ? 1.3 : 1);

    public double SkillDamageFactor(int skillId) =>
        (1 + (Tree["damagePct"] + Tree["skillDamagePct"] + Tree.For(_balance.SkillTags(skillId)).DamagePct) / 100) * (Tree.Has(PassiveTree.GlassCannon) ? 1.3 : 1);

    public double HealFactor(int skillId) => 1 + (Tree["healPct"] + Tree.For(_balance.SkillTags(skillId)).DamagePct) / 100;

    public double RechargeFactor(int skillId) => Math.Max(0, 1 - Tree.For(_balance.SkillTags(skillId)).CooldownPct / 100);

    public int EnergyCost(int skillId, int cost) => (int)Math.Ceiling(cost * Math.Max(0, 1 - Tree.For(_balance.SkillTags(skillId)).CostPct / 100));

    /// <summary>Extra chance for monsters to miss; Iron Hide means they never miss.</summary>
    public double EvadeChance => Tree["evadePct"] / 100;

    public int AttackSpeedPercent => (int)Tree["attackSpeedPct"] + (Tree.Has(PassiveTree.Windrunner) ? 25 : 0);

    public int RunSpeedPercent => (int)Tree["runSpeedPct"] + (Tree.Has(PassiveTree.Windrunner) ? 25 : 0);

    /// <summary>
    /// The rank a skill works at: trained rank, plus small gear SkillBonuses (large ones are flat damage), plus passive
    /// ranks. The client indexes Levels[compiled], so stay inside the (extended) table.
    /// </summary>
    public int EffectiveRank(int skillId, int trained) => Math.Clamp(
        trained + Gear.SelectMany(g => g.SkillBonuses).Where(b => b.Id == skillId && Math.Abs(b.Amount) <= 10).Sum(b => b.Amount)
            + Tree.For(_balance.SkillTags(skillId)).Ranks,
        0, Math.Max(0, (_balance.Skills.GetValueOrDefault(skillId)?.Levels.Count ?? 1) - 1));

    public CharacterRecord Character { get; }
    public CharacterProgress Progress { get; }
    public IReadOnlyList<ItemRecord> Items { get; }
    public IReadOnlyList<(int SkillId, int Level)> Skills { get; }
    public ClassKit Kit { get; }

    public int Level => Math.Max(1, Progress.Level);
    public int Strength { get; }
    public int Dexterity { get; }
    public int Focus { get; }
    public int Vitality { get; }

    /// <summary>Unspent points. Earned per level (<see cref="Progression"/>) minus what's been spent, so level changes stay consistent.</summary>
    public int AttributePoints { get; }
    public int SkillPoints { get; }

    /// <summary>Base attributes plus equipment PrimaryStatBonuses: what the stats page shows and what stats derive from.</summary>
    public int CombinedStrength { get; }
    public int CombinedDexterity { get; }
    public int CombinedFocus { get; }
    public int CombinedVitality { get; }

    /// <summary>The equipped mount (slot 22), if any. The client shows the player riding it (MountsManager.CheckForMount).</summary>
    public ItemInfo? Mount => Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Mount) is { } m ? _balance.Items.GetValueOrDefault(m.TemplateId) : null;

    /// <summary>Run speed comes from the "Mounted" status effect while riding (GameServer.Pets), not from the sheet.</summary>
    public int MountSpeedPercent => 0;

    /// <summary>Templates of everything equipped.</summary>
    public IReadOnlyList<ItemInfo> Gear { get; }

    /// <summary>Extra skill damage from Focus. <b>Approximated.</b></summary>
    public int SkillDamageBonus => (CombinedFocus - ClassKit.BaseAttributes.Foc) / 2;

    /// <summary>Skills granted by equipped items (EquipSkillID), usable while worn, not trainable.</summary>
    public IReadOnlyList<int> EquipSkills => Gear.Where(g => g.EquipSkillId > 0 && _balance.Skills.ContainsKey(g.EquipSkillId))
        .Select(g => g.EquipSkillId).Distinct().Where(id => Skills.All(k => k.SkillId != id)).ToList();

    /// <summary>Learned skills plus equipment skills, as (id, rank).</summary>
    public IReadOnlyList<(int SkillId, int Level)> UsableSkills => Skills.Concat(EquipSkills.Select(id => (id, 0))).ToList();
    public int MaxHealth { get; }
    public int MaxEnergy { get; }
    public int Health { get; }
    public int Energy { get; }
    public int Attack { get; }
    public int Defence { get; }
    public int WeaponDamage { get; }
    public int AttackSpeed { get; }
    public float AttackRange { get; }
    public int Armour { get; }
    public int Encumbrance { get; }

    public IEnumerable<ItemRecord> Equipped => Items.Where(i => i.Slot >= 0);

    public IEnumerable<ItemRecord> Backpack => Items.Where(i => i.Slot == ItemRecord.Backpack);

    public static ItemSlot ToWire(ItemRecord i) => new((int)i.Id, i.TemplateId, i.Quantity, i.Bound, i.Charges, i.Favourite);

    /// <summary>The 25 equipment entries, empty slots as template -1.</summary>
    public IReadOnlyList<ItemSlot> EquipmentSlots()
    {
        var slots = Enumerable.Repeat(ItemSlot.Empty, CharacterSummary.EquipmentSlots).ToArray();
        foreach (var item in Equipped.Where(i => i.Slot < CharacterSummary.EquipmentSlots)) slots[item.Slot] = ToWire(item);
        return slots;
    }

    public IReadOnlyList<ItemSlot> BackpackSlots() => Backpack.Select(ToWire).ToList();

    public InventoryWithMoney InventoryWithMoney() => new(Progress.Coins, BackpackSlots());

    /// <summary>The highest rank a skill can reach: the end of its (extended, ungated) level table (see <see cref="SkillLevels"/>).</summary>
    public int MaxRank(int skillId) => _balance.Skills.TryGetValue(skillId, out var skill) && skill.Levels.Count > 0 ? skill.MaxRank(Level) : SkillMaxLevelAtStart;

    public IReadOnlyList<SkillEntry> SkillEntries() =>
        Skills.Select(s => new SkillEntry(s.SkillId, s.Level, MaxRank(s.SkillId), 0f))
            .Concat(EquipSkills.Select(id => new SkillEntry(id, 0, 0, 0f)))
            .ToList();

    public CharacterSummary Summary(int worldId)
    {
        var appearanceIds = Enumerable.Repeat(CharacterSummary.EmptySlot, CharacterSummary.EquipmentSlots).ToArray();
        foreach (var item in Equipped.Where(i => i.Slot < CharacterSummary.EquipmentSlots)) appearanceIds[item.Slot] = item.TemplateId;
        var c = Character;
        return new CharacterSummary((uint)c.Id, (uint)c.Id, worldId, c.Name, 0, "",
            Appearance(), appearanceIds, c.Zone, Level, 1, 1, c.Class, AttackRange, AttackSpeed, 0, 0, false);
    }

    public Appearance Appearance()
    {
        var c = Character;
        return new Appearance(c.Race, c.Face, c.Skin, c.SkinColour, c.Hair, c.HairColour, c.FaceAccessory, c.FaceAccessoryColour, c.Scale, c.Gender,
            Progress.ShowHeadgear, Progress.ShowFashion);
    }

    public CharacterStats Stats()
    {
        var (vit, str, foc, dex) = (CombinedVitality, CombinedStrength, CombinedFocus, CombinedDexterity);
        return new CharacterStats
        {
            Health = Health, MaxHealth = MaxHealth, Energy = Energy, MaxEnergy = MaxEnergy,
            Attack = Attack, Defence = Defence, AttackSpeed = AttackSpeed, WeaponDamage = WeaponDamage, ArmourValue = Armour,
            Vitality = vit, Strength = str, Focus = foc, Dexterity = dex, Encumbrance = Encumbrance,
            SkillPoints = SkillPoints, AttributePoints = AttributePoints, Concentration = 50, MaxConcentration = 50,
            AttackRange = AttackRange, FastTravelItemLimit = FastTravelItemLimit, SoloBankItemLimit = BankSlots, MaxSpeed = 5f * (100 + MountSpeedPercent) / 100f, Scale = 1f,
            CompiledAbilities = Abilities(),
            CompiledSkills = UsableSkills.Select(s => new IdValue(s.SkillId, EffectiveRank(s.SkillId, s.Level))).ToList(),
            Avoidances = Gear.SelectMany(g => g.AvoidanceBonuses).GroupBy(a => a.Id).Select(g => new IdValue(g.Key, g.Sum(a => a.Amount))).ToList(),
            Bonuses = GearBonuses(),
        };
    }

    /// <summary>Ability levels: class abilities at 1, plus equipment AbilityBonuses (also for abilities the class lacks).</summary>
    /// <summary>Abilities learnt from trainers, on top of the class's own.</summary>
    public IReadOnlyList<int> LearnedAbilities { get; init; } = [];

    public IReadOnlyList<IdValue> Abilities()
    {
        var levels = Kit.Abilities.Concat(LearnedAbilities).Distinct().ToDictionary(a => a, _ => 1);
        foreach (var (id, amount) in Gear.SelectMany(g => g.AbilityBonuses)) levels[id] = levels.GetValueOrDefault(id) + amount;
        return levels.Select(kv => new IdValue(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Resist and other BonusTypes for the stats page, as client BonusType ids (see <see cref="BonusTypeIds"/>).</summary>
    public IReadOnlyList<IdValue> GearBonuses() =>
        Gear.SelectMany(g => g.Bonuses).Where(b => BonusTypeIds.ContainsKey(b.Type))
            .GroupBy(b => BonusTypeIds[b.Type]).Select(g => new IdValue(g.Key, g.Sum(b => b.Amount))).ToList();

    /// <summary>Client BonusType enum values by name. Filled from the client enum (Balance.Model BonusType).</summary>
    /// <summary>
    /// Client BonusType values (Balance.Model/BonusType.cs) for the resists shown on the Abilities screen. Attack, defence,
    /// health and energy bonuses (10-13) are folded into those stats instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> BonusTypeIds = new Dictionary<string, int>
    {
        ["PiercingResist"] = 0, ["SlashingResist"] = 1, ["CrushingResist"] = 2, ["HeatResist"] = 3, ["ColdResist"] = 4,
        ["MagicResist"] = 5, ["PoisonResist"] = 6, ["DivineResist"] = 7, ["ChaosResist"] = 8, ["FishingResist"] = 15,
        ["ConcentrationBonus"] = 16,
    };

    /// <summary>XP progress into the current level, 0-100.</summary>
    public float XpPercent => Math.Clamp(100f * Progress.Xp / Progression.XpToNextLevel(Level), 0f, 100f);

    public IReadOnlyList<HudSet> Hud()
    {
        if (!string.IsNullOrEmpty(Progress.Hud))
        {
            var saved = JsonSerializer.Deserialize<List<HudSet>>(Progress.Hud);
            if (saved is { Count: > 0 }) return saved;
        }
        return DefaultHud();
    }

    /// <summary>Starter skills on set 0, a potion in the last slot, set 1 empty. The client needs at least one set.</summary>
    public IReadOnlyList<HudSet> DefaultHud()
    {
        var set0 = Enumerable.Repeat(new IdValue(0, -1), HudSlotsPerSet).ToArray();
        for (var i = 0; i < Math.Min(Skills.Count, HudSlotsPerSet - 1); i++) set0[i] = new IdValue(1, Skills[i].SkillId);
        if (Backpack.FirstOrDefault(i => i.TemplateId == ClassKit.MinorHealthPotion) is { } potion)
            set0[HudSlotsPerSet - 1] = new IdValue(2, (int)potion.Id);
        return [new HudSet(0, set0), new HudSet(1, Enumerable.Repeat(new IdValue(0, -1), HudSlotsPerSet).ToArray())];
    }

    /// <param name="teleports">discovered leystones (PlayerSpawnPoint ids)</param>
    /// <param name="completedQuests">quests the client should treat as done (zone-exit gates; quests aren't implemented)</param>
    /// <summary>
    /// Backpack size above which fast travel needs a Leystone Fragment (client default 20). The private server makes
    /// travel free.
    /// </summary>
    public const int FastTravelItemLimit = 10000;

    /// <summary>Personal bank capacity (stats block soloBankItemLimit). <b>Approximated.</b></summary>
    public const int BankSlots = 100;

    public StartGameReply StartGame(int worldId, IReadOnlyList<int>? teleports = null, IReadOnlyList<int>? completedQuests = null,
        IReadOnlyList<ActiveQuest>? activeQuests = null) => new()
    {
        Platinum = Progress.Platinum,
        Character = Summary(worldId),
        Experience = XpPercent,
        Abilities = Abilities(),
        Skills = SkillEntries(),
        Stats = Stats(),
        Level = Level,
        Coins = Progress.Coins,
        Equipment = EquipmentSlots(),
        Inventory = BackpackSlots(),
        HudSets = Hud(),
        BaseVitality = Vitality,
        BaseStrength = Strength,
        BaseFocus = Focus,
        BaseDexterity = Dexterity,
        X = Character.X,
        Y = Character.Y,
        Z = Character.Z,
        Direction = Character.Direction,
        KnownZones = [Character.Zone],
        Teleports = teleports ?? [],
        CompletedQuests = completedQuests ?? [],
        ActiveQuests = activeQuests ?? [],
    };

    public static string SerializeHud(IReadOnlyList<HudSet> sets) => JsonSerializer.Serialize(sets);
}

/// <summary>Levelling curve. <b>Approximated</b>: the client ships none.</summary>
public static class Progression
{
    public const int MaxLevel = 250;
    public const int AttributePointsPerLevel = 3;
    public const int SkillPointsPerLevel = 1;

    public static int XpToNextLevel(int level) => (int)(100 * Math.Pow(Math.Max(1, level), 1.6));
}
