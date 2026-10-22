using System.Text.Json;
using System.Text.RegularExpressions;

namespace CH.Server.Game;

/// <summary>
/// The owner's own monsters and quests (server/content/custom-monsters.json, custom-quests.json): copies of existing
/// ones under new ids with new names and numbers. The client mod adds the same copies to the game's own lists
/// (CHPrivateMods.extras.txt, written by scripts/install-custom-gear.py), so the game can draw and describe them.
/// Gilthral the Gladiator (a level 210 Killain) and Segorax's "Spar with Gilthral" daily are the first.
/// </summary>
public static class CustomContent
{
    public sealed record Monster(int Id, int Base, string Name, int Level, int Health, int? Attack, int? Defence, double DamageScale)
    {
        /// <summary>Its own resists by damage type and skill evasion by avoidance type (null: the base's).</summary>
        public IReadOnlyDictionary<int, int>? Resists { get; init; }
        public IReadOnlyDictionary<int, int>? Evade { get; init; }

        /// <summary>
        /// Its own kill XP and gold (null: the base's). Flat XP goes to everyone sharing the kill at any level (Denya:
        /// 200,000); boosts like elixirs still apply.
        /// </summary>
        public int? Xp { get; init; }
        public int? Gold { get; init; }

        /// <summary>
        /// Its own auto-attack: "hitDamage" (the most a swing deals before the player's resist; each swing rolls half to
        /// all of it) of "hitDamageType" (Crush, Slash, Poison…, True ignores resists; left out: the base's first type, else Crush). Null: the
        /// base's damage times <see cref="DamageScale"/>.
        /// </summary>
        public int? HitDamage { get; init; }
        public int? HitDamageType { get; init; }

        /// <summary>The swing's damage parts: its own hit, or the base's times the damage scale.</summary>
        public List<(int Type, int Amount)> Damage(IReadOnlyList<(int Type, int Amount)> baseDamage) =>
            HitDamage is { } hit
                ? [(HitDamageType ?? (baseDamage.Count > 0 ? baseDamage[0].Type : 2), Math.Max(0, hit))]
                : baseDamage.Select(d => (d.Type, (int)(d.Amount * DamageScale))).ToList();

        /// <summary>Model size times the base's (Denya: 3, three times the Priestess of the Morrigan).</summary>
        public float Scale { get; init; } = 1;

        /// <summary>Whether the file set level / health (overrides leave the game's when it didn't).</summary>
        public bool LevelSet { get; init; } = true;
        public bool HealthSet { get; init; } = true;

        /// <summary>Its "abilities" as written, read by <see cref="BossAbility.Parse"/>.</summary>
        public JsonElement? AbilitiesJson { get; init; }

        /// <summary>The skills and effects its abilities show under (the client mod copies them): (new id, look id, name).</summary>
        public IReadOnlyList<(int Id, int Look, string Name, bool Effect)> Shown { get; init; } = [];
    }

