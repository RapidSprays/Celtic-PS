namespace CH.Server.Game;

/// <summary>
/// Picks equipment for a class and level from the client's item templates: for each combat slot, the strongest item
/// the class can wear at that level. Used by the .gearset GM command.
/// </summary>
public static class GearPlanner
{
    /// <summary>Each class's signature weapon type, preferred when one exists for the level.</summary>
    private static readonly IReadOnlyDictionary<CharacterClass, string[]> PreferredWeapons = new Dictionary<CharacterClass, string[]>
    {
        [CharacterClass.Rogue] = ["Dagger"],
        [CharacterClass.Warrior] = ["Sword", "Axe", "Blunt"],
        [CharacterClass.Mage] = ["Wand"],
        [CharacterClass.Druid] = ["Staff", "Totem"],
        [CharacterClass.Ranger] = ["Bow"],
    };

    /// <summary>Each class's armour type, preferred for head/chest/legs/feet/hands when one exists for the level.</summary>
    private static readonly IReadOnlyDictionary<CharacterClass, string> PreferredArmour = new Dictionary<CharacterClass, string>
    {
        [CharacterClass.Rogue] = "Leather",
        [CharacterClass.Ranger] = "Leather",
        [CharacterClass.Warrior] = "Plate",
        [CharacterClass.Mage] = "Cloth",
        [CharacterClass.Druid] = "Cloth",
    };

    private static readonly EquipSlot[] Armour = [EquipSlot.Head, EquipSlot.Chest, EquipSlot.Leg, EquipSlot.Feet, EquipSlot.Hands];

    private static readonly System.Text.RegularExpressions.Regex DevName = new(@"\bT\d\b");

    private static readonly EquipSlot[] Rings = [EquipSlot.RingR1, EquipSlot.RingR2, EquipSlot.RingL1, EquipSlot.RingL2];
    private static readonly EquipSlot[] Bangles = [EquipSlot.BangleR, EquipSlot.BangleL];

    public static bool Usable(ItemInfo item, CharacterClass cls, int level, byte gender) =>
        !item.IsPlaceholder && !DevName.IsMatch(item.Name)
        && (item.Classes.Count == 0 || item.Classes.Contains(cls.ToString()))
        && item.LevelRequirement <= level
        && item.Requirements.All(r => r.Type != "GenderRequirement" || r.Amount == gender);

    /// <summary>Rough strength of an item for its slot: level tier first, then stars, stats, damage and armour.</summary>
    public static double Score(ItemInfo i) =>
        i.LevelRequirement * 1000.0 + i.StarLevel * 100 + i.StatBonuses.Sum(b => b.Amount) + i.Damage * 2 + i.Armour
        + i.Bonuses.Where(b => b.Type.EndsWith("Bonus")).Sum(b => b.Amount) / 10.0;

    /// <summary>The best kit: slot → item. Two-handed weapons leave the offhand empty.</summary>
    public static Dictionary<int, ItemInfo> Plan(Balance balance, CharacterClass cls, int level, byte gender)
    {
        var usable = balance.Items.Values.Where(i => i.Slot >= EquipSlot.Weapon && i.Slot <= EquipSlot.Neck && Usable(i, cls, level, gender)).ToList();
        var kit = new Dictionary<int, ItemInfo>();

        var weapons = usable.Where(i => i.Slot == EquipSlot.Weapon && i.Damage > 0).ToList();
        var preferred = PreferredWeapons.GetValueOrDefault(cls) ?? [];
        var weapon = weapons.Where(w => preferred.Contains(w.Subtype)).OrderByDescending(Score).FirstOrDefault()
                     ?? weapons.OrderByDescending(Score).FirstOrDefault();
        if (weapon is not null) kit[(int)EquipSlot.Weapon] = weapon;

        foreach (var slot in new[] { EquipSlot.Head, EquipSlot.Chest, EquipSlot.Leg, EquipSlot.Feet, EquipSlot.Hands, EquipSlot.Neck, EquipSlot.Offhand })
        {
            if (slot == EquipSlot.Offhand && weapon is not null && weapon.BlockedSlots.Contains((int)EquipSlot.Offhand)) continue;
            // Bows want arrows in the offhand; other weapons take a shield or offhand item.
            var candidates = usable.Where(i => i.Slot == slot);
            if (slot == EquipSlot.Offhand) candidates = weapon?.Subtype == "Bow" ? candidates.Where(i => i.Subtype == "Arrow") : candidates.Where(i => i.Subtype != "Arrow");
            var list = candidates.ToList();
            if (Armour.Contains(slot) && PreferredArmour.TryGetValue(cls, out var armourType) && list.Any(i => i.Subtype == armourType))
            {
                // Best of the class's armour type unless something else is a whole tier (10+ levels) better.
                var bestOwn = list.Where(i => i.Subtype == armourType).MaxBy(Score)!;
                list = list.Where(i => i.Subtype == armourType || i.LevelRequirement >= bestOwn.LevelRequirement + 10).ToList();
            }
            if (list.OrderByDescending(Score).FirstOrDefault() is { } best) kit[(int)slot] = best;
        }

        // Ring items are all tagged RingR1 in the data and fill slots 8-11; bangles fill 12-13. Prefer different items.
        Fill(usable.Where(i => Rings.Contains(i.Slot)), Rings, kit);
        Fill(usable.Where(i => Bangles.Contains(i.Slot)), Bangles, kit);
        return kit;
    }

    private static void Fill(IEnumerable<ItemInfo> candidates, EquipSlot[] slots, Dictionary<int, ItemInfo> kit)
    {
        var best = candidates.OrderByDescending(Score).DistinctBy(i => i.Name).Take(slots.Length).ToList();
        for (var i = 0; i < best.Count; i++) kit[(int)slots[i]] = best[i];
    }
}
