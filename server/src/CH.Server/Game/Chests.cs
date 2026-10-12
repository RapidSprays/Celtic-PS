using System.Text.RegularExpressions;

namespace CH.Server.Game;

public enum ChestKind
{
    /// <summary>Mystery Chests: 1-3 rewards shown on the client's chest-opening screen.</summary>
    Mystery,
    /// <summary>Fixed contents ("contains five X", "Bag of 24 Pet Tokens"); "up to N" rolls 1-N.</summary>
    Fixed,
    /// <summary>One random item from a pool ("one random coloured pirate shirt", seasonal gifts, caches).</summary>
    OneOf,
    /// <summary>Everything in the pool (starter packs that list several items).</summary>
    Bundle,
}

/// <summary>One thing a chest can give: an item (with quantity range), coins or platinum.</summary>
public sealed record ChestReward(int TemplateId, int MinQty, int MaxQty, double Weight, int Coins = 0, int Platinum = 0, int MaxPlatinum = 0)
{
    public bool IsCurrency => TemplateId <= 0;
}

public sealed record ChestDefinition(int ItemId, string Name, ChestKind Kind, int MinRewards, int MaxRewards, IReadOnlyList<ChestReward> Pool, string Rule);

/// <summary>What one opening produced.</summary>
public sealed record ChestRoll(IReadOnlyList<(int TemplateId, int Quantity)> Items, int Coins, int Platinum);

/// <summary>
/// Openable container items ("legacy chests") and what they give. The client ships the containers but not their loot
/// tables (server-only LootSets), so contents are <b>reconstructed</b> from each item's own description and the
/// item templates it names; see docs/decisions.md. Which items are containers comes from their skill link (the
/// container skills catalogued from the client data).
/// </summary>
public sealed class ChestCatalog
{
    public const int MagicBox = 208, MagicBoxTest = 209, MagicBoxNew = 267;

