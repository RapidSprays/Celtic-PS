using System.Text.Json;

namespace CH.Server.Game;

/// <summary>Equipment slots (client Balance.Model.EquipSlot). Values are the indices of the 25 equipment entries.</summary>
public enum EquipSlot
{
    None = -2,
    Unequipable = -1,
    Weapon = 0,
    Head = 1,
    Chest = 2,
    Leg = 3,
    Feet = 4,
    Offhand = 5,
    Hands = 6,
    Misc = 7,
    RingR1 = 8,
    RingR2 = 9,
    RingL1 = 10,
    RingL2 = 11,
    BangleR = 12,
    BangleL = 13,
    Neck = 14,
    FashHead = 15,
    FashTorso = 16,
    FashLegs = 17,
    FashFeet = 18,
    FashHands = 19,
    Companion = 20,
    Saddle = 21,
    Mount = 22,
    FashWeapon = 23,
    FashOffhand = 24,
}

/// <summary>Client Balance.Model.ClassType.</summary>
public enum CharacterClass
{
    Undefined = 0,
    Warrior = 1,
    Druid = 2,
    Mage = 3,
    Ranger = 4,
    Rogue = 5,
}

public sealed record ItemInfo(
    int Id,
    string Name,
    EquipSlot Slot,
    string Subtype,
    int Armour,
    int Speed,
    int Weight,
    float Range,
    int Damage,
    IReadOnlyList<string> Classes,
    bool Usable,
    bool Stackable,
    string SkillLink);

public sealed record SkillLevelInfo(float InitialDamage, float BaseDamage, float CastingTime, float RechargeTime, int EnergyCost);

public sealed record SkillInfo(int Id, string Name, int AbilityId, float CastRange, bool IncludesWeaponAttack, IReadOnlyList<SkillLevelInfo> Levels);

public sealed record MobInfo(int Id, int TemplateId, string Name, string Race, int Type, float Radius, float AttackRange, int AttackSpeed, int Power);

public sealed record ZoneInfo(int Id, string Name, string Scene, (float X, float Z) Min, (float X, float Z) Max, IReadOnlyList<(int Id, float X, float Y, float Z)> PlayerSpawns);

/// <summary>
/// The parts of the client's own balance data the server needs (item, skill, mob and zone templates). Read at start-up
/// from the local extraction (scripts/extract-balance.py); never part of the repo. <see cref="Empty"/> lets the server
/// and tests run without it.
/// </summary>
public sealed class Balance
{
    public static readonly Balance Empty = new(new(), new(), new(), new(), new(), 0);

    private Balance(Dictionary<int, ItemInfo> items, Dictionary<int, SkillInfo> skills, Dictionary<int, MobInfo> mobs,
        Dictionary<int, ZoneInfo> zones, Dictionary<int, int[]> mobSets, int version)
    {
        Items = items;
        Skills = skills;
        Mobs = mobs;
        Zones = zones;
        MobSets = mobSets;
        Version = version;
    }

    public IReadOnlyDictionary<int, ItemInfo> Items { get; }
    public IReadOnlyDictionary<int, SkillInfo> Skills { get; }
    public IReadOnlyDictionary<int, MobInfo> Mobs { get; }
    public IReadOnlyDictionary<int, ZoneInfo> Zones { get; }
    public IReadOnlyDictionary<int, int[]> MobSets { get; }
    public int Version { get; }
    public bool IsLoaded => Items.Count > 0;

