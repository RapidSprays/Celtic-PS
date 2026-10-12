namespace CH.Server.Game;

/// <summary>
/// Monster equipment drops. The real loot sets and tables are server-only (docs/economy-notes.md), so drops are
/// <b>approximated</b> from the client's item templates: tradeable, non-starred, level-gated equipment near the
/// monster's level, leaning towards the killer's class. Bosses always drop.
/// </summary>
public sealed class LootTable
{
    private static readonly HashSet<EquipSlot> DropSlots =
    [
        EquipSlot.Weapon, EquipSlot.Offhand, EquipSlot.Head, EquipSlot.Chest, EquipSlot.Leg, EquipSlot.Feet, EquipSlot.Hands,
        EquipSlot.Neck, EquipSlot.RingR1, EquipSlot.RingR2, EquipSlot.RingL1, EquipSlot.RingL2, EquipSlot.BangleR, EquipSlot.BangleL,
    ];

    private static readonly System.Text.RegularExpressions.Regex DevName = new(@"\bT\d\b");

    /// <summary>Drop pool sorted by level requirement.</summary>
    private readonly List<ItemInfo> _pool;
    private readonly int[] _levels;

    public LootTable(Balance balance)
    {
        _pool = balance.Items.Values
            .Where(i => DropSlots.Contains(i.Slot) && i.StarLevel < 0 && i.Tradeable && !i.IsPlaceholder && !DevName.IsMatch(i.Name)
                        && i.LevelRequirement >= 1)
            .OrderBy(i => i.LevelRequirement).ThenBy(i => i.Id)
            .ToList();
        _levels = _pool.Select(i => i.LevelRequirement).ToArray();
    }

    public int PoolSize => _pool.Count;

    /// <summary>Chance of an equipment drop per kill, by star power; bosses (4+) always drop, more the bigger they are.</summary>
    public static (double Chance, int Count) DropsFor(int power) => power switch
    {
        <= 1 => (0.10, 1),
        2 => (0.18, 1),
        3 => (0.30, 1),
        4 => (1.0, 1),
        5 => (1.0, 2),
        _ => (1.0, 3),
    };

    /// <summary>Rolls this kill's equipment drops: items with a level requirement within [level−6, level+1].</summary>
    public List<int> Roll(int mobLevel, int power, CharacterClass killerClass, Random rng)
    {
        var drops = new List<int>();
        var (chance, count) = DropsFor(power);
        if (_pool.Count == 0 || rng.NextDouble() >= chance) return drops;
        var (lo, hi) = Window(Math.Max(1, mobLevel - 6), mobLevel + 1);
        if (lo >= hi) (lo, hi) = Window(1, Math.Max(2, mobLevel + 1)); // nothing that close: anything up to its level
        if (lo >= hi) return drops;
        for (var i = 0; i < count; i++)
        {
            // Two in three drops are something the killer's class can use.
            var classTries = rng.NextDouble() < 0.65 ? 20 : 0;
            ItemInfo item;
            var tries = 0;
            do item = _pool[rng.Next(lo, hi)];
            while (tries++ < classTries && item.Classes.Count > 0 && !item.Classes.Contains(killerClass.ToString()));
            drops.Add(item.Id);
        }
        return drops;
    }

    private (int Lo, int Hi) Window(int minLevel, int maxLevel) => (FirstAtLeast(minLevel), FirstAtLeast(maxLevel + 1));

    /// <summary>Index of the first pool item whose level requirement is ≥ <paramref name="level"/>.</summary>
    private int FirstAtLeast(int level)
    {
        int lo = 0, hi = _levels.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_levels[mid] < level) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
