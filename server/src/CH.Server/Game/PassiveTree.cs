using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CH.Server.Game;

/// <summary>One stat line on a node, e.g. ("str", 15) or ("healthPct", 4).</summary>
public sealed record TreeStat(string Stat, double Amount);

/// <summary>A notable's change to a group of skills: a class's skills ("warrior") or healing skills ("heal").</summary>
public sealed record SkillMod(string Tag, double DamagePct, double CooldownPct, double CostPct, int Ranks);

public sealed record TreeNode(int Id, string Kind, string Name, int X, int Y, int Class, IReadOnlyList<TreeStat> Stats,
    IReadOnlyList<SkillMod> SkillMods, string? Keystone, IReadOnlyList<int> Links);

/// <summary>
/// The passive tree (server/content/passive-tree.json, built by scripts/gen-passive-tree.py; docs/passive-tree.md).
/// Points come at one per <see cref="PointsPerLevels"/> levels; each class starts at its own free start node, and a node
/// can be taken when it links to the start or a node already taken. The server owns the rules and applies the bonuses
/// (CharacterSheet, LiveStats, combat); the client mod only draws the layout it is sent.
/// </summary>
public sealed class PassiveTree
{
    public static readonly PassiveTree Empty = new([], 5, new Dictionary<string, string>(), "");

    /// <summary>Every stat a node may grant, with its tooltip text ({0} is the amount).</summary>
    public static readonly IReadOnlyDictionary<string, string> StatText = new Dictionary<string, string>
    {
        ["str"] = "+{0} Strength", ["dex"] = "+{0} Dexterity", ["foc"] = "+{0} Focus", ["vit"] = "+{0} Vitality",
        ["health"] = "+{0} maximum health", ["healthPct"] = "+{0}% maximum health",
        ["energy"] = "+{0} maximum energy", ["energyPct"] = "+{0}% maximum energy",
        ["attack"] = "+{0} attack", ["attackPct"] = "+{0}% attack",
        ["defence"] = "+{0} defence", ["defencePct"] = "+{0}% defence",
        ["armour"] = "+{0} armour", ["armourPct"] = "+{0}% armour",
        ["evadePct"] = "+{0}% chance for enemies to miss you",
        ["attackSpeedPct"] = "+{0}% attack speed", ["runSpeedPct"] = "+{0}% run speed",
        ["healthRegenPct"] = "Regenerate {0}% of maximum health every 3 seconds",
        ["energyRegenPct"] = "Regenerate {0}% of maximum energy every 3 seconds",
        ["critChance"] = "+{0}% critical strike chance", ["critMulti"] = "+{0}% critical strike damage",
        ["damagePct"] = "+{0}% damage", ["attackDamagePct"] = "+{0}% auto-attack damage",
        ["skillDamagePct"] = "+{0}% skill damage", ["healPct"] = "+{0}% healing",
        ["xpPct"] = "+{0}% experience", ["goldPct"] = "+{0}% gold",
    };

    /// <summary>Keystone rules, applied in CharacterSheet and combat (see each use).</summary>
    public const string IronHide = "ironHide", AssassinsMark = "assassinsMark", Windrunner = "windrunner",
        GlassCannon = "glassCannon", LifeTap = "lifeTap";

    public static readonly IReadOnlySet<string> KnownKeystones = new HashSet<string> { IronHide, AssassinsMark, Windrunner, GlassCannon, LifeTap };

    private PassiveTree(IReadOnlyList<TreeNode> nodes, int pointsPerLevels, IReadOnlyDictionary<string, string> keystones, string version)
    {
        Nodes = nodes.ToDictionary(n => n.Id);
        PointsPerLevels = pointsPerLevels;
        KeystoneText = keystones;
        Version = version;
    }

    public IReadOnlyDictionary<int, TreeNode> Nodes { get; }
    public int PointsPerLevels { get; }
    public IReadOnlyDictionary<string, string> KeystoneText { get; }

    /// <summary>Hash of the file, so the client mod can tell whether its cached layout is current.</summary>
    public string Version { get; }

    public bool IsLoaded => Nodes.Count > 0;

