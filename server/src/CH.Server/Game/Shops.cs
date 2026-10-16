using System.Text.Json;

namespace CH.Server.Game;

/// <summary>A merchant's shop from scripts/gen-world.py: category from its menu label, class gate, and the seller's zone.</summary>
public sealed record ShopInfo(int Id, string Category, IReadOnlyList<string> Classes, int? Zone, string Label);

/// <summary>
/// Merchant stock and prices. Both are server-only data the client never had (docs/economy-notes.md), so they're
/// <b>approximated</b>: each shop stocks its category's tradeable items around its zone's level band, and prices
/// grow with the item's level requirement. Selling pays a quarter of the buying price.
/// </summary>
public sealed class ShopCatalog
{
    private static readonly System.Text.RegularExpressions.Regex DevName = new(@"\bT\d\b");
    private static readonly EquipSlot[] ArmourSlots = [EquipSlot.Head, EquipSlot.Chest, EquipSlot.Leg, EquipSlot.Feet, EquipSlot.Hands];
    private static readonly EquipSlot[] JewellerySlots = [EquipSlot.RingR1, EquipSlot.BangleR, EquipSlot.Neck, EquipSlot.Misc];
    private static readonly EquipSlot[] FashionSlots = [EquipSlot.FashHead, EquipSlot.FashTorso, EquipSlot.FashLegs, EquipSlot.FashFeet, EquipSlot.FashHands];

    /// <summary>Zone level bands (as in scripts/chdata.py LEVELS).</summary>
    private static readonly Dictionary<int, (int Lo, int Hi)> Bands = new()
    {
        [2] = (1, 15), [93] = (5, 20), [9] = (15, 35), [22] = (3, 8), [4] = (8, 14), [5] = (12, 20), [6] = (16, 30), [3] = (25, 45),
        [11] = (35, 55), [10] = (45, 70), [12] = (60, 80), [13] = (70, 90), [7] = (80, 120), [15] = (100, 110), [16] = (108, 118),
        [17] = (116, 126), [18] = (124, 134), [19] = (132, 142), [20] = (140, 150), [24] = (150, 180), [27] = (180, 220),
    };

    private const int MaxStock = 40;

    private readonly Balance _balance;
    private readonly Dictionary<int, IReadOnlyList<int>> _stock = new();
    private readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> _extras;

    public IReadOnlyDictionary<int, ShopInfo> Shops { get; }

    public ShopCatalog(Balance balance, IReadOnlyDictionary<int, ShopInfo> shops, IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>>? extras = null,
        IReadOnlySet<int>? replaced = null)
    {
        _balance = balance;
        Shops = shops;
        _extras = extras ?? new Dictionary<int, IReadOnlyDictionary<int, int>>();
        _replaced = replaced ?? new HashSet<int>();
    }

    /// <summary>Shops whose whole stock is their shop-extras.json list ("replace": true), nothing generated.</summary>
    private readonly IReadOnlySet<int> _replaced;