    public static Balance Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Empty;
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 256 });
        var root = doc.RootElement;

        var items = new Dictionary<int, ItemInfo>();
        foreach (var (_, e) in Entries(root, "ItemTemplates"))
        {
            var id = e.GetProperty("ID").GetInt32();
            items[id] = new ItemInfo(
                id,
                Str(e, "Name"),
                Enum.TryParse<EquipSlot>(Str(e, "EquipSlot"), out var slot) ? slot : EquipSlot.Unequipable,
                Str(e, "Subtype"),
                Int(e, "Armour"), Int(e, "Speed"), Int(e, "Weight"), Float(e, "Range"),
                e.TryGetProperty("DamageTypes", out var dmg) && dmg.ValueKind == JsonValueKind.Array ? dmg.EnumerateArray().Sum(d => Int(d, "Amount")) : 0,
                e.TryGetProperty("ClassTypes", out var cls) && cls.ValueKind == JsonValueKind.Array ? cls.EnumerateArray().Select(c => c.GetString() ?? "").ToList() : [],
                Int(e, "Usable") == 1, Int(e, "Stackable") == 1, Str(e, "SkillLinkID"));
        }

        var skills = new Dictionary<int, SkillInfo>();
        foreach (var (key, e) in Entries(root, "SkillTemplates"))
        {
            var id = e.TryGetProperty("SkillId", out var sid) && sid.ValueKind == JsonValueKind.Number ? sid.GetInt32() : int.Parse(key);
            var levels = e.TryGetProperty("Levels", out var lv) && lv.ValueKind == JsonValueKind.Array
                ? lv.EnumerateArray().Select(l => new SkillLevelInfo(Float(l, "InitialDamage"), Float(l, "BaseDamage"), Float(l, "CastingTime"), Float(l, "RechargeTime"), Int(l, "EnergyCost"))).ToList()
                : [];
            skills[id] = new SkillInfo(id, Str(e, "Name"), Int(e, "AbilityId"), Float(e, "CastRange"), Bool(e, "IncludesWeaponAttack"), levels);
        }

        var mobs = new Dictionary<int, MobInfo>();
        foreach (var (_, e) in Entries(root, "MobTemplates"))
        {
            var id = Int(e, "Id");
            mobs[id] = new MobInfo(id, Int(e, "TemplateId"), Str(e, "Name"), Str(e, "Race"), Int(e, "Type"), Float(e, "Radius"),
                Float(e, "AttackRange"), Int(e, "AttackSpeed"), Int(e, "Power"));
        }

        var zones = new Dictionary<int, ZoneInfo>();
        foreach (var (key, e) in Entries(root, "Zones"))
        {
            var spawns = e.TryGetProperty("PlayerSpawnPoints", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.EnumerateArray().Select(p => { var pos = Vec(p, "Position"); return (Int(p, "Id"), pos[0], pos[1], pos[2]); }).ToList()
                : [];
            var min = Vec(e, "MinPos");
            var max = Vec(e, "MaxPos");
            zones[int.Parse(key)] = new ZoneInfo(int.Parse(key), Str(e, "ScreenName"), Str(e, "SceneFilename"),
                (min.ElementAtOrDefault(0), min.ElementAtOrDefault(min.Length > 2 ? 2 : 1)),
                (max.ElementAtOrDefault(0), max.ElementAtOrDefault(max.Length > 2 ? 2 : 1)), spawns);
        }

        var mobSets = new Dictionary<int, int[]>();
        foreach (var (key, e) in Entries(root, "MobSets"))
            if (e.ValueKind == JsonValueKind.Array) mobSets[int.Parse(key)] = e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToArray();

        var version = root.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new Balance(items, skills, mobs, zones, mobSets, version);
    }

    /// <summary>Skill taught by a "Learn…" skill book, matched on the skill's name without spaces.</summary>
    public SkillInfo? SkillTaughtBy(ItemInfo book)
    {
        if (!book.SkillLink.StartsWith("Learn", StringComparison.Ordinal)) return null;
        var wanted = book.SkillLink["Learn".Length..];
        return Skills.Values.FirstOrDefault(s => s.Name.Replace(" ", "").Replace("'", "").Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<(string Key, JsonElement Value)> Entries(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in section.EnumerateObject())
            if (p.Name != "$id" && int.TryParse(p.Name, out _)) yield return (p.Name, p.Value);
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

    private static float Float(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetDouble() != 0));

    private static float[] Vec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray() : [];
}