    private static readonly Dictionary<string, int> DamageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pierce"] = 0, ["slash"] = 1, ["crush"] = 2, ["heat"] = 3, ["fire"] = 3, ["cold"] = 4, ["ice"] = 4, ["magic"] = 5,
        ["poison"] = 6, ["divine"] = 7, ["chaos"] = 8,
    };

    private static readonly Dictionary<string, int> AvoidanceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["physical"] = 0, ["spell"] = 1, ["movement"] = 2, ["wounding"] = 3, ["weakening"] = 4, ["mental"] = 5,
    };

    /// <summary>{"all": 400, "Poison": 300} → every type at 400 but poison; null when the monster doesn't set it.</summary>
    private static Dictionary<int, int>? ByType(JsonElement e, string field, IReadOnlyDictionary<string, int> names, IEnumerable<int> all)
    {
        if (!e.TryGetProperty(field, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<int, int>();
        if (o.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.Number) foreach (var t in all) d[t] = a.GetInt32();
        foreach (var p in o.EnumerateObject())
            if (p.Name != "all" && names.TryGetValue(p.Name, out var t) && p.Value.ValueKind == JsonValueKind.Number) d[t] = p.Value.GetInt32();
        return d;
    }

    public sealed record Quest(int Id, int Base, IReadOnlyList<(string From, string To)> Replace, string? Conversation);

    private static JsonDocument? Read(string? path) =>
        path is null || !File.Exists(path) ? null
            : JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    /// <summary>"hitDamageType": a damage type's name, or "True" (9, no resist).</summary>
    private static int? HitType(JsonElement e)
    {
        if (!e.TryGetProperty("hitDamageType", out var t) || t.ValueKind != JsonValueKind.String) return null;
        var name = t.GetString() ?? "";
        if (DamageTypes.TryGetValue(name, out var type)) return type;
        return string.Equals(name, "true", StringComparison.OrdinalIgnoreCase) ? 9 : null;
    }

    public static IReadOnlyList<Monster> LoadMonsters(string? path)
    {
        using var doc = Read(path);
        if (doc is null || !doc.RootElement.TryGetProperty("monsters", out var list)) return [];
        int? Opt(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return list.EnumerateArray().Select(e => new Monster(e.GetProperty("id").GetInt32(), e.TryGetProperty("base", out var bs) && bs.ValueKind == JsonValueKind.Number ? bs.GetInt32() : e.GetProperty("id").GetInt32(),
            e.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "", Opt(e, "level") ?? 1, Opt(e, "health") ?? 1, Opt(e, "attack"), Opt(e, "defence"),
            e.TryGetProperty("damageScale", out var ds) && ds.ValueKind == JsonValueKind.Number ? ds.GetDouble() : 1)
        {
            Resists = ByType(e, "resists", DamageTypes, [0, 1, 2, 3, 4, 5, 6, 8]), // "all" leaves divine as the base has it
            Evade = ByType(e, "evasion", AvoidanceTypes, [0, 1, 2, 3, 4, 5]),
            LevelSet = Opt(e, "level") is not null,
            HealthSet = Opt(e, "health") is not null,
            HitDamage = Opt(e, "hitDamage"),
            HitDamageType = HitType(e),
            Xp = Opt(e, "xp"),
            Gold = Opt(e, "gold"),
            Scale = e.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetSingle() : 1,
            AbilitiesJson = e.TryGetProperty("abilities", out _) ? e.Clone() : null,
            Shown = e.TryGetProperty("abilities", out var ab) && ab.ValueKind == JsonValueKind.Array
                ? ab.EnumerateArray().SelectMany(x => new[]
                    {
                        (Id: Opt(x, "skillId") ?? 0, Look: Opt(x, "look") ?? 0, Name: x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", Effect: false),
                        (Id: Opt(x, "effectId") ?? 0, Look: Opt(x, "look") ?? 0, Name: x.TryGetProperty("name", out var n2) ? n2.GetString() ?? "" : "", Effect: true),
                    }).Where(x => x.Id > 0 && x.Look > 0).ToList()
                : [],
        }).ToList();
    }

    public static IReadOnlyList<Quest> LoadQuests(string? path)
    {
        using var doc = Read(path);
        if (doc is null || !doc.RootElement.TryGetProperty("quests", out var list)) return [];
        return list.EnumerateArray().Select(e => new Quest(e.GetProperty("id").GetInt32(), e.GetProperty("base").GetInt32(),
            e.TryGetProperty("replace", out var r) && r.ValueKind == JsonValueKind.Object
                ? r.EnumerateObject().Select(p => (p.Name, p.Value.GetString() ?? "")).ToList() : [],
            e.TryGetProperty("conversation", out var c) ? c.GetString() : null)).ToList();
    }

    /// <summary>The monsters as copies of their base templates (look, size, attack speed) under the new id and name.</summary>
    public static void ApplyMonsters(Dictionary<int, MobInfo> mobs, IEnumerable<Monster> monsters)
    {
        foreach (var m in monsters)
            if (mobs.TryGetValue(m.Base, out var b)) mobs[m.Id] = b with { Id = m.Id, Name = m.Name };
    }

    /// <summary>
    /// A monster's combat numbers: its own level and health; everything else as its base (owner's rule, 2026-10-20:
    /// Gilthral has Killain's resists, evasion, attack, defence and damage), unless the file sets attack, defence or a
    /// damageScale.
    /// </summary>
    public static MobCombat? Combat(Monster m, MobCombat? b)
    {
        // A base without official numbers (Brown Rabbit): the owner's numbers still count; attack and damage left at 0 / none
        // fall back to the level formulas when it fights.
        b ??= new MobCombat(m.Level, m.Health, 0, 0, []);
        var resists = b.Resists.ToDictionary(r => r.Key, r => r.Value);
        foreach (var (type, flat) in m.Resists ?? new Dictionary<int, int>())
            resists[type] = (flat, resists.TryGetValue(type, out var had) && had.Share > 0 ? had.Share : 1.0);
        var evade = b.Evade.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (type, value) in m.Evade ?? new Dictionary<int, int>()) evade[type] = value;
        return new MobCombat(m.Level, m.Health, m.Attack ?? b.Attack, m.Defence ?? b.Defence, m.Damage(b.Damage))
        {
            Xp = m.Xp ?? b.Xp, Gold = m.Gold is { } g ? (g, g) : b.Gold, Resists = resists, Evade = evade,
        };
    }

    /// <summary>Text with the quest's replacements and its base id swapped for its own.</summary>
    public static string Swap(string text, Quest q)
    {
        foreach (var (from, to) in q.Replace) text = text.Replace(from, to);
        return Regex.Replace(text, $@"\b{q.Base}\b", q.Id.ToString());
    }

    /// <summary>The quests as copies of their base quests, with the replacements in names and stage targets.</summary>
    public static Dictionary<int, QuestInfo> ApplyQuests(IReadOnlyDictionary<int, QuestInfo> quests, IEnumerable<Quest> custom)
    {
        var all = quests.ToDictionary(k => k.Key, k => k.Value);
        foreach (var q in custom)
        {
            if (!all.TryGetValue(q.Base, out var b)) continue;
            int Id(int v) => int.TryParse(Swap(v.ToString(), q), out var n) ? n : v;
            all[q.Id] = b with
            {
                Id = q.Id,
                Name = Swap(b.Name, q),
                Stages = b.Stages.Select(st => st with { Details = string.Join(";", st.Details.Split(';').Select(part =>
                        string.Join(",", part.Split(',').Select((x, i) => i == 0 && int.TryParse(x, out var n) ? Id(n).ToString() : x)))) }
                    with { Sources = st.Sources.Select(Id).ToList() }).ToList(),
            };
        }
        return all;
    }
}