    /// <summary>Container skill links (ItemTemplate.SkillLinkID), from the client's skill templates.</summary>
    public static readonly IReadOnlySet<int> ContainerLinks = new HashSet<int>
    {
        MagicBox, MagicBoxTest, MagicBoxNew,
        // random single-item boxes
        207, 246, 242, 254, 255, 256, 264, 265, 300, 301, 302, 303, 304, 310, 340, 344, 7862, 8068, 333,
        // event, seasonal, bounty and activity caches
        326, 307, 270, 20028, 20029, 327, 341, 8055, 346, 8054, 8153, 7409, 8225, 20088, 20020, 20103, 20031, 20073, 20074, 20075,
        // fixed-content packs, crates, sets, starter kits, bundles and token bags
        235, 236, 237, 238, 239, 240, 241, 247, 248, 249, 257, 258, 259, 260, 261, 262, 266, 311, 312, 313, 7378, 8493, 328, 331,
        7437, 8145, 8146, 206, 330, 332, 7576, 8069, 8235, 7874, 8067, 8462, 8463, 8464, 8465, 8466, 8468, 20008, 20019, 7907, 20033,
    };

    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["twenty-five"] = 25,
        ["thirty"] = 30, ["fifty"] = 50, ["hundred"] = 100,
    };

    private static readonly string[] Seasons = ["yule", "ostara", "beltane", "samhain", "rimebound", "midwinter", "winter", "lughnasadh", "imbolc"];

    private readonly Dictionary<int, ChestDefinition> _chests = new();

    public IReadOnlyDictionary<int, ChestDefinition> Chests => _chests;

    public ChestDefinition? For(int itemTemplateId) => _chests.GetValueOrDefault(itemTemplateId);

    public static ChestCatalog Build(Balance balance)
    {
        var catalog = new ChestCatalog();
        if (!balance.IsLoaded) return catalog;
        var index = new ItemIndex(balance);
        // Containers by skill link (token bags use 7874 / 8067), plus any other "Bag of N … Tokens" by name.
        foreach (var item in balance.Items.Values.Where(i => i.Usable && (ContainerLinks.Contains(i.SkillLinkId)
                     || (i.Name.StartsWith("Bag of ", StringComparison.Ordinal) && i.Name.Contains("Token", StringComparison.Ordinal)))))
        {
            var def = Classify(item, balance, index);
            if (def is not null && def.Pool.Count > 0) catalog._chests[item.Id] = def;
        }
        return catalog;
    }

    private static ChestDefinition? Classify(ItemInfo chest, Balance balance, ItemIndex index)
    {
        var name = chest.Name.ToLowerInvariant();
        var desc = chest.Description.ToLowerInvariant();

        // "Bag of 24 Pet Tokens", "Bag of up to 35 Silver Witchmarks", "Bag of Fourty-Eight Mount Tokens"
        foreach (var split in BagSplits(name))
            if (Count(split.Count) is { } bagCount && index.Find(split.Item) is { } bagItem)
                return Fixed(chest, bagItem, split.UpTo ? 1 : bagCount, bagCount, "bag of N");

        if (chest.SkillLinkId is MagicBox or MagicBoxTest or MagicBoxNew) return Mystery(chest, desc, index);
        if (name.Contains("bounty")) return Bounty(chest, index);

        // Starter kits: "contains a Backpack Expansion, 1 Sigil of Health and Energy, a Ressurrection Idol, … and 1 Super Combination Elixir!"
        if (ContainsList(desc, index) is { Count: > 1 } list)
            return new(chest.Id, chest.Name, ChestKind.Bundle, 1, 1, list.Select(x => new ChestReward(x.Id, x.Qty, x.Qty, 1)).ToList(), "contains list");
        if (name == "celtic heroes starter pack") // "containing sigils, backpack and bank expansions"
            return new(chest.Id, chest.Name, ChestKind.Bundle, 1, 1,
                new[] { "sigil of health", "sigil of energy", "backpack expansion", "bank expansion" }.Select(index.Find).OfType<int>()
                    .Select(id => new ChestReward(id, 1, 1, 1)).ToList(), "starter pack");

        // "contains five super combination elixirs", "contains fifty royal corrupted broadheads of agony", "contains 5-330 platinum"
        var range = Regex.Match(desc, @"(\d+)\s*-\s*(\d+) platinum");
        if (range.Success)
            return new(chest.Id, chest.Name, ChestKind.Fixed, 1, 1, [new ChestReward(0, 1, 1, 1, Platinum: int.Parse(range.Groups[1].Value), MaxPlatinum: int.Parse(range.Groups[2].Value))], "platinum range");
        foreach (Match m in Regex.Matches(desc, @"contains? (up to )?([\w-]+) (.+?)(?=\.|,| use| for| and |!|$)"))
        {
            var what = m.Groups[3].Value;
            if (what.Contains("random")) continue;
            if (Count(m.Groups[2].Value) is { } n && index.Find(what) is { } fixedItem)
                return Fixed(chest, fixedItem, m.Groups[1].Success ? 1 : n, n, "contains N X");
        }

        // "one random coloured pirate shirt", skill "Random Pirate Fashion"
        var random = Regex.Match(desc, @"one random (?:coloured |colored )?(.+?)(?=\.|,| use|!|$)");
        var skillName = balance.Skills.GetValueOrDefault(chest.SkillLinkId)?.Name ?? "";
        var noun = random.Success ? random.Groups[1].Value : Regex.Match(skillName, @"^Random (.+)$", RegexOptions.IgnoreCase).Groups[1].Value;
        if (!string.IsNullOrWhiteSpace(noun))
        {
            var pool = index.Matching(Singular(noun.ToLowerInvariant().Replace(" fashion", "").Trim()), equipmentOnly: true);
            if (pool.Count > 0) return OneOf(chest, pool, "one random X");
        }

        // Items the description names: packs that "contain" several things give them all; otherwise one of them
        // ("may contain the casket of whispers", "health or energy potion or … phoenix crest").
        var named = index.NamedIn(desc, exclude: chest.Id);
        if (named.Count > 1 && Regex.IsMatch(desc, @"^(this )?(pack |kit |set )?contains"))
            return new(chest.Id, chest.Name, ChestKind.Bundle, 1, 1, named.Select(id => new ChestReward(id, 1, 1, 1)).ToList(), "bundle");
        if (named.Count > 0)
        {
            // "…or for the very lucky there may be a Burnished Phoenix Crest": prizes named after "lucky" are rare.
            var lucky = desc.IndexOf("lucky", StringComparison.Ordinal);
            var weighted = named.Distinct().Select(id =>
            {
                var at = desc.IndexOf(balance.Items[id].Name.ToLowerInvariant(), StringComparison.Ordinal);
                return new ChestReward(id, 1, 1, lucky >= 0 && at > lucky ? 0.1 : 1);
            }).ToList();
            return new(chest.Id, chest.Name, ChestKind.OneOf, 1, 1, weighted, "named in description");
        }

        // Themed pools for the rest.
        var season = Seasons.FirstOrDefault(s => name.Contains(s) || desc.Contains(s));
        if (season is not null)
        {
            var seasonal = index.Matching(season, equipmentOnly: false).Where(id => id != chest.Id && !balance.Chests(id)).ToList();
            if (seasonal.Count > 0) return OneOf(chest, seasonal, $"seasonal ({season})");
        }
        if (name.Contains("fishing")) return OneOf(chest, index.Matching("bait", false).Concat(index.Matching("fishing", false)).Concat(index.Matching("fishing rod", true)).ToList(), "fishing cache");
        if (name.Contains("arcana")) return OneOf(chest, index.Matching("tarot", false).Concat(index.Matching("arcana", false)).Where(id => !balance.Chests(id)).ToList(), "arcana");
        if (name.Contains("cooking")) return OneOf(chest, index.Matching("spice", false).Concat(index.Matching("herb", false)).Concat(index.Matching("ingredient", false)).ToList(), "cooking cache");
        if (name.Contains("clam")) return OneOf(chest, index.Matching("pearl", false), "clam");
        return OneOf(chest, index.Elixirs, "generic");
    }

    private static ChestDefinition Mystery(ItemInfo chest, string desc, ItemIndex index)
    {
        var (min, max) = desc.Contains("up to four") ? (1, 4) : desc.Contains("up to 2") || desc.Contains("up to two") ? (1, 2)
            : Regex.IsMatch(desc, @"\b(one|a) random (reward|item)\b") && !desc.Contains("between") ? (1, 1) : (1, 3);
        var pool = new List<ChestReward>();
        // Items the description names (fashion, mounts, charms, instruments, tokens, eggs) are the chest's featured prizes.
        pool.AddRange(index.NamedIn(desc, exclude: chest.Id).Select(id => new ChestReward(id, 1, 1, 3)));
        pool.AddRange(index.Elixirs.Select(id => new ChestReward(id, 1, 3, 6.0 / Math.Max(1, index.Elixirs.Count))));
        var platinumCap = desc.Contains("4000") ? 4000 : 1000;
        pool.Add(new ChestReward(0, 1, 1, 2, Platinum: 10, MaxPlatinum: platinumCap));
        var grand = desc.Contains("2 million") || desc.Contains("two million") ? 2_000_000 : desc.Contains("million gold") ? 1_000_000 : 0;
        if (grand > 0) pool.Add(new ChestReward(0, 1, 1, 0.02, Coins: grand));
        pool.Add(new ChestReward(0, 1, 1, 1.5, Coins: 2500));
        return new(chest.Id, chest.Name, ChestKind.Mystery, min, max, pool, "mystery");
    }

    private static ChestDefinition Fixed(ItemInfo chest, int itemId, int min, int max, string rule) =>
        new(chest.Id, chest.Name, ChestKind.Fixed, 1, 1, [new ChestReward(itemId, min, max, 1)], rule);

    private static ChestDefinition OneOf(ItemInfo chest, IReadOnlyCollection<int> pool, string rule) =>
        new(chest.Id, chest.Name, ChestKind.OneOf, 1, 1, pool.Distinct().Where(id => id != chest.Id).Select(id => new ChestReward(id, 1, 1, 1)).ToList(), rule);

    private static ChestDefinition Bounty(ItemInfo chest, ItemIndex index)
    {
        var tier = chest.Name.Contains("Superior") || chest.Name.Contains("Epic") ? 3 : chest.Name.Contains("Elite") || !chest.Name.Contains("Standard") ? 2 : 1;
        var pool = index.Elixirs.Select(id => new ChestReward(id, 1, tier, 1.0 / Math.Max(1, index.Elixirs.Count))).ToList();
        if (index.Find("bag of gold") is { } bagOfGold) pool.Add(new ChestReward(bagOfGold, 1, tier, 0.5));
        pool.Add(new ChestReward(0, 1, 1, 0.5, Coins: 1000 * tier * tier));
        return new(chest.Id, chest.Name, ChestKind.OneOf, 1, 1, pool, "bounty bag");
    }

    private static readonly Dictionary<string, int> Tens = new() { ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90 };

    /// <summary>"5", "five", "twenty-five", "Fourty-Eight", "one hundred and twenty-eight".</summary>
    private static int? Count(string words)
    {
        var w = words.Trim().ToLowerInvariant();
        if (int.TryParse(w, out var n)) return n;
        if (Numbers.TryGetValue(w, out var simple)) return simple;
        var total = 0;
        foreach (var part in w.Replace(" and ", " ").Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "hundred") total = Math.Max(1, total) * 100;
            else if (Tens.TryGetValue(part, out var t)) total += t;
            else if (Numbers.TryGetValue(part, out var u)) total += u;
            else return null;
        }
        return total > 0 ? total : null;
    }

    /// <summary>Parses "contains a X, 2 Ys, 1 Z of A and B and 5 Ws" into items and quantities.</summary>
    private static List<(int Id, int Qty)>? ContainsList(string desc, ItemIndex index)
    {
        var m = Regex.Match(desc, @"contains (.+?)(?:!|\.|$)");
        if (!m.Success || !m.Groups[1].Value.Contains(',')) return null;
        var result = new List<(int, int)>();
        foreach (var raw in Regex.Split(m.Groups[1].Value, @",\s*|\s+and\s+(?=(?:a |an |\d+ |one |two |three |four |five |ten ))"))
        {
            var part = Regex.Match(raw.Trim(), @"^(?:and\s+)?(a|an|one|two|three|four|five|ten|\d+)\s+(.+)$");
            if (!part.Success) continue;
            var qty = Count(part.Groups[1].Value) ?? 1;
            var what = part.Groups[2].Value;
            if (index.Find(what) is { } id) result.Add((id, qty));
            else if (Regex.Match(what, @"^(.+ of )(\w+) and (\w+)$") is { Success: true } both)
            {
                // "Sigils of Health and Energy" → one of each
                foreach (var tail in new[] { both.Groups[2].Value, both.Groups[3].Value })
                    if (index.Find(both.Groups[1].Value + tail) is { } eachId) result.Add((eachId, qty));
            }
        }
        return result;
    }

    /// <summary>Ways to read "bag of [up to] &lt;count words&gt; &lt;item&gt;": the count is one or more leading words.</summary>
    private static IEnumerable<(bool UpTo, string Count, string Item)> BagSplits(string name)
    {
        var m = Regex.Match(name, @"^bag of (up to )?(.+)$");
        if (!m.Success) yield break;
        var words = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = 1; take < words.Length && take <= 5; take++)
            yield return (m.Groups[1].Success, string.Join(' ', words[..take]), string.Join(' ', words[take..]));
    }

    private static string Singular(string s) => s.EndsWith("ies") ? s[..^3] + "y" : s.EndsWith("es") && s.Length > 4 ? s[..^1] : s.EndsWith('s') ? s[..^1] : s;

    /// <summary>Rolls one opening.</summary>
    public static ChestRoll Roll(ChestDefinition chest, Random rng)
    {
        var items = new List<(int, int)>();
        int coins = 0, platinum = 0;
        if (chest.Kind == ChestKind.Bundle)
            return new ChestRoll(chest.Pool.Select(p => (p.TemplateId, rng.Next(p.MinQty, p.MaxQty + 1))).ToList(), 0, 0);
        var count = rng.Next(chest.MinRewards, chest.MaxRewards + 1);
        for (var i = 0; i < count; i++)
        {
            var pick = Pick(chest.Pool, rng);
            if (pick.Coins > 0) coins += pick.Coins;
            else if (pick.MaxPlatinum > 0)
            {
                // Low amounts are common, the cap is rare (skewed roll).
                var t = Math.Pow(rng.NextDouble(), 3);
                platinum += (int)(pick.Platinum + t * (pick.MaxPlatinum - pick.Platinum));
            }
            else items.Add((pick.TemplateId, rng.Next(pick.MinQty, pick.MaxQty + 1)));
        }
        return new ChestRoll(items, coins, platinum);
    }

    private static ChestReward Pick(IReadOnlyList<ChestReward> pool, Random rng)
    {
        var total = pool.Sum(p => p.Weight);
        var roll = rng.NextDouble() * total;
        foreach (var p in pool)
        {
            roll -= p.Weight;
            if (roll <= 0) return p;
        }
        return pool[^1];
    }

    /// <summary>Name lookups over item templates, skipping test and placeholder items.</summary>
    private sealed class ItemIndex
    {
        private readonly List<ItemInfo> _items;
        private readonly Dictionary<string, List<ItemInfo>> _byName;
        private readonly Dictionary<string, List<ItemInfo>> _bySquashedName;
        private readonly Dictionary<string, List<ItemInfo>> _byNormalizedName;
        private readonly Dictionary<(string, bool), List<int>> _matching = new();
        private int MaxNameWords { get; }

        private static string Squash(string s) => Regex.Replace(s, @"(\w)\1+", "$1");

        private readonly Balance _balance;

        public ItemIndex(Balance balance)
        {
            _balance = balance;
            _items = balance.Items.Values.Where(i => !i.IsPlaceholder).ToList();
            _byName = _items.GroupBy(i => i.Name.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.OrderBy(i => i.Id).ToList());
            _bySquashedName = _byName.GroupBy(kv => Squash(kv.Key)).ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).ToList());
            _byNormalizedName = _byName.GroupBy(kv => Normalize(kv.Key)).ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).OrderBy(i => i.Id).ToList());
            MaxNameWords = Math.Min(10, _byNormalizedName.Keys.Max(k => k.Count(c => c == ' ') + 1));
            Elixirs = _items.Where(i => Regex.IsMatch(i.Name, @"^(Super|Heroic) .*Elixir") && i.Usable && !balance.Chests(i.Id)).Select(i => i.Id).Distinct().ToList();
        }

        public List<int> Elixirs { get; }

        /// <summary>An exact item name, trying singular/plural forms; prefers stackable, lowest id.</summary>
        public int? Find(string phrase)
        {
            var p = phrase.Trim().ToLowerInvariant();
            var candidates = new[] { p, Singular(p), p.TrimEnd('s'), Regex.Replace(p, "elixirs", "elixir"), Regex.Replace(p, @"(\w+)s of ", "$1 of ") };
            foreach (var candidate in candidates)
                if (_byName.TryGetValue(candidate, out var found))
                    return found.OrderByDescending(i => i.Stackable).ThenBy(i => i.Id).First().Id;
            // Descriptions have typos ("Ressurrection Idol"): compare with doubled letters collapsed.
            foreach (var candidate in candidates.Select(Squash))
                if (_bySquashedName.TryGetValue(candidate, out var fuzzy))
                    return fuzzy.OrderByDescending(i => i.Stackable).ThenBy(i => i.Id).First().Id;
            return null;
        }

        /// <summary>Every item whose name contains the phrase (whole words).</summary>
        public List<int> Matching(string phrase, bool equipmentOnly)
        {
            if (string.IsNullOrWhiteSpace(phrase)) return [];
            var key = (phrase.Trim().ToLowerInvariant(), equipmentOnly);
            if (_matching.TryGetValue(key, out var cached)) return cached;
            var needle = key.Item1;
            var result = _items.Where(i => (!equipmentOnly || i.Slot >= EquipSlot.Weapon) && StartsWordIn(i.Name.ToLowerInvariant(), needle)).Select(i => i.Id).ToList();
            return _matching[key] = result;
        }

        /// <summary>Whole-word-start match ("yule" matches "Yuletide" and "Beoite Yule", not "Joyule").</summary>
        private static bool StartsWordIn(string haystack, string needle)
        {
            for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
                if (at == 0 || !char.IsLetterOrDigit(haystack[at - 1])) return true;
            return false;
        }

        private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9']+", " ").Trim();

        /// <summary>Items whose full names appear in a description, longest names first, without overlaps.</summary>
        public List<int> NamedIn(string description, int exclude)
        {
            // Look up runs of words in a name dictionary (longest runs first) instead of scanning every item name.
            var words = Normalize(description).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var used = new bool[words.Length];
            var found = new List<int>();
            for (var length = Math.Min(MaxNameWords, words.Length); length >= 1; length--)
            {
                for (var start = 0; start + length <= words.Length; start++)
                {
                    if (used.AsSpan(start, length).Contains(true)) continue;
                    var phrase = string.Join(' ', words, start, length);
                    if (phrase.Length < 6 || !_byNormalizedName.TryGetValue(phrase, out var items)) continue;
                    var pick = items.FirstOrDefault(i => i.Id != exclude && !_balance.Chests(i.Id) && !Regex.IsMatch(i.Name, @"\d+ Platinum"));
                    if (pick is null) continue;
                    found.Add(pick.Id); // other containers are never prizes (no "Mystery Chest" inside a Mystery Chest)
                    used.AsSpan(start, length).Fill(true);
                    if (found.Count >= 12) return found;
                }
            }
            return found;
        }
    }
}
