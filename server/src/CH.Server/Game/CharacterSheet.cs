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
        IReadOnlyList<(int SkillId, int Level)> skills, Balance balance)
    {
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

        MaxHealth = Kit.Health + (level - 1) * HealthPerLevel + (Vitality - baseVit) * 6;
        MaxEnergy = Kit.Energy + (level - 1) * EnergyPerLevel + (Focus - baseFoc) * 4;
        Health = progress.Health < 0 ? MaxHealth : Math.Clamp(progress.Health, 0, MaxHealth);
        Energy = progress.Energy < 0 ? MaxEnergy : Math.Clamp(progress.Energy, 0, MaxEnergy);

        var equipped = Equipped.Select(i => _balance.Items.GetValueOrDefault(i.TemplateId)).Where(i => i is not null).Select(i => i!).ToList();
        var weapon = Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Weapon) is { } w ? _balance.Items.GetValueOrDefault(w.TemplateId) : null;
        var offhand = Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Offhand) is { } o ? _balance.Items.GetValueOrDefault(o.TemplateId) : null;
        WeaponDamage = weapon?.Damage ?? 1;
        AttackSpeed = weapon is { Speed: > 0 } ? weapon.Speed : 1000;
        AttackRange = weapon is { Range: > 0 } ? weapon.Range + (offhand is { Subtype: "Arrow" } ? offhand.Range : 0) : 1f;
        Armour = equipped.Sum(i => i.Armour);
        Encumbrance = equipped.Sum(i => i.Weight);
        var ranged = weapon is { Range: > 3 };
        Attack = Kit.Attack + level + (ranged ? Dexterity - baseDex : Strength - baseStr);
        Defence = Kit.Defence + level + Armour / 2 + (Dexterity - baseDex) / 2;
    }

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

    /// <summary>Extra skill damage from Focus. <b>Approximated.</b></summary>
    public int SkillDamageBonus => (Focus - ClassKit.BaseAttributes.Foc) / 2;
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

    /// <summary>The highest rank this character may train a skill to now (rises at levels 15 and 30).</summary>
    public int MaxRank(int skillId) => _balance.Skills.TryGetValue(skillId, out var skill) && skill.Levels.Count > 0 ? skill.MaxRank(Level) : SkillMaxLevelAtStart;

    public IReadOnlyList<SkillEntry> SkillEntries() =>
        Skills.Select(s => new SkillEntry(s.SkillId, s.Level, MaxRank(s.SkillId), 0f)).ToList();

    public CharacterSummary Summary(int worldId)
    {
        var appearanceIds = Enumerable.Repeat(CharacterSummary.EmptySlot, CharacterSummary.EquipmentSlots).ToArray();
        foreach (var item in Equipped.Where(i => i.Slot < CharacterSummary.EquipmentSlots)) appearanceIds[item.Slot] = item.TemplateId;
        var c = Character;
        return new CharacterSummary((uint)c.Id, (uint)c.Id, worldId, c.Name, 0, "",
            new Appearance(c.Race, c.Face, c.Skin, c.SkinColour, c.Hair, c.HairColour, c.FaceAccessory, c.FaceAccessoryColour, c.Scale, c.Gender),
            appearanceIds, c.Zone, Level, 1, 1, c.Class, AttackRange, AttackSpeed, 0, 0, false);
    }

    public CharacterStats Stats()
    {
        var (vit, str, foc, dex) = (Vitality, Strength, Focus, Dexterity);
        return new CharacterStats
        {
            Health = Health, MaxHealth = MaxHealth, Energy = Energy, MaxEnergy = MaxEnergy,
            Attack = Attack, Defence = Defence, AttackSpeed = AttackSpeed, WeaponDamage = WeaponDamage, ArmourValue = Armour,
            Vitality = vit, Strength = str, Focus = foc, Dexterity = dex, Encumbrance = Encumbrance,
            SkillPoints = SkillPoints, AttributePoints = AttributePoints, Concentration = 50, MaxConcentration = 50,
            AttackRange = AttackRange, FastTravelItemLimit = 20, MaxSpeed = 5f, Scale = 1f,
            CompiledAbilities = Abilities(), CompiledSkills = Skills.Select(s => new IdValue(s.SkillId, s.Level)).ToList(),
        };
    }

    public IReadOnlyList<IdValue> Abilities() => Kit.Abilities.Select(a => new IdValue(a, 1)).ToList();

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

    public StartGameReply StartGame(int worldId) => new()
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