    /// <summary>The shops shop-extras.json marks "replace": true.</summary>
    public static IReadOnlySet<int> LoadReplaced(string? path)
    {
        var replaced = new HashSet<int>();
        if (path is null || !File.Exists(path)) return replaced;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var group in doc.RootElement.GetProperty("extras").EnumerateArray())
            if (group.TryGetProperty("replace", out var r) && r.GetBoolean())
                foreach (var shop in group.GetProperty("shops").EnumerateArray()) replaced.Add(shop.GetInt32());
        return replaced;
    }

    /// <summary>
    /// Our own fixed-price additions (server/content/shop-extras.json): shop id → (item template → price). They are added
    /// to the generated stock and sell at exactly that price.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> LoadExtras(string? path)
    {
        var extras = new Dictionary<int, Dictionary<int, int>>();
        if (path is null || !File.Exists(path)) return new Dictionary<int, IReadOnlyDictionary<int, int>>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var group in doc.RootElement.GetProperty("extras").EnumerateArray())
            foreach (var shop in group.GetProperty("shops").EnumerateArray().Select(s => s.GetInt32()))
                foreach (var item in group.GetProperty("items").EnumerateArray())
                {
                    if (!extras.TryGetValue(shop, out var prices)) extras[shop] = prices = new();
                    // No price: the usual formula (stored as 0).
                    prices[item.GetProperty("id").GetInt32()] = item.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                }
        return extras.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<int, int>)kv.Value);
    }

    /// <summary>The price in this shop: a fixed extra price if it has one, otherwise <see cref="BuyPrice(ItemInfo)"/>.</summary>
    public int BuyPrice(int shopId, ItemInfo item) =>
        _extras.TryGetValue(shopId, out var prices) && prices.TryGetValue(item.Id, out var price) && price > 0 ? price : BuyPrice(item);

    public static IReadOnlyDictionary<int, ShopInfo> Load(string? worldPath)
    {
        var shops = new Dictionary<int, ShopInfo>();
        if (worldPath is null || !File.Exists(worldPath)) return shops;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(worldPath));
        if (!doc.RootElement.TryGetProperty("shops", out var all)) return shops;
        foreach (var s in all.EnumerateObject())
        {
            var e = s.Value;
            shops[int.Parse(s.Name)] = new ShopInfo(int.Parse(s.Name), e.GetProperty("category").GetString() ?? "general",
                e.GetProperty("classes").EnumerateArray().Select(c => c.GetString() ?? "").ToList(),
                e.GetProperty("zone").ValueKind == JsonValueKind.Number ? e.GetProperty("zone").GetInt32() : null,
                e.GetProperty("label").GetString() ?? "");
        }
        return shops;
    }

    /// <summary>Buying price: grows with the level requirement; weapons and jewellery cost more. <b>Approximated.</b></summary>
    public int BuyPrice(ItemInfo item)
    {
        var level = Math.Max(1, item.LevelRequirement);
        double basePrice = 8 + 2.5 * Math.Pow(level, 1.6);
        var factor = item.Slot switch
        {
            EquipSlot.Weapon => 1.6,
            EquipSlot.Offhand => 1.2,
            EquipSlot.RingR1 or EquipSlot.RingR2 or EquipSlot.RingL1 or EquipSlot.RingL2 or EquipSlot.BangleR or EquipSlot.BangleL or EquipSlot.Neck => 1.3,
            EquipSlot.Mount => 40,
            _ when item.Slot.ToString().StartsWith("Fash", StringComparison.Ordinal) => 6,
            EquipSlot.Unequipable when item.Stackable => 0.15,
            _ => 1.0,
        };
        if (item.StarLevel >= 0) factor *= 2 + item.StarLevel;
        if (_balance.SkillTaughtBy(item) is not null) factor = 3;
        return Math.Max(1, (int)Math.Round(basePrice * factor));
    }

    public int SellPrice(ItemInfo item) => Math.Max(1, BuyPrice(item) / 4);

    /// <summary>The template ids a shop sells, built once per shop (deterministic).</summary>
    public IReadOnlyList<int> Stock(int shopId)
    {
        if (_stock.TryGetValue(shopId, out var cached)) return cached;
        if (_replaced.Contains(shopId))
            return _stock[shopId] = (_extras.GetValueOrDefault(shopId)?.Keys.Where(_balance.Items.ContainsKey) ?? []).ToList();
        var shop = Shops.GetValueOrDefault(shopId) ?? new ShopInfo(shopId, "general", [], null, "");
        var (lo, hi) = shop.Zone is { } z && Bands.TryGetValue(z, out var band) ? band : (1, 40);
        var classes = shop.Classes;
        bool ForClass(ItemInfo i) => classes.Count == 0 || i.Classes.Count == 0 || classes.Any(c => i.Classes.Contains(c));
        bool Sellable(ItemInfo i) => i.Tradeable && !i.IsPlaceholder && !DevName.IsMatch(i.Name) && !i.ImportantItem;
        bool InBand(ItemInfo i) => i.LevelRequirement >= Math.Max(0, lo - 4) && i.LevelRequirement <= hi;
        var items = _balance.Items.Values.Where(Sellable);
        var rng = new Random(shopId * 104729);
        List<ItemInfo> pick = shop.Category switch
        {
            "armour" => Spread(items.Where(i => ArmourSlots.Contains(i.Slot) && i.StarLevel < 0 && InBand(i) && ForClass(i))),
            "weapon" => Spread(items.Where(i => (i.Slot is EquipSlot.Weapon or EquipSlot.Offhand) && i.StarLevel < 0 && InBand(i) && ForClass(i)
                                                && !i.Subtype.StartsWith("Novelty", StringComparison.Ordinal) && !i.Subtype.EndsWith("Novelty", StringComparison.Ordinal))),
            "jewellery" => Spread(items.Where(i => JewellerySlots.Contains(i.Slot) && i.StarLevel < 0 && InBand(i) && ForClass(i))),
            "book" => items.Where(i => _balance.SkillTaughtBy(i) is not null && ForClass(i) && i.LevelRequirement <= hi)
                          .OrderBy(i => i.LevelRequirement).ThenBy(i => i.Id).Take(MaxStock).ToList(),
            "fashion" => items.Where(i => FashionSlots.Contains(i.Slot) && i.LevelRequirement <= hi).OrderBy(_ => rng.Next()).Take(MaxStock).ToList(),
            "mount" => items.Where(i => i.Slot == EquipSlot.Mount).OrderBy(_ => rng.Next()).Take(20).ToList(),
            "food" => Food(items, hi).ToList(),
            "stamps" => [],
            _ => Potions(items, hi).Concat(Spread(items.Where(i => (ArmourSlots.Contains(i.Slot) || i.Slot == EquipSlot.Weapon)
                                                                    && i.StarLevel < 0 && InBand(i) && ForClass(i)), 24)).ToList(),
        };
        if (shop.Category == "potion") pick = Potions(items, hi).ToList();
        var result = pick.Select(i => i.Id).Concat(_extras.GetValueOrDefault(shopId)?.Keys.Where(_balance.Items.ContainsKey) ?? []).Distinct().ToList();
        _stock[shopId] = result;
        return result;

        // Evenly across the level band rather than the first few items.
        List<ItemInfo> Spread(IEnumerable<ItemInfo> source, int max = MaxStock)
        {
            var sorted = source.OrderBy(i => i.LevelRequirement).ThenBy(i => i.Id).ToList();
            if (sorted.Count <= max) return sorted;
            return Enumerable.Range(0, max).Select(k => sorted[(int)((long)k * (sorted.Count - 1) / (max - 1))]).ToList();
        }
    }

    /// <summary>
    /// Food merchants: cooking ingredients (not dishes), the standard fishing rods, concentration potions, and recipe
    /// books for dishes up to 40 levels past the band (cooking and fishing; docs/gathering-notes.md).
    /// </summary>
    private IEnumerable<ItemInfo> Food(IEnumerable<ItemInfo> items, int maxLevel)
    {
        var dishes = _balance.RecipeTemplates.Values.SelectMany(r => new[] { r.Failure, r.Result, r.CriticalResult, r.MasterResult }).ToHashSet();
        var books = _balance.RecipeTemplates.Values.Where(r => r.Level <= maxLevel + 40).Select(r => $"'{r.Name}' Recipe Book").ToHashSet();
        var list = items.ToList();
        return list.Where(i => i.Subtype == "Cooking" && i.Stackable && !dishes.Contains(i.Id) && !i.Name.Contains("Recipe", StringComparison.Ordinal)
                               && !i.Name.Contains("Chef", StringComparison.Ordinal) && !i.Name.Contains("Paddle", StringComparison.Ordinal))
            .Concat(list.Where(i => i.Subtype == "FishingRod" && i.Name.EndsWith("'s Fishing Rod", StringComparison.Ordinal)))
            .Concat(list.Where(i => i.Subtype == "FishingItem" && i.Name.EndsWith("Potion of Concentration", StringComparison.Ordinal)))
            .Concat(list.Where(i => books.Contains(i.Name)).OrderBy(i => i.Id).Take(24))
            .DistinctBy(i => i.Id);
    }

    private static IEnumerable<ItemInfo> Potions(IEnumerable<ItemInfo> items, int maxLevel) =>
        items.Where(i => i.Usable && i.Stackable && i.Subtype == "Consumable" && i.LevelRequirement <= maxLevel
                         && (i.Name.Contains("Potion", StringComparison.Ordinal) || i.Name.Contains("Elixir", StringComparison.Ordinal)
                             || i.Name.Contains("Tonic", StringComparison.Ordinal) || i.Name.Contains("Bandage", StringComparison.Ordinal)))
            .OrderBy(i => i.LevelRequirement).ThenBy(i => i.Id).Take(16);
}