    public static PassiveTree Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Empty;
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var nodes = root.GetProperty("nodes").EnumerateArray().Select(n => new TreeNode(
            n.GetProperty("id").GetInt32(),
            n.GetProperty("kind").GetString() ?? "small",
            n.GetProperty("name").GetString() ?? "",
            n.GetProperty("x").GetInt32(),
            n.GetProperty("y").GetInt32(),
            n.TryGetProperty("class", out var c) ? c.GetInt32() : 0,
            n.TryGetProperty("stats", out var st) ? st.EnumerateArray().Select(x => new TreeStat(x.GetProperty("stat").GetString() ?? "", x.GetProperty("amount").GetDouble())).ToList() : [],
            n.TryGetProperty("skillMods", out var sm) ? sm.EnumerateArray().Select(x => new SkillMod(
                x.GetProperty("tag").GetString() ?? "", Num(x, "damagePct"), Num(x, "cooldownPct"), Num(x, "costPct"), (int)Num(x, "ranks"))).ToList() : [],
            n.TryGetProperty("keystone", out var k) ? k.GetString() : null,
            n.GetProperty("links").EnumerateArray().Select(l => l.GetInt32()).ToList())).ToList();
        var keystones = root.TryGetProperty("keystones", out var ks)
            ? ks.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
            : new Dictionary<string, string>();
        var tree = new PassiveTree(nodes, root.TryGetProperty("pointsPerLevels", out var p) ? p.GetInt32() : 5, keystones,
            Convert.ToHexString(SHA256.HashData(bytes))[..16]);
        var problems = tree.Validate();
        if (problems.Count > 0) throw new InvalidDataException($"{path}: " + string.Join("; ", problems));
        return tree;
    }

    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>Unique ids, links both ways to real nodes, one start per class, known stats and keystones, all connected.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        foreach (var n in Nodes.Values)
        {
            foreach (var l in n.Links)
                if (!Nodes.TryGetValue(l, out var other)) problems.Add($"node {n.Id} links to missing {l}");
                else if (!other.Links.Contains(n.Id)) problems.Add($"link {n.Id}→{l} is one-way");
            foreach (var s in n.Stats.Where(s => !StatText.ContainsKey(s.Stat))) problems.Add($"node {n.Id}: unknown stat {s.Stat}");
            if (n.Kind == "keystone" && (n.Keystone is null || !KnownKeystones.Contains(n.Keystone))) problems.Add($"node {n.Id}: unknown keystone {n.Keystone}");
        }
        foreach (var cls in Enum.GetValues<CharacterClass>().Where(c => c != CharacterClass.Undefined))
            if (Nodes.Values.Count(n => n.Kind == "start" && n.Class == (int)cls) != 1) problems.Add($"{cls} needs exactly one start node");
        if (Nodes.Count > 0 && Reachable(Nodes.Keys.First(), _ => true).Count != Nodes.Count) problems.Add("the tree is not connected");
        return problems;
    }

    public int PointsFor(int level) => Math.Max(0, level) / PointsPerLevels;

    public int? StartFor(int @class) => Nodes.Values.FirstOrDefault(n => n.Kind == "start" && n.Class == @class)?.Id;

    private HashSet<int> Reachable(int from, Func<int, bool> allowed)
    {
        var seen = new HashSet<int> { from };
        var queue = new Queue<int>([from]);
        while (queue.TryDequeue(out var id))
            foreach (var next in Nodes[id].Links.Where(l => allowed(l) && seen.Add(l)))
                queue.Enqueue(next);
        return seen;
    }

    /// <summary>Why a node can't be taken now, or null if it can.</summary>
    public string? CannotAllocate(IReadOnlySet<int> owned, int @class, int level, int nodeId)
    {
        if (!Nodes.TryGetValue(nodeId, out var node)) return "There is no such passive.";
        if (node.Kind == "start") return "Start nodes are free and can't be taken.";
        if (owned.Contains(nodeId)) return $"You already have {node.Name}.";
        if (owned.Count >= PointsFor(level)) return $"You have no passive points left (one every {PointsPerLevels} levels).";
        var start = StartFor(@class);
        if (!node.Links.Any(l => l == start || owned.Contains(l))) return $"{node.Name} isn't connected to your passives yet.";
        return null;
    }

    /// <summary>Why a node can't be given back, or null if it can: everything else must stay connected to the start.</summary>
    public string? CannotRefund(IReadOnlySet<int> owned, int @class, int nodeId)
    {
        if (!owned.Contains(nodeId)) return "You don't have that passive.";
        if (StartFor(@class) is not { } start) return "Your class has no start in the tree.";
        var rest = owned.Where(id => id != nodeId).ToHashSet();
        var connected = Reachable(start, rest.Contains);
        return rest.All(connected.Contains) ? null : $"Other passives depend on {Nodes[nodeId].Name}; give those back first.";
    }

    /// <summary>
    /// The nodes that count: owned, known and connected to the class start. Anything else (after the tree is re-tuned) is
    /// dropped by the server and its point returned.
    /// </summary>
    public HashSet<int> Connected(IEnumerable<int> owned, int @class)
    {
        var set = owned.Where(Nodes.ContainsKey).ToHashSet();
        if (StartFor(@class) is not { } start) return [];
        var connected = Reachable(start, set.Contains);
        set.IntersectWith(connected);
        return set;
    }

    public TreeBonuses Totals(IEnumerable<int> owned)
    {
        var nodes = owned.Where(Nodes.ContainsKey).Select(id => Nodes[id]).ToList();
        var stats = nodes.SelectMany(n => n.Stats).GroupBy(s => s.Stat).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
        return new TreeBonuses(stats, nodes.SelectMany(n => n.SkillMods).ToList(),
            nodes.Where(n => n.Keystone is not null).Select(n => n.Keystone!).ToHashSet());
    }

    /// <summary>Tooltip lines for a node (the client mod shows them as sent).</summary>
    public IReadOnlyList<string> Describe(TreeNode node)
    {
        var lines = node.Stats.Select(s => string.Format(CultureInfo.InvariantCulture, StatText[s.Stat], s.Amount)).ToList();
        foreach (var m in node.SkillMods)
        {
            var group = m.Tag == "heal" ? "Healing skills" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Tag) + " skills";
            var parts = new List<string>();
            if (m.DamagePct != 0) parts.Add(m.Tag == "heal" ? $"+{m.DamagePct:0.#}% healing" : $"+{m.DamagePct:0.#}% damage");
            if (m.CooldownPct != 0) parts.Add($"{m.CooldownPct:0.#}% shorter cooldown");
            if (m.CostPct != 0) parts.Add($"{m.CostPct:0.#}% lower energy cost");
            if (m.Ranks != 0) parts.Add($"+{m.Ranks} rank{(m.Ranks == 1 ? "" : "s")}");
            lines.Add($"{group}: {string.Join(", ", parts)}");
        }
        if (node.Keystone is { } k && KeystoneText.TryGetValue(k, out var text)) lines.Add(text);
        if (node.Kind == "start") lines.Add("Your class starts here");
        return lines;
    }
}

/// <summary>What a character's passives add up to. Stats are summed per key; keystones and skill mods are listed.</summary>
public sealed record TreeBonuses(IReadOnlyDictionary<string, double> Stats, IReadOnlyList<SkillMod> SkillMods, IReadOnlySet<string> Keystones)
{
    public static readonly TreeBonuses None = new(new Dictionary<string, double>(), [], new HashSet<string>());

    public double this[string stat] => Stats.GetValueOrDefault(stat);

    public bool Has(string keystone) => Keystones.Contains(keystone);

    /// <summary>Combined skill mods for a skill, given the tags it carries (its classes and "heal").</summary>
    public (double DamagePct, double CooldownPct, double CostPct, int Ranks) For(IReadOnlySet<string> tags)
    {
        var mods = SkillMods.Where(m => tags.Contains(m.Tag)).ToList();
        return (mods.Sum(m => m.DamagePct), mods.Sum(m => m.CooldownPct), mods.Sum(m => m.CostPct), mods.Sum(m => m.Ranks));
    }
}
