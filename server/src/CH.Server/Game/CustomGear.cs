using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's own gear with typed-in numbers (server/content/custom-gear.json): rings, necklaces, bracelets, charms,
/// armour, weapons, shields, offhands. Each copies an existing item's look ("look": its model, icon, slot, class rules,
/// weapon animations; rings, necklaces, bracelets and charms have a default) under a new id, and everything else comes
/// from the file: no stat is kept from the look. scripts/install-custom-gear.py writes the same list into the client
/// (CHPrivateMods.gear.txt, resolved the same way), so both sides agree.
/// </summary>
public static class CustomGear
{
    public static readonly IReadOnlyDictionary<string, int> DefaultLooks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["ring"] = 59705, ["necklace"] = 58282, ["bracelet"] = 17802, ["charm"] = 35386,
    };

    public static readonly IReadOnlyDictionary<string, string> Stats = Names("Strength", "Dexterity", "Focus", "Vitality");

    public static readonly IReadOnlyDictionary<string, string> Bonuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Health"] = "HealthBonus", ["Energy"] = "EnergyBonus", ["Attack"] = "AttackBonus", ["Defence"] = "DefenceBonus",
        ["Defense"] = "DefenceBonus", ["Concentration"] = "ConcentrationBonus",
    };

    public static readonly IReadOnlyDictionary<string, string> Resists = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Heat"] = "HeatResist", ["Fire"] = "HeatResist", ["Cold"] = "ColdResist", ["Ice"] = "ColdResist", ["Magic"] = "MagicResist",
        ["Poison"] = "PoisonResist", ["Divine"] = "DivineResist", ["Chaos"] = "ChaosResist", ["Pierce"] = "PiercingResist",
        ["Piercing"] = "PiercingResist", ["Slash"] = "SlashingResist", ["Slashing"] = "SlashingResist", ["Crush"] = "CrushingResist",
        ["Crushing"] = "CrushingResist",
    };

    public static readonly IReadOnlyDictionary<string, string> DamageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Pierce"] = "PiercingDamage", ["Piercing"] = "PiercingDamage", ["Slash"] = "SlashingDamage", ["Slashing"] = "SlashingDamage",
        ["Crush"] = "CrushingDamage", ["Crushing"] = "CrushingDamage", ["Heat"] = "HeatDamage", ["Fire"] = "HeatDamage",
        ["Cold"] = "ColdDamage", ["Ice"] = "ColdDamage", ["Magic"] = "MagicDamage", ["Poison"] = "PoisonDamage",
        ["Divine"] = "DivineDamage", ["Chaos"] = "ChaosDamage", ["True"] = "TrueDamage",
    };

    public static readonly IReadOnlyDictionary<string, int> Avoidances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Physical"] = 0, ["Spell"] = 1, ["Movement"] = 2, ["Wounding"] = 3, ["Weakening"] = 4, ["Mental"] = 5,
    };

    private static Dictionary<string, string> Names(params string[] names) => names.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds the file's gear to <paramref name="items"/>. Unknown names are an error, so a typo shows at start-up.</summary>
    public static void Apply(Dictionary<int, ItemInfo> items, string? path, IReadOnlyDictionary<int, string> abilityNames, IReadOnlyDictionary<int, SkillInfo> skills)
    {
        if (path is null || !File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("gear", out var gear)) return;
        foreach (var e in gear.EnumerateArray())
        {
            var id = e.GetProperty("id").GetInt32();
            var name = e.GetProperty("name").GetString() ?? $"item {id}";
            string Where(string what) => $"custom-gear.json, {name}: {what}";
            var look = LookId(e, items, Where);
            if (!items.TryGetValue(look, out var baseItem)) throw new InvalidDataException(Where($"look {look} isn't an item"));
            if (items.TryGetValue(id, out var taken) && taken.Name != name) throw new InvalidDataException(Where($"id {id} is taken by {taken.Name}"));

            List<(string, int)> Map(string field, IReadOnlyDictionary<string, string> names) => Obj(e, field)
                .Select(p => (names.TryGetValue(p.Name, out var n) ? n : throw new InvalidDataException(Where($"unknown {field} \"{p.Name}\" (use {string.Join(", ", names.Keys)})")), p.Amount)).ToList();
            var damage = Map("damage", DamageTypes);
            var abilities = Obj(e, "abilities").Select(p => (Ability(p.Name, abilityNames) ?? throw new InvalidDataException(Where($"unknown ability \"{p.Name}\""))  , p.Amount)).ToList();
            var avoid = Obj(e, "avoidance").Select(p => (Avoidances.TryGetValue(p.Name, out var a) ? a : throw new InvalidDataException(Where($"unknown avoidance \"{p.Name}\" (use {string.Join(", ", Avoidances.Keys)})")), p.Amount)).ToList();
            var skillBonus = Obj(e, "skills").Select(p => (Skill(p.Name, skills) ?? throw new InvalidDataException(Where($"unknown skill \"{p.Name}\"")), p.Amount)).ToList();
            // skillDamage: flat damage to a skill (Double Shot +50), told apart from ranks by the line it adds to the text.
            var skillDamage = Obj(e, "skillDamage").Select(p => (Skill(p.Name, skills) ?? throw new InvalidDataException(Where($"unknown skill \"{p.Name}\"")), p.Amount, p.Name)).ToList();
            skillBonus.AddRange(skillDamage.Select(d => (d.Item1, d.Amount)));
            var requirements = new List<(string, int)>();
            if (Num(e, "level") is { } level) requirements.Add(("LevelRequirement", level));
            int SkillOf(JsonElement s) => s.ValueKind == JsonValueKind.Number ? s.GetInt32() : Skill(s.GetString() ?? "", skills) ?? throw new InvalidDataException(Where($"unknown skill \"{s}\""));
            var proc = e.TryGetProperty("proc", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;

            items[id] = baseItem with
            {
                Id = id,
                Name = name,
                Description = ExtraText(e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "", skillDamage.Select(x => (x.Name, x.Amount)), Obj(e, "regen")),
                Armour = Num(e, "armour") ?? 0,
                Speed = Num(e, "speed") ?? baseItem.Speed,
                Range = e.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetSingle() : baseItem.Range,
                Damage = damage.Sum(x => x.Item2),
                DamageByType = damage.Select(x => (ItemInfo.DamageTypeIds[x.Item1], x.Item2)).ToList(),
                PhysicalDamage = damage.Where(x => x.Item1 is "PiercingDamage" or "SlashingDamage" or "CrushingDamage").Sum(x => x.Item2),
                Classes = e.TryGetProperty("classes", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
                Bonuses = Map("bonuses", Bonuses).Concat(Map("resists", Resists)).ToList(),
                StatBonuses = Map("stats", Stats),
                Requirements = requirements,
                AbilityBonuses = abilities,
                AvoidanceBonuses = avoid,
                SkillBonuses = skillBonus,
                ProcSkillId = proc is { } pr && pr.TryGetProperty("skill", out var ps) ? SkillOf(ps) : 0,
                ProcSkillLevel = proc is { } pl ? Num(pl, "level") ?? 1 : 0,
                ProcSkillChance = proc is { } pc && pc.TryGetProperty("chance", out var ch) ? ch.GetSingle() / 100f : 0,
                EquipSkillId = e.TryGetProperty("equipSkill", out var es) ? SkillOf(es) : 0,
                StarLevel = Num(e, "stars") ?? -1,
                Tradeable = !e.TryGetProperty("tradeable", out var t) || t.ValueKind != JsonValueKind.False,
                ImportantItem = false,
            };
        }
    }

    /// <summary>
    /// The description with the lines the server reads stats from (scripts/install-custom-gear.py writes the same):
    /// "Increases the damage of your Double Shot skill by 50." for skillDamage, "Restores 25 health and 25 energy per
    /// tick." for regen.
    /// </summary>
    public static string ExtraText(string description, IEnumerable<(string Skill, int Amount)> skillDamage, IEnumerable<(string Name, int Amount)> regen)
    {
        var lines = new List<string>();
        if (description.Length > 0) lines.Add(description);
        lines.AddRange(skillDamage.Select(s => $"Increases the damage of your {s.Skill} skill by {s.Amount}."));
        var r = regen.ToDictionary(x => x.Name.ToLowerInvariant(), x => x.Amount);
        var (h, en) = (r.GetValueOrDefault("health"), r.GetValueOrDefault("energy"));
        if (h > 0 && en > 0) lines.Add($"Restores {h} health and {en} energy per tick.");
        else if (h > 0) lines.Add($"Restores {h} health per tick.");
        else if (en > 0) lines.Add($"Restores {en} energy per tick.");
        return string.Join(" ", lines);
    }

    private static int LookId(JsonElement e, Dictionary<int, ItemInfo> items, Func<string, string> where)
    {
        if (e.TryGetProperty("look", out var look))
        {
            if (look.ValueKind == JsonValueKind.Number) return look.GetInt32();
            var name = look.GetString() ?? "";
            return items.Values.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(i => (int?)i.Id).Min()
                ?? throw new InvalidDataException(where($"no item called \"{name}\" to copy the look of"));
        }
        var type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        return DefaultLooks.TryGetValue(type, out var id) ? id
            : throw new InvalidDataException(where("needs a \"look\" (an item id or name), or \"type\": ring, necklace, bracelet or charm"));
    }

    private static int? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static IEnumerable<(string Name, int Amount)> Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object
            ? o.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number).Select(p => (p.Name, p.Value.GetInt32())).ToList()
            : [];

    /// <summary>An ability by id or name ("Treasure Hunter").</summary>
    public static int? Ability(string name, IReadOnlyDictionary<int, string> abilityNames) =>
        int.TryParse(name, out var id) ? id : abilityNames.Where(a => a.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(a => (int?)a.Key).FirstOrDefault();

    /// <summary>A skill by id or name: a player skill first (the lowest id), as scripts/install-custom-gear.py picks.</summary>
    public static int? Skill(string name, IReadOnlyDictionary<int, SkillInfo> skills) =>
        int.TryParse(name, out var id) ? id
            : skills.Values.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.AbilityId < 0).ThenBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefault();
}
