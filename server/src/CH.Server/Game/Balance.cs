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
    string SkillLink,
    int SkillLinkId = -1,
    int SkillLinkLevel = 0,
    string Description = "")
{
    /// <summary>BonusTypes: resists and flat Health/Energy/Attack/Defence/Concentration bonuses (names as in the client data).</summary>
    public IReadOnlyList<(string Type, int Amount)> Bonuses { get; init; } = [];

    /// <summary>RequirementTypes: LevelRequirement, Strength/Dexterity/Focus/VitalityRequirement, GenderRequirement.</summary>
    public IReadOnlyList<(string Type, int Amount)> Requirements { get; init; } = [];

    /// <summary>PrimaryStatBonuses: Strength, Dexterity, Focus, Vitality.</summary>
    public IReadOnlyList<(string Type, int Amount)> StatBonuses { get; init; } = [];

    public IReadOnlyList<(int Id, int Amount)> AbilityBonuses { get; init; } = [];
    public IReadOnlyList<(int Id, int Amount)> AvoidanceBonuses { get; init; } = [];
    public IReadOnlyList<(int Id, int Amount)> SkillBonuses { get; init; } = [];

    /// <summary>Equipment slots this item blocks while worn (two-handers block the offhand, 5).</summary>
    public IReadOnlyList<int> BlockedSlots { get; init; } = [];

    public int ProcSkillId { get; init; }
    public int ProcSkillLevel { get; init; }
    public float ProcSkillChance { get; init; }
    public int EquipSkillId { get; init; }
    public int StarLevel { get; init; }

    /// <summary>Tradeable (1) in the client data; untradeable items are quest, bound or premium items.</summary>
    public bool Tradeable { get; init; }

    /// <summary>ImportantItem: quest and key items the client asks about before selling.</summary>
    public bool ImportantItem { get; init; }

    /// <summary>MaxCharges: uses before the item is gone (1 = single use, −1 = never runs out).</summary>
    public int MaxCharges { get; init; } = 1;
    public int ModelId { get; init; }

    public int Requirement(string type) => Requirements.Where(r => r.Type == type).Select(r => r.Amount).DefaultIfEmpty(0).Max();
    public int LevelRequirement => Requirement("LevelRequirement");
    public int StatBonus(string type) => StatBonuses.Where(b => b.Type == type).Sum(b => b.Amount);
    public int Bonus(string type) => Bonuses.Where(b => b.Type == type).Sum(b => b.Amount);

    /// <summary>Test and placeholder templates ("TEST …", "… DNU", "NOT USED") never appear as rewards.</summary>
    public bool IsPlaceholder => Name.Contains("TEST", StringComparison.Ordinal) || Name.Contains("DNU", StringComparison.Ordinal)
        || Name.Contains("NOT USED", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(" OLD", StringComparison.Ordinal) || Name == "INVALID ITEM";
}

public sealed record SkillLevelInfo(float InitialDamage, float BaseDamage, float CastingTime, float RechargeTime, int EnergyCost, int TemplateLevel = 0, int MinLevel = 0);

/// <summary>A skill template. Heals have negative damage values. <see cref="CastTarget"/> is Self, Enemy, Friendly, Group or Fish.</summary>
public sealed record SkillInfo(int Id, string Name, int AbilityId, float CastRange, bool IncludesWeaponAttack, string CastTarget, int StatusEffectId, IReadOnlyList<SkillLevelInfo> Levels)
{
    public SkillLevelInfo Level(int level) => Levels.Count == 0 ? new SkillLevelInfo(0, 0, 0, 5, 0) : Levels[Math.Clamp(level, 0, Levels.Count - 1)];

    /// <summary>Highest rank a character of this level may train (CSkillTemplateManager.GetMaxLevel).</summary>
    public int MaxRank(int playerLevel) => Levels.Where(l => l.MinLevel <= playerLevel).Select(l => l.TemplateLevel).DefaultIfEmpty(0).Max();
}

public sealed record ConversationInfo(int Id, string Filename);

public sealed record StatusEffectLevel(float Amount, float BaseAmount, float Duration)
{
    /// <summary>
    /// The client's own formula for an effect's strength (StatusEffectLevel.GetModifiedAmount), so tooltips match what
    /// the server applies.
    /// </summary>
    public int ModifiedAmount(float abilityLevel, float statModifier = 0) =>
        (int)Math.Ceiling(BaseAmount + Amount * (1.0 + Math.Sqrt(abilityLevel / 100f) + Math.Sqrt(statModifier)));
}

/// <summary>A status effect template. <see cref="Type"/> is the client's EffectType enum value.</summary>
public sealed record StatusEffectInfo(int Id, string Name, int Type, IReadOnlyList<StatusEffectLevel> Levels)
{
    public StatusEffectLevel? Level(int index) => Levels.Count == 0 ? null : Levels[Math.Clamp(index, 0, Levels.Count - 1)];
}

public sealed record MobInfo(int Id, int TemplateId, string Name, string Race, int Type, float Radius, float AttackRange, int AttackSpeed, int Power);

public sealed record ZoneInfo(int Id, string Name, string Scene, (float X, float Z) Min, (float X, float Z) Max, IReadOnlyList<SpawnPoint> PlayerSpawns)
{
    /// <summary>"L" = live zone; "T" = test/dev; a zone id = shown under that zone's map (client MapScreen).</summary>
    public string Accessibility { get; init; } = "";
}

/// <summary>A leystone / player spawn point (Zones[n].PlayerSpawnPoints). Its id is the fast-travel id.</summary>
public sealed record SpawnPoint(int Id, string Name, float X, float Y, float Z, bool FreeTravel);

/// <summary>
/// The parts of the client's own balance data the server needs (item, skill, mob and zone templates). Read at start-up
/// from the local extraction (scripts/extract-balance.py); never part of the repo. <see cref="Empty"/> lets the server
/// and tests run without it.
/// </summary>
public sealed class Balance
{
    public static readonly Balance Empty = new(new(), new(), new(), new(), new(), 0);

    private Balance(Dictionary<int, ItemInfo> items, Dictionary<int, SkillInfo> skills, Dictionary<int, MobInfo> mobs,
        Dictionary<int, ZoneInfo> zones, Dictionary<int, int[]> mobSets, int version,
        List<ConversationInfo>? conversations = null, Dictionary<int, List<int>>? spawnIconIds = null,
        Dictionary<int, StatusEffectInfo>? statusEffects = null)
    {
        StatusEffects = statusEffects ?? new();
        Conversations = conversations ?? [];
        SpawnIconIdsByMob = spawnIconIds ?? new();
        Items = items;
        Skills = skills;
        Mobs = mobs;
        Zones = zones;
        MobSets = mobSets;
        Version = version;
    }

    public IReadOnlyDictionary<int, ItemInfo> Items { get; }
    public IReadOnlyDictionary<int, SkillInfo> Skills { get; }

    /// <summary>Cooking recipes (CraftingTemplates): recipe id → name.</summary>
    public IReadOnlyDictionary<int, string> Recipes { get; private init; } = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, MobInfo> Mobs { get; }
    public IReadOnlyDictionary<int, ZoneInfo> Zones { get; }
    public IReadOnlyDictionary<int, int[]> MobSets { get; }
    public int Version { get; }
    public IReadOnlyList<ConversationInfo> Conversations { get; }
    public IReadOnlyDictionary<int, StatusEffectInfo> StatusEffects { get; }

    /// <summary>Official spawn-point ids that carry quest/shop icons for a mob template (SpawnIconInfoCollections).</summary>
    public IReadOnlyDictionary<int, List<int>> SpawnIconIdsByMob { get; }
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
                Int(e, "Usable") == 1, Int(e, "Stackable") == 1, Str(e, "SkillLinkID"), SkillLinkId(e), Int(e, "SkillLinkLevel"), Str(e, "Description"))
            {
                Bonuses = Pairs(e, "BonusTypes", "BonusType"),
                Requirements = Pairs(e, "RequirementTypes", "RequirementType"),
                StatBonuses = Pairs(e, "PrimaryStatBonuses", "PrimaryStatType"),
                AbilityBonuses = IdPairs(e, "AbilityBonuses", "AbilityType"),
                AvoidanceBonuses = IdPairs(e, "AvoidanceBonuses", "AbilityType"),
                SkillBonuses = IdPairs(e, "SkillBonuses", "SkillType"),
                BlockedSlots = e.TryGetProperty("BlockedSlots", out var blocked) && blocked.ValueKind == JsonValueKind.Array
                    ? blocked.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : [],
                ProcSkillId = Int(e, "ProcSkillID"),
                ProcSkillLevel = Int(e, "ProcSkillLevel"),
                ProcSkillChance = Float(e, "ProcSkillChance"),
                EquipSkillId = Int(e, "EquipSkillID"),
                StarLevel = Int(e, "StarLevel"),
                Tradeable = Int(e, "Tradeable") == 1,
                ImportantItem = Int(e, "ImportantItem") == 1,
                MaxCharges = Int(e, "MaxCharges"),
                ModelId = Int(e, "ModelID"),
            };
        }

        var skills = new Dictionary<int, SkillInfo>();
        foreach (var (key, e) in Entries(root, "SkillTemplates"))
        {
            var id = e.TryGetProperty("SkillId", out var sid) && sid.ValueKind == JsonValueKind.Number ? sid.GetInt32() : int.Parse(key);
            var levels = e.TryGetProperty("Levels", out var lv) && lv.ValueKind == JsonValueKind.Array
                ? lv.EnumerateArray().Select(l => new SkillLevelInfo(Float(l, "InitialDamage"), Float(l, "BaseDamage"), Float(l, "CastingTime"), Float(l, "RechargeTime"), Int(l, "EnergyCost"), Int(l, "TemplateLevel"), Int(l, "MinLevel"))).ToList()
                : [];
            skills[id] = new SkillInfo(id, Str(e, "Name"), Int(e, "AbilityId"), Float(e, "CastRange"), Bool(e, "IncludesWeaponAttack"), Str(e, "CastTarget"), Int(e, "StatusEffectId"), levels);
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
                ? ps.EnumerateArray().Select(p =>
                {
                    var pos = Vec(p, "Position");
                    var name = Str(p, "Description").Replace("\\n", " ").Replace("\n", " ").Replace("(Free Travel)", "").Trim();
                    return new SpawnPoint(Int(p, "Id"), name, pos[0], pos[1], pos[2], Int(p, "FreeTravel") != 0);
                }).ToList()
                : [];
            var min = Vec(e, "MinPos");
            var max = Vec(e, "MaxPos");
            zones[int.Parse(key)] = new ZoneInfo(int.Parse(key), Str(e, "ScreenName"), Str(e, "SceneFilename"),
                (min.ElementAtOrDefault(0), min.ElementAtOrDefault(min.Length > 2 ? 2 : 1)),
                (max.ElementAtOrDefault(0), max.ElementAtOrDefault(max.Length > 2 ? 2 : 1)), spawns) { Accessibility = Str(e, "Accessibility") };
        }

        var mobSets = new Dictionary<int, int[]>();
        foreach (var (key, e) in Entries(root, "MobSets"))
            if (e.ValueKind == JsonValueKind.Array) mobSets[int.Parse(key)] = e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToArray();

        var conversations = new List<ConversationInfo>();
        if (root.TryGetProperty("ConversationLookupsTemplates", out var convs) && convs.ValueKind == JsonValueKind.Array)
            foreach (var c in convs.EnumerateArray())
                conversations.Add(new ConversationInfo(Int(c, "ConversationId"), Str(c, "Filename")));

        var spawnIcons = new Dictionary<int, List<int>>();
        foreach (var (key, e) in Entries(root, "SpawnIconInfoCollections"))
        {
            if (!e.TryGetProperty("IconsInfoPerMob", out var perMob) || perMob.ValueKind != JsonValueKind.Object) continue;
            foreach (var mob in perMob.EnumerateObject())
                if (int.TryParse(mob.Name, out var mobId))
                    (spawnIcons.TryGetValue(mobId, out var list) ? list : spawnIcons[mobId] = []).Add(int.Parse(key));
        }

        var statusEffects = new Dictionary<int, StatusEffectInfo>();
        foreach (var (_, e) in Entries(root, "StatusEffects"))
        {
            var id = Int(e, "Id");
            var levels = e.TryGetProperty("Levels", out var lv) && lv.ValueKind == JsonValueKind.Array
                ? lv.EnumerateArray().Select(l => new StatusEffectLevel(Float(l, "Amount"), Float(l, "BaseAmount"), Float(l, "Duration"))).ToList()
                : [];
            statusEffects[id] = new StatusEffectInfo(id, Str(e, "Name"), Int(e, "EffectType"), levels);
        }

        var recipes = new Dictionary<int, string>();
        foreach (var (key, e) in Entries(root, "CraftingTemplates")) recipes[int.Parse(key)] = Str(e, "RecipeName");

        var version = root.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new Balance(items, skills, mobs, zones, mobSets, version, conversations, spawnIcons, statusEffects) { Recipes = recipes };
    }

    /// <summary>Whether an item is an openable container (see <see cref="ChestCatalog"/>).</summary>
    public bool Chests(int itemId) => Items.TryGetValue(itemId, out var i) && i.Usable && ChestCatalog.ContainerLinks.Contains(i.SkillLinkId);

    /// <summary>
    /// What a "Learn …" book teaches: its link is either the name ("LearnPummel") or a skill id whose template is
    /// called "Learn Natures Breath". Returns the name after "Learn".
    /// </summary>
    private string? LearnTarget(ItemInfo book) =>
        book.SkillLink.StartsWith("Learn", StringComparison.Ordinal) && !book.SkillLink.StartsWith("Learn ", StringComparison.Ordinal) ? book.SkillLink["Learn".Length..]
        : Skills.TryGetValue(book.SkillLinkId, out var link) && link.Name.StartsWith("Learn ", StringComparison.OrdinalIgnoreCase) ? link.Name["Learn ".Length..]
        : null;

    private static string NameKey(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Skill taught by a skill book (training manual / spell tome), matched on the skill's name.</summary>
    public SkillInfo? SkillTaughtBy(ItemInfo book)
    {
        if (LearnTarget(book) is not { } wanted) return null;
        var key = NameKey(wanted);
        return Skills.Values.FirstOrDefault(s => !s.Name.StartsWith("Learn", StringComparison.OrdinalIgnoreCase) && NameKey(s.Name) == key);
    }

    /// <summary>Cooking recipe taught by a recipe book ("'Broth' Recipe Book" → recipe "Broth").</summary>
    public int? RecipeTaughtBy(ItemInfo book)
    {
        if (LearnTarget(book) is not { } wanted) return null;
        var key = NameKey(wanted);
        foreach (var (id, name) in Recipes)
            if (NameKey(name) == key) return id;
        return null;
    }

    /// <summary>
    /// Best conversation for an NPC, matched on name words (e.g. "Private Duncan" → cv_private_duncan). The server-side
    /// mob → conversation link isn't shipped, so this is a heuristic; GM commands can override it.
    /// </summary>
    public ConversationInfo? ConversationFor(MobInfo mob)
    {
        static string[] Words(string s) => s.ToLowerInvariant().Split([' ', '_', '-', '\'', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("the" or "of" or "cv")).ToArray();
        var name = mob.Name.Contains(']') ? mob.Name[(mob.Name.IndexOf(']') + 1)..] : mob.Name;
        var nameWords = Words(name);
        if (nameWords.Length == 0) return null;
        return Conversations
            .Select(c => (Conv: c, Words: Words(c.Filename)))
            .Where(c => c.Words.Contains(nameWords[0]))
            .OrderByDescending(c => c.Words.Count(nameWords.Contains))
            .ThenBy(c => c.Words.Length)
            .Select(c => c.Conv)
            .FirstOrDefault();
    }

    private static IEnumerable<(string Key, JsonElement Value)> Entries(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in section.EnumerateObject())
            if (p.Name != "$id" && int.TryParse(p.Name, out _)) yield return (p.Name, p.Value);
    }

    /// <summary>
    /// ItemTemplate.SkillLinkID is the client's SkillType enum: a name when the enum has one, otherwise the number. Only
    /// Names are resolved through the generated <see cref="SkillTypes"/> table (e.g. InstantHeal = 6, MagicBox = 208).
    /// </summary>
    private static int SkillLinkId(JsonElement e)
    {
        if (!e.TryGetProperty("SkillLinkID", out var v)) return -1;
        if (v.ValueKind == JsonValueKind.Number) return (int)v.GetDouble();
        return v.GetString() is { } name && SkillTypes.ByName.TryGetValue(name, out var id) ? id : -1;
    }

    private static List<(string, int)> Pairs(JsonElement e, string list, string key) =>
        e.TryGetProperty(list, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => (x.TryGetProperty(key, out var k) ? k.ValueKind == JsonValueKind.String ? k.GetString()! : k.ToString() : "", Int(x, "Amount"))).ToList()
            : [];

    private static List<(int, int)> IdPairs(JsonElement e, string list, string key) =>
        e.TryGetProperty(list, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.TryGetProperty(key, out var k) && k.ValueKind == JsonValueKind.Number).Select(x => (Int(x, key), Int(x, "Amount"))).ToList()
            : [];

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

    private static float Float(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetDouble() != 0));

    private static float[] Vec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray() : [];
}
