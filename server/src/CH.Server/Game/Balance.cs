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

    /// <summary>The pierce, slash and crush part of <see cref="Damage"/>: what Steady Aim and Frenzy boost by a percentage.</summary>
    public int PhysicalDamage { get; init; }

    /// <summary>Damage by type (0 pierce, 1 slash, 2 crush, 3 heat, 4 cold, 5 magic, 6 poison, 7 divine, 8 chaos, 9 true).</summary>
    public IReadOnlyList<(int Type, int Amount)> DamageByType { get; init; } = [];

    /// <summary>The client's DamageType names as the type numbers monsters' damage and resists use.</summary>
    public static readonly IReadOnlyDictionary<string, int> DamageTypeIds = new Dictionary<string, int>
    {
        ["PiercingDamage"] = 0, ["SlashingDamage"] = 1, ["CrushingDamage"] = 2, ["HeatDamage"] = 3, ["ColdDamage"] = 4,
        ["MagicDamage"] = 5, ["PoisonDamage"] = 6, ["DivineDamage"] = 7, ["ChaosDamage"] = 8, ["TrueDamage"] = 9,
    };

    private static readonly System.Text.RegularExpressions.Regex HasteText = new(
        @"attack speed (?:is )?(?:increased |improved )?by (\d+)%|attack (\d+)% faster|speed of your attacks by (\d+)%|attack speed by (\d+)%",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex AttackPercentText = new(
        @"total Attack is increased by (\d+)%", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Percent to total Attack the item's description promises ("your total Attack is increased by 15%": Spectral Ring
    /// of the Reaper). The client data has no field for it.
    /// </summary>
    public int AttackPercent =>
        Slot is not EquipSlot.Unequipable && AttackPercentText.Match(Description) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    /// <summary>
    /// Attack speed bonus the item's description promises ("Boosts attack speed by 30%", "attack 20% faster"): Ring of
    /// Haste, Spectral Ring of the Reaper, the Eldritch/Runic Quivers of Savagery, haste gloves and daggers. The client
    /// data has no field for it. Items say these don't stack with each other, so the character sheet takes the highest.
    /// </summary>
    public int HastePercent
    {
        get
        {
            if (Slot is EquipSlot.Unequipable || HasteText.Match(Description) is not { Success: true } m) return 0;
            return int.Parse(m.Groups.Values.Skip(1).First(g => g.Success).Value);
        }
    }

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

public sealed record SkillLevelInfo(float InitialDamage, float BaseDamage, float CastingTime, float RechargeTime, int EnergyCost, int TemplateLevel = 0, int MinLevel = 0)
{
    /// <summary>Chance in percent that being hit doesn't interrupt the cast.</summary>
    public int ChargingProtection { get; init; } = 100;
}

/// <summary>A skill template. Heals have negative damage values. <see cref="CastTarget"/> is Self, Enemy, Friendly, Group or Fish.</summary>
public sealed record SkillInfo(int Id, string Name, int AbilityId, float CastRange, bool IncludesWeaponAttack, string CastTarget, int StatusEffectId, IReadOnlyList<SkillLevelInfo> Levels)
{
    /// <summary>Radius in metres (the client's tooltip &lt;AOE&gt;): enemy skills hit everything around the target; group skills reach party members around the caster.</summary>
    public float AreaOfEffect { get; init; }

    /// <summary>
    /// Owner's rule (2026-10-16, skill-overrides.json "castWithWeaponDelay"): the cast takes the weapon's attack delay on top
    /// of its own cast time (Shatter, Double Attack, Barbed Shot, Rend), and lands at its end.
    /// </summary>
    public bool CastWithWeaponDelay { get; init; }

    /// <summary>skill-overrides.json "weaponDelayOffset": seconds added to a weapon-delay cast (−0.5 = half a second sooner).</summary>
    public double WeaponDelayOffset { get; init; }

    /// <summary>
    /// skill-overrides.json "evasionPercent": how much of a monster's evasion counts against this skill (50 = half; 100,
    /// the default, as the game has it). "minLandChance": the lowest chance (percent) it lands on a monster, whatever the
    /// monster's evasion (0 = no floor).
    /// </summary>
    public double EvasionPercent { get; init; } = 100;
    public double MinLandChance { get; init; }

    /// <summary>
    /// skill-overrides.json "damageAsDot": the hit's damage (same formula) is dealt as damage over time instead, in
    /// equal ticks under the status effect <c>Effect</c> (its icon and type; Pounce: Snakebite's poison), the first on the hit.
    /// </summary>
    public (int Effect, int Ticks, double Seconds)? DamageAsDot { get; init; }

    /// <summary>What evades the skill: 0 physical, 1 spell, 2 movement, 3 wounding, 4 weakening, 5 mental; −1 nothing.</summary>
    public int AvoidanceType { get; init; } = -1;

    /// <summary>Attribute the skill (and its status effect) scales with: 0 strength, 1 dexterity, 2 focus, 3 vitality, −1 none.</summary>
    public int PrimaryStat { get; init; } = -1;

    /// <summary>What the attribute is divided by before its square root is taken (Sharp Shot: dexterity ÷ 6).</summary>
    public float PrimaryStatDivisor { get; init; }

    /// <summary>Item subtypes the skill needs in the weapon or offhand slot (SkillWeaponRequirements); empty → any weapon.</summary>
    public IReadOnlyList<string> WeaponRequirements { get; init; } = [];

    /// <summary>
    /// The client's weapon-type enum (SkillWeaponRequirements) as ItemTemplate subtypes, matched against the same skills'
    /// requirements by name in Master's Grimoire (Double Shot [11] = Bow, Shield Bash [12] = Shield, Double Attack's list
    /// = every melee weapon or none). 0 is "no weapon".
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> WeaponTypes = new Dictionary<int, string>
    {
        [0] = "None", [1] = "Sword", [2] = "Axe", [3] = "Blunt", [8] = "Staff", [9] = "Dagger", [10] = "Wand", [11] = "Bow",
        [12] = "Shield", [13] = "SwordTwoHanded", [14] = "AxeTwoHanded", [15] = "BluntTwoHanded", [19] = "Spear", [20] = "Totem",
        [23] = "HandToHand", [29] = "NoveltyLute", [31] = "NoveltyFlute", [54] = "FishingRod",
    };

    public SkillLevelInfo Level(int level) => Levels.Count == 0 ? new SkillLevelInfo(0, 0, 0, 5, 0) : Levels[Math.Clamp(level, 0, Levels.Count - 1)];

    /// <summary>The skill's PvP version of a rank, for hits on players (the rank as usual when it has none).</summary>
    public SkillLevelInfo PvpLevel(int level) => PvpLevels.Count == 0 ? Level(level) : PvpLevels[Math.Clamp(level, 0, PvpLevels.Count - 1)];

    /// <summary>PvP ranks (SkillTemplate.PvpLevels): many skills hit players for less than monsters.</summary>
    public IReadOnlyList<SkillLevelInfo> PvpLevels { get; init; } = [];

    /// <summary>
    /// Master's Grimoire's formula for a skill's amount (damage, heal or buff strength): ⌈base + initial × (1 +
    /// √(ability ÷ 100) + √(attribute ÷ divisor))⌉. Ability is 0 for skills without one; <paramref name="statRatio"/> is
    /// <see cref="CharacterSheet.StatRatio"/>. Heals have negative numbers in the data, so their sizes are used.
    /// </summary>
    /// <remarks>
    /// Rounded to the nearest whole number, as live shows it (2026-10-15: a Giant Hexbound Imp at 2,000 Imp Taming hits
    /// for at most 3,852 = 704 × (1 + √20) = 3,852.38; Spirit III at 2,300: 7,535 = 7,534.58). Master's Grimoire rounds up.
    /// </remarks>
    public static double Amount(SkillLevelInfo level, float ability, double statRatio) =>
        Math.Round(Math.Abs(level.BaseDamage) + Math.Abs(level.InitialDamage) * (1 + Math.Sqrt(Math.Max(0, ability) / 100.0) + Math.Sqrt(Math.Max(0, statRatio))), MidpointRounding.AwayFromZero);

    /// <summary>Highest rank a character of this level may train (CSkillTemplateManager.GetMaxLevel).</summary>
    public int MaxRank(int playerLevel) => Math.Min(SkillLevels.TrainableRanks - 1,
        Levels.Where(l => l.MinLevel <= playerLevel).Select(l => l.TemplateLevel).DefaultIfEmpty(0).Max());
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
    /// <summary>Seconds between damage-over-time ticks (Master's Grimoire's status list; null → the server's 3 s).</summary>
    public float? TickSeconds { get; init; }

    /// <summary>Damage type of damage over time (0 pierce … 9 true), for resists; null → not resisted.</summary>
    public int? DamageType { get; init; }

    /// <summary>
    /// Ranks the client's own table has. Player skills' effects are extended past it on the server (gear ranks such as
    /// Exalted Dragonlord's Rapid Shot +8), but the client is only ever sent a rank it knows.
    /// </summary>
    public int ClientLevels { get; init; } = -1;

    /// <summary>The rank to send the client for a server rank.</summary>
    public int ClientLevel(int index) => Math.Clamp(index, 0, Math.Max(0, (ClientLevels >= 0 ? ClientLevels : Levels.Count) - 1));

    public StatusEffectLevel? Level(int index) => Levels.Count == 0 ? null : Levels[Math.Clamp(index, 0, Levels.Count - 1)];
}

/// <summary>A cooking recipe (CraftingTemplate): chances in percent, ability thresholds, results and timing.</summary>
public sealed record RecipeInfo(int Id, string Name, int CostId, int Level, int Xp, int MinAbility, int Difficulty, (int Min, int Max) Success,
    (int Min, int Max) Critical, (int Min, int Max) Master, int Failure, int Result, int CriticalResult, int MasterResult, float CraftingTime, int OptionalId)
{
    /// <summary>The client's own chance curve (GUICraftingPage): min + max(0, a − minAbility)/(that + difficulty) × (max − min).</summary>
    public static double Chance(int ability, int minAbility, (int Min, int Max) range, int difficulty)
    {
        var over = Math.Max(0, ability - minAbility);
        return (range.Min + (over + difficulty == 0 ? 0 : (double)over / (over + difficulty)) * Math.Max(0, range.Max - range.Min)) / 100.0;
    }
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
    /// <summary>Equipment sets and armour auras (Game/SetBonuses.cs); set by the server after loading.</summary>
    public SetBonusCatalog SetBonuses { get; set; } = SetBonusCatalog.Empty;

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

    private Dictionary<int, IReadOnlyList<(int Skill, int Percent)>>? _recast;

    private static readonly System.Text.RegularExpressions.Regex RecastText = new(
        @"(?:recast(?: time)?|the) of (?:your )?(?<skill>[A-Z][A-Za-z' ]+?)(?: skills?)? by (?<pct>\d+)%|Reduces the (?<skill>[A-Z][A-Za-z' ]+?) by (?<pct>\d+)%");

    /// <summary>
    /// Cooldown cuts an item's description promises ("Reduces the recast time of Rapid Shot by 20%", "decreases the
    /// recast of your Rupture skill by 15%", "…of Energy Shield and Energy Boost by 25%"), matched to skills by name.
    /// The client data has no field for them.
    /// </summary>
    public IReadOnlyList<(int Skill, int Percent)> RecastReductions(ItemInfo item)
    {
        if (_recast is null)
        {
            string Key(string name) => new(name.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
            var byName = new Dictionary<string, int>();
            foreach (var skill in Skills.Values.OrderBy(s => s.Id)) byName.TryAdd(Key(skill.Name), skill.Id);
            var map = new Dictionary<int, IReadOnlyList<(int, int)>>();
            foreach (var i in Items.Values)
            {
                if (i.Slot is EquipSlot.Unequipable || RecastText.Match(i.Description) is not { Success: true } m) continue;
                var pct = int.Parse(m.Groups["pct"].Value);
                var skills = m.Groups["skill"].Value.Split(" and ", StringSplitOptions.TrimEntries)
                    .Select(n => byName.TryGetValue(Key(n), out var id) ? id : -1).Where(id => id > 0).Select(id => (id, pct)).ToList();
                if (skills.Count > 0) map[i.Id] = skills;
            }
            _recast = map;
        }
        return _recast.TryGetValue(item.Id, out var list) ? list : [];
    }

    /// <summary>Ability names by id (the client's Abilities: 6 Bow, 8 Ranged Combat, 19 Fortitude…).</summary>
    public IReadOnlyDictionary<int, string> AbilityNames { get; init; } = new Dictionary<int, string>();

    /// <summary>Swaps in a changed status effect (tick and type from the Grimoire import, the owner's overrides).</summary>
    public void ReplaceStatusEffect(StatusEffectInfo effect) => ((Dictionary<int, StatusEffectInfo>)StatusEffects)[effect.Id] = effect;

    /// <summary>
    /// Adds each status effect's tick time and damage type from status-effects.json (scripts/import-grimoire.py,
    /// beside the balance JSON). Returns how many effects it covered.
    /// </summary>
    public int LoadStatusDetails(string? path)
    {
        if (path is null || !File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var count = 0;
        foreach (var e in doc.RootElement.GetProperty("statusEffects").EnumerateObject())
        {
            if (!int.TryParse(e.Name, out var id) || !StatusEffects.TryGetValue(id, out var info)) continue;
            ReplaceStatusEffect(info with
            {
                TickSeconds = e.Value.TryGetProperty("tick", out var t) ? t.GetSingle() : info.TickSeconds,
                DamageType = e.Value.TryGetProperty("type", out var d) ? d.GetInt32() : info.DamageType,
            });
            count++;
        }
        return count;
    }

    /// <summary>Swaps in a changed skill (server/content/skill-overrides.json, Game/SkillOverrides.cs).</summary>
    public void ReplaceSkill(SkillInfo skill) => ((Dictionary<int, SkillInfo>)Skills)[skill.Id] = skill;

    /// <summary>Cooking recipes (CraftingTemplates): recipe id → name.</summary>
    public IReadOnlyDictionary<int, string> Recipes { get; private init; } = new Dictionary<int, string>();
    /// <summary>Cooking recipes in full (CraftingTemplates), by recipe id.</summary>
    public IReadOnlyDictionary<int, RecipeInfo> RecipeTemplates { get; private init; } = new Dictionary<int, RecipeInfo>();

    /// <summary>Reputation factions (FactionTemplates): level thresholds and titles.</summary>
    public IReadOnlyDictionary<int, FactionInfo> Factions { get; private init; } = new Dictionary<int, FactionInfo>();
    public IReadOnlyDictionary<int, MobInfo> Mobs { get; }
    public IReadOnlyDictionary<int, ZoneInfo> Zones { get; }
    public IReadOnlyDictionary<int, int[]> MobSets { get; }
    public int Version { get; }
    public IReadOnlyList<ConversationInfo> Conversations { get; }
    public IReadOnlyDictionary<int, StatusEffectInfo> StatusEffects { get; }

    /// <summary>Official spawn-point ids that carry quest/shop icons for a mob template (SpawnIconInfoCollections).</summary>
    public IReadOnlyDictionary<int, List<int>> SpawnIconIdsByMob { get; }
    public bool IsLoaded => Items.Count > 0;

    public static Balance Load(string? path, string? customItemsPath = null)
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
                PhysicalDamage = Pairs(e, "DamageTypes", "DamageType").Where(d => d.Item1 is "PiercingDamage" or "SlashingDamage" or "CrushingDamage").Sum(d => d.Item2),
                DamageByType = Pairs(e, "DamageTypes", "DamageType").Where(d => ItemInfo.DamageTypeIds.ContainsKey(d.Item1))
                    .Select(d => (ItemInfo.DamageTypeIds[d.Item1], d.Item2)).ToList(),
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
                ? lv.EnumerateArray().Select(l => new SkillLevelInfo(Float(l, "InitialDamage"), Float(l, "BaseDamage"), Float(l, "CastingTime"), Float(l, "RechargeTime"), Int(l, "EnergyCost"), Int(l, "TemplateLevel"), Int(l, "MinLevel"))
                    { ChargingProtection = l.TryGetProperty("ChargingProtection", out var cp) && cp.ValueKind == JsonValueKind.Number ? cp.GetInt32() : 100 }).ToList()
                : [];
            var pvpLevels = e.TryGetProperty("PvpLevels", out var plv) && plv.ValueKind == JsonValueKind.Array
                ? plv.EnumerateArray().Select(l => new SkillLevelInfo(Float(l, "InitialDamage"), Float(l, "BaseDamage"), Float(l, "CastingTime"), Float(l, "RechargeTime"), Int(l, "EnergyCost"), Int(l, "TemplateLevel"), Int(l, "MinLevel"))
                    { ChargingProtection = l.TryGetProperty("ChargingProtection", out var pcp) && pcp.ValueKind == JsonValueKind.Number ? pcp.GetInt32() : 100 }).ToList()
                : [];
            if (SkillLevels.IsPlayerSkill(Int(e, "AbilityId"), levels.Count)) levels = SkillLevels.Extend(levels);
            if (SkillLevels.IsPlayerSkill(Int(e, "AbilityId"), pvpLevels.Count)) pvpLevels = SkillLevels.Extend(pvpLevels);
            skills[id] = new SkillInfo(id, Str(e, "Name"), Int(e, "AbilityId"), Float(e, "CastRange"), Bool(e, "IncludesWeaponAttack"), Str(e, "CastTarget"), Int(e, "StatusEffectId"), levels)
            {
                AreaOfEffect = Float(e, "AreaOfEffect"),
                PvpLevels = pvpLevels,
                AvoidanceType = e.TryGetProperty("AvoidanceType", out var avoid) && avoid.ValueKind == JsonValueKind.Number ? avoid.GetInt32() : -1,
                PrimaryStat = Int(e, "PrimaryStatModifier"),
                PrimaryStatDivisor = Float(e, "PrimaryStatDivisor"),
                WeaponRequirements = e.TryGetProperty("SkillWeaponRequirements", out var req) && req.ValueKind == JsonValueKind.Array
                    ? req.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Number)
                        .Select(r => SkillInfo.WeaponTypes.GetValueOrDefault(r.GetInt32())).OfType<string>().ToList()
                    : [],
            };
        }

        CustomItems.Apply(items, CustomItems.Load(customItemsPath));

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
        // Player skills' buffs and debuffs grow past their last rank like the skills' damage does, so gear ranks above
        // rank 50 count (Rapid Shot +8 lasts 4 s longer; amounts keep their last step).
        foreach (var effectId in skills.Values.Where(s => s.StatusEffectId > 0 && s.Levels.Count >= SkillLevels.Ranks).Select(s => s.StatusEffectId).Distinct().ToList())
            if (statusEffects.TryGetValue(effectId, out var effect) && effect.Levels.Count >= 2 && effect.Levels.Count < SkillLevels.Ranks)
                statusEffects[effectId] = effect with { Levels = SkillLevels.ExtendEffect(effect.Levels), ClientLevels = effect.Levels.Count };

        var recipes = new Dictionary<int, string>();
        var recipeTemplates = new Dictionary<int, RecipeInfo>();
        foreach (var (key, e) in Entries(root, "CraftingTemplates"))
        {
            recipes[int.Parse(key)] = Str(e, "RecipeName");
            recipeTemplates[Int(e, "RecipeId")] = new RecipeInfo(Int(e, "RecipeId"), Str(e, "RecipeName"), Int(e, "CostId"), Int(e, "RecipeLevel"), Int(e, "RecipeXp"),
                Int(e, "MinAbility"), Int(e, "Difficulty"), (Int(e, "SuccessMinChance"), Int(e, "SuccessMaxChance")),
                (Int(e, "CriticalMinChance"), Int(e, "CriticalMaxChance")), (Int(e, "MasterMinChance"), Int(e, "MasterMaxChance")),
                Int(e, "FailureItemReward"), Int(e, "SuccessItemReward"), Int(e, "CriticalItemReward"), Int(e, "MasterItemReward"),
                Float(e, "CraftingTime"), Int(e, "OptionalID"));
        }

        var factions = new Dictionary<int, FactionInfo>();
        foreach (var (key, e) in Entries(root, "FactionTemplates"))
            factions[int.Parse(key)] = new FactionInfo(int.Parse(key), Str(e, "Name"),
                e.TryGetProperty("Levels", out var fl) && fl.ValueKind == JsonValueKind.Array
                    ? fl.EnumerateArray().Select(l => new FactionLevel(Int(l, "Value"), Int(l, "Points"), Str(l, "Title"))).ToList() : [],
                Int(e, "Min"), Int(e, "Max"));

        var version = root.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var abilityNames = Entries(root, "Abilities").Select(x => x.Item2)
            .Where(e => e.TryGetProperty("Id", out var aid) && aid.ValueKind == JsonValueKind.Number)
            .GroupBy(e => e.GetProperty("Id").GetInt32()).ToDictionary(g => g.Key, g => Str(g.First(), "Name"));
        // The owner's own monsters (content/custom-monsters.json: Gilthral the Gladiator), copies of existing templates.
        if (customItemsPath is not null)
            CustomContent.ApplyMonsters(mobs, CustomContent.LoadMonsters(Path.Combine(Path.GetDirectoryName(customItemsPath) ?? ".", "custom-monsters.json")));
        // The owner's gear with typed-in numbers, beside custom-items.json (content/custom-gear.json).
        if (customItemsPath is not null)
            CustomGear.Apply(items, Path.Combine(Path.GetDirectoryName(customItemsPath) ?? ".", "custom-gear.json"), abilityNames, skills);
        return new Balance(items, skills, mobs, zones, mobSets, version, conversations, spawnIcons, statusEffects)
            { Recipes = recipes, RecipeTemplates = recipeTemplates, Factions = factions, AbilityNames = abilityNames };
    }

    /// <summary>Whether an item is an openable container (see <see cref="ChestCatalog"/>).</summary>
    private Dictionary<int, int>? _equipSkillRanks;

    /// <summary>
    /// The rank (as an index) of the skill a pet or mount gives. Each family (the items giving the same skill, without
    /// the TEST and Template dev items) is ordered by required level, then item id. Families of 9 give ranks 1-9 in that
    /// order (Tiny Hexbound Imp → Hexbound Pierce rank 1 = 46, Small → rank 2 = 126, Giant → rank 6 = 704, confirmed on
    /// live). Families of 27 are 6 sizes × 4 colours plus Spirit I-III: pets rank size by size (Tiny Brown, Tan, Black,
    /// Silver = 1-4, Small = 5-8…), mounts colour by colour (Tame…Primal Brown = 1-6, then Red, Black, Silver), as their
    /// skills' rank tables show (Howl: 40, 80, 160, 320, 480, 640, then 44…; a Ferocious Brown War Wolf's Howl is rank 5 on
    /// live, 2026-10-16). Spirit I-III are ranks 25-27.
    /// </summary>
    public int EquipSkillRank(int itemId)
    {
        _equipSkillRanks ??= Items.Values
            .Where(i => i.EquipSkillId > 0 && i.Slot is EquipSlot.Companion or EquipSlot.Mount
                        && !i.Name.StartsWith("TEST ", StringComparison.Ordinal) && !i.Name.StartsWith("Template ", StringComparison.Ordinal))
            .GroupBy(i => i.EquipSkillId)
            .SelectMany(g =>
            {
                var family = g.OrderBy(i => i.LevelRequirement).ThenBy(i => i.Id).ToList();
                var mount = family[0].Slot == EquipSlot.Mount;
                return family.Select((item, n) => (item.Id, Rank: family.Count == 27 && mount && n < 24 ? n % 4 * 6 + n / 4 : n));
            })
            .ToDictionary(x => x.Id, x => x.Rank);
        return _equipSkillRanks.GetValueOrDefault(itemId);
    }

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

    private static readonly IReadOnlySet<string> NoTags = new HashSet<string>();
    private Dictionary<int, HashSet<string>>? _skillTags;

    /// <summary>
    /// Passive-tree tags for a skill: the classes that can learn it (lower case, from skill books' class rules and the
    /// class kits) and "heal" for healing skills.
    /// </summary>
    public IReadOnlySet<string> SkillTags(int skillId) =>
        LazyInitializer.EnsureInitialized(ref _skillTags, BuildSkillTags).TryGetValue(skillId, out var tags) ? tags : NoTags;

    private Dictionary<int, HashSet<string>> BuildSkillTags()
    {
        var tags = new Dictionary<int, HashSet<string>>();
        void Add(int skill, string tag)
        {
            if (!tags.TryGetValue(skill, out var set)) tags[skill] = set = [];
            set.Add(tag);
        }
        foreach (var kit in ClassKit.All.Values)
            foreach (var skill in kit.Skills) Add(skill, kit.Class.ToString().ToLowerInvariant());
        foreach (var book in Items.Values.Where(i => i.Classes.Count > 0 && i.SkillLink.StartsWith("Learn", StringComparison.Ordinal) || i.SkillLinkId > 0 && i.Classes.Count > 0))
            if (SkillTaughtBy(book) is { } skill)
                foreach (var cls in book.Classes) Add(skill.Id, cls.ToLowerInvariant());
        foreach (var skill in Skills.Values.Where(k => k.Levels.Count > 0 && (k.Levels[0].InitialDamage < 0 || k.Levels[0].BaseDamage < 0)))
            Add(skill.Id, "heal");
        return tags;
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
