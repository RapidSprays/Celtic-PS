using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// A class's kit. <see cref="Abilities"/> are the abilities the class has for good (the abilities page, training);
/// <see cref="Skills"/> are learnt and <see cref="Equipment"/> worn when a new character first enters the world, and
/// <see cref="StartingLevels"/> sets abilities above level 1 at that moment. Skills and abilities come from
/// server/content/class-kits.json (owner-editable); the defaults below are used without it. Item and skill ids are real
/// client data: the level-1 class gear (items 10000-10016) and the tutorial starter skills (TutorialConstants.cs:555-566).
/// </summary>
public sealed record ClassKit(
    CharacterClass Class,
    int[] Skills,
    (int TemplateId, EquipSlot Slot)[] Equipment,
    int[] Abilities)
{
    public const int StartingCoins = 50;
    public const int MinorHealthPotion = 10040;
    public const int StartingPotions = 5;

    /// <summary>Abilities a new character starts above level 1 (ability id → level).</summary>
    public IReadOnlyDictionary<int, int> StartingLevels { get; init; } = new Dictionary<int, int>();

    /// <summary>Highlander base attributes (RaceTemplates race 1): vitality, strength, focus, dexterity.</summary>
    public static readonly (int Vit, int Str, int Foc, int Dex) BaseAttributes = (10, 5, 10, 5);

    private const int AdventurersBoots = 10006;
    // Before General: static fields are set up in the order they're written.
    public static readonly int[] Taming = [24, 25, 27, 28, 33, 34, 35, 40, 41, 44, 45, 47, 48, 50, 51];
    public static readonly int[] Riding = [36, 37, 42, 43, 46, 49, 52];

    /// <summary>
    /// Abilities every class has: Physical Fitness, First Aid, the five defensive ones, Critical Strike and Critical
    /// Skills (owner's rule, 2026-10-15), then (owner's rule, 2026-10-14)
    /// every pet taming and mount riding ability: Dog 24, Rabbit 25, Bear 27, Spirit 28, Wolf 33, Spider 34, Boar 35,
    /// Dragon 40, Chicken 41, Eagle 44, Phoenix 45, Pig 47, Seedling 48, Reindeer 50 and Imp 51 Taming; Wolf 36, Bear 37,
    /// Horse 42, Elk 43, Tiger 46, Hellsteed 49 and Nightmare Steed 52 Riding.
    /// </summary>
    private static readonly int[] General = [9, 14, 19, 20, 21, 22, 23, 29, 30, .. Taming, .. Riding];

    public static readonly IReadOnlyDictionary<CharacterClass, ClassKit> Defaults = new Dictionary<CharacterClass, ClassKit>
    {
        [CharacterClass.Warrior] = new(CharacterClass.Warrior, [1, 47, 2, 3],
            [(10000, EquipSlot.Weapon), (10005, EquipSlot.Offhand), (10012, EquipSlot.Chest), (10007, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [0, 1, 2, 15, 7, .. General]),
        [CharacterClass.Druid] = new(CharacterClass.Druid, [38, 8, 5, 3],
            [(10003, EquipSlot.Weapon), (10015, EquipSlot.Chest), (10010, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [3, 16, 10, .. General]),
        [CharacterClass.Mage] = new(CharacterClass.Mage, [11, 42, 43, 55],
            [(10004, EquipSlot.Weapon), (10016, EquipSlot.Chest), (10011, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [5, 12, 13, .. General]),
        [CharacterClass.Ranger] = new(CharacterClass.Ranger, [13, 52, 14, 3],
            [(10002, EquipSlot.Weapon), (10013, EquipSlot.Chest), (10008, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [6, 15, 18, 8, .. General]),
        [CharacterClass.Rogue] = new(CharacterClass.Rogue, [53, 18, 16, 17],
            [(10001, EquipSlot.Weapon), (10014, EquipSlot.Chest), (10009, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [4, 15, 18, 11, .. General]),
    };

    /// <summary>The kits in use: <see cref="Defaults"/>, with class-kits.json's skills and abilities once loaded.</summary>
    public static IReadOnlyDictionary<CharacterClass, ClassKit> All { get; private set; } = Defaults;

    public static ClassKit For(int @class) =>
        All.TryGetValue((CharacterClass)@class, out var kit) ? kit : All[CharacterClass.Warrior];

    /// <summary>
    /// Reads class-kits.json: "allClasses" abilities every class gets, and per class its starting "skills" and its own
    /// "abilities", each entry an id (names are only there to read), abilities with an optional starting "level". A
    /// class the file leaves out keeps its defaults; equipment always comes from the defaults.
    /// </summary>
    public static void Load(string? path) => All = Read(path) ?? Defaults;

    /// <summary>The kits class-kits.json describes, or null without the file.</summary>
    public static IReadOnlyDictionary<CharacterClass, ClassKit>? Read(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        static List<(int Id, int Level)> Entries(JsonElement e, string name) => e.TryGetProperty(name, out var list)
            ? list.EnumerateArray().Select(x => (x.GetProperty("id").GetInt32(), x.TryGetProperty("level", out var l) ? l.GetInt32() : 1)).ToList()
            : [];
        var shared = root.TryGetProperty("allClasses", out var all) ? Entries(all, "abilities") : General.Select(a => (Id: a, Level: 1)).ToList();
        var kits = new Dictionary<CharacterClass, ClassKit>();
        foreach (var (cls, kit) in Defaults)
        {
            if (!root.TryGetProperty("classes", out var classes) || !classes.TryGetProperty(cls.ToString(), out var entry))
            {
                kits[cls] = kit;
                continue;
            }
            var own = entry.TryGetProperty("abilities", out _) ? Entries(entry, "abilities") : kit.Abilities.Except(General).Select(a => (Id: a, Level: 1)).ToList();
            var abilities = own.Concat(shared).GroupBy(a => a.Id).Select(g => (Id: g.Key, Level: g.Max(a => a.Level))).ToList();
            kits[cls] = kit with
            {
                Skills = entry.TryGetProperty("skills", out _) ? Entries(entry, "skills").Select(s => s.Id).Distinct().ToArray() : kit.Skills,
                Abilities = abilities.Select(a => a.Id).ToArray(),
                StartingLevels = abilities.Where(a => a.Level > 1).ToDictionary(a => a.Id, a => a.Level),
            };
        }
        return kits;
    }
}
