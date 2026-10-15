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

/// <summary>
/// The official loot tables, from Master's Grimoire (scripts/import-grimoire.py writes loot.json beside the balance JSON).
/// A monster rolls each of its loot sets (some several times); a set picks one table by weight, -1 being "nothing";
/// the table picks one item and quantity by weight. Monsters without an entry keep the approximated <see cref="LootTable"/>.
/// </summary>
public sealed class OfficialLoot
{
    private readonly Dictionary<int, (int Set, int Times)[]> _mobs;
    private readonly Dictionary<int, (int Table, int Weight)[]> _sets;
    private readonly Dictionary<int, (int Item, int Quantity, int Weight)[]> _tables;

    private OfficialLoot(Dictionary<int, (int, int)[]> mobs, Dictionary<int, (int, int)[]> sets, Dictionary<int, (int, int, int)[]> tables)
    {
        _mobs = mobs;
        _sets = sets;
        _tables = tables;
    }

    public static OfficialLoot Empty { get; } = new([], [], []);

    public int MobCount => _mobs.Count;

    public static OfficialLoot Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Empty;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        Dictionary<int, T[]> Read<T>(string name, Func<System.Text.Json.JsonElement, T> row) => root.GetProperty(name).EnumerateObject()
            .ToDictionary(e => int.Parse(e.Name), e => e.Value.EnumerateArray().Select(row).ToArray());
        return new OfficialLoot(
            Read("mobs", r => (r[0].GetInt32(), r[1].GetInt32())),
            Read("sets", r => (r[0].GetInt32(), r[1].GetInt32())),
            Read("tables", r => (r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32())));
    }

    public bool Has(int mobTemplate) => _mobs.ContainsKey(mobTemplate);

    /// <summary>This kill's drops as (item, quantity); items the client doesn't know are left out.</summary>
    public List<(int Item, int Quantity)> Roll(int mobTemplate, Balance balance, Random rng)
    {
        var drops = new List<(int, int)>();
        if (!_mobs.TryGetValue(mobTemplate, out var sets)) return drops;
        foreach (var (set, times) in sets)
        {
            if (!_sets.TryGetValue(set, out var tables)) continue;
            for (var i = 0; i < times; i++)
            {
                var table = Pick(tables, t => t.Weight, rng).Table;
                if (!_tables.TryGetValue(table, out var items)) continue; // -1: nothing
                var (item, quantity, _) = Pick(items, t => t.Weight, rng);
                if (item > 0 && quantity > 0 && balance.Items.ContainsKey(item)) drops.Add((item, quantity));
            }
        }
        return drops;
    }

    private static T Pick<T>(T[] entries, Func<T, int> weight, Random rng)
    {
        var roll = rng.NextDouble() * entries.Sum(e => Math.Max(0, weight(e)));
        foreach (var e in entries)
        {
            roll -= Math.Max(0, weight(e));
            if (roll < 0) return e;
        }
        return entries[^1];
    }
}
