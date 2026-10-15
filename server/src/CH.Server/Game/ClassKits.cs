namespace CH.Server.Game;

/// <summary>
/// Level-1 starting kit for a class. Item and skill ids are real client data: the level-1 class gear (items 10000-10016),
/// the tutorial starter skills (TutorialConstants.cs:555-566) and class skill books. HP, energy, attack and defence are
/// <b>approximated</b>: the client ships no formula for them (docs/decisions.md).
/// </summary>
public sealed record ClassKit(
    CharacterClass Class,
    int[] Skills,
    (int TemplateId, EquipSlot Slot)[] Equipment,
    int[] Abilities,
    int Health,
    int Energy,
    int Attack,
    int Defence)
{
    public const int StartingCoins = 50;
    public const int MinorHealthPotion = 10040;
    public const int StartingPotions = 5;

    /// <summary>Highlander base attributes (RaceTemplates race 1): vitality, strength, focus, dexterity.</summary>
    public static readonly (int Vit, int Str, int Foc, int Dex) BaseAttributes = (10, 5, 10, 5);

    private const int AdventurersBoots = 10006;
    // Before General: static fields are set up in the order they're written.
    public static readonly int[] Taming = [24, 25, 27, 28, 33, 34, 35, 40, 41, 44, 45, 47, 48, 50, 51];
    public static readonly int[] Riding = [36, 37, 42, 43, 46, 49, 52];

    /// <summary>
    /// Abilities every class has: Physical Fitness, First Aid, the five defensive ones, then (owner's rule, 2026-10-14)
    /// every pet taming and mount riding ability: Dog 24, Rabbit 25, Bear 27, Spirit 28, Wolf 33, Spider 34, Boar 35,
    /// Dragon 40, Chicken 41, Eagle 44, Phoenix 45, Pig 47, Seedling 48, Reindeer 50 and Imp 51 Taming; Wolf 36, Bear 37,
    /// Horse 42, Elk 43, Tiger 46, Hellsteed 49 and Nightmare Steed 52 Riding.
    /// </summary>
    private static readonly int[] General = [9, 14, 19, 20, 21, 22, 23, .. Taming, .. Riding];

    public static readonly IReadOnlyDictionary<CharacterClass, ClassKit> All = new Dictionary<CharacterClass, ClassKit>
    {
        [CharacterClass.Warrior] = new(CharacterClass.Warrior, [1, 47, 2, 3],
            [(10000, EquipSlot.Weapon), (10005, EquipSlot.Offhand), (10012, EquipSlot.Chest), (10007, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [0, 1, 2, 15, 7, .. General], Health: 80, Energy: 30, Attack: 12, Defence: 18),
        [CharacterClass.Druid] = new(CharacterClass.Druid, [38, 8, 5, 3],
            [(10003, EquipSlot.Weapon), (10015, EquipSlot.Chest), (10010, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [3, 16, 10, .. General], Health: 60, Energy: 45, Attack: 10, Defence: 14),
        [CharacterClass.Mage] = new(CharacterClass.Mage, [11, 42, 43, 55],
            [(10004, EquipSlot.Weapon), (10016, EquipSlot.Chest), (10011, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [5, 12, 13, .. General], Health: 55, Energy: 50, Attack: 10, Defence: 12),
        [CharacterClass.Ranger] = new(CharacterClass.Ranger, [13, 52, 14, 3],
            [(10002, EquipSlot.Weapon), (10013, EquipSlot.Chest), (10008, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [6, 15, 18, 8, .. General], Health: 70, Energy: 35, Attack: 12, Defence: 15),
        [CharacterClass.Rogue] = new(CharacterClass.Rogue, [53, 18, 16, 17],
            [(10001, EquipSlot.Weapon), (10014, EquipSlot.Chest), (10009, EquipSlot.Leg), (AdventurersBoots, EquipSlot.Feet)],
            [4, 15, 18, 11, .. General], Health: 70, Energy: 35, Attack: 12, Defence: 15),
    };

    public static ClassKit For(int @class) =>
        All.TryGetValue((CharacterClass)@class, out var kit) ? kit : All[CharacterClass.Warrior];
}
