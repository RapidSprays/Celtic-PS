using System.Text.RegularExpressions;

namespace CH.Server.Game;

/// <summary>
/// Pet and mount rules from the client (docs/pets-mounts-notes.md). The client draws a pet only while its owner
/// has a status effect and none of the "Hungry" type (CompanionManager.Hungry), and draws a mount only without the
/// Dismounted effect (MountsManager.CheckForMount).
/// </summary>
public static class Pets
{
    public const int DismountedEffect = 20007;
    public const int WhistleSkill = 8066;      // Mount Whistle's skill link
    public const int StableRemountSkill = 8065; // the stablemaster's CASTSKILL 8065 <gold>
    public const int NewFedEffect = 5647;       // "New Fed Dog", fed by every "New Pet Food"
    public const double DismountedSeconds = 3600;

    /// <summary>Pet family → (Hungry effect, Well Fed effect).</summary>
    private static readonly Dictionary<string, (int Hungry, int Fed)> Families = new()
    {
        ["dog"] = (5588, 5589), ["rabbit"] = (5601, 5602), ["bear"] = (5713, 5711), ["spirit"] = (5714, 5712), ["wolf"] = (5716, 5717),
        ["spider"] = (5731, 5732), ["boar"] = (5733, 5734), ["chicken"] = (5974, 5975), ["hatchling"] = (5976, 5977),
        ["eagle"] = (5999, 6000), ["phoenix"] = (6136, 6137), ["pig"] = (6171, 6172), ["seedling"] = (6177, 6178),
        ["reindeer"] = (6270, 6271), ["imp"] = (6303, 6304),
    };

    private static readonly Dictionary<string, string> SpeciesFamily = new()
    {
        ["mastiff"] = "dog", ["hound"] = "dog", ["dog"] = "dog", ["hare"] = "rabbit", ["rabbit"] = "rabbit", ["wisp"] = "spirit",
    };

    /// <summary>Food skill (the food item's skill link) → the Well Fed effect it gives.</summary>
    private static readonly Dictionary<int, int> FoodEffect = new()
    {
        [7582] = 5589, [7870] = 5602, [8036] = 5711, [8037] = 5712, [8039] = 5717, [8059] = 5732, [8060] = 5734, [8149] = 5975,
        [8150] = 5977, [8176] = 6000, [8321] = 6137, [8361] = 6172, [8365] = 6178, [8430] = 6271, [8467] = 6304, [7900] = NewFedEffect,
    };

    public static IEnumerable<int> AllPetEffects => Families.Values.SelectMany(f => new[] { f.Hungry, f.Fed }).Append(NewFedEffect);

    public static bool IsFedEffect(int id) => id == NewFedEffect || Families.Values.Any(f => f.Fed == id);

    /// <summary>A pet item's family from the last word of its name ("Tiny Brown Mastiff" → dog). Unknown → dog.</summary>
    public static (int Hungry, int Fed) FamilyOf(ItemInfo pet)
    {
        var word = pet.Name.Trim().Split(' ').Where(w => !Regex.IsMatch(w, "^I+$")).LastOrDefault()?.ToLowerInvariant() ?? "";
        var family = SpeciesFamily.GetValueOrDefault(word, word);
        if (pet.Name.StartsWith("Spirit ", StringComparison.Ordinal) && !Families.ContainsKey(family)) family = "spirit";
        return Families.GetValueOrDefault(family, Families["dog"]);
    }

    public static int? FedEffectOf(ItemInfo food) => FoodEffect.TryGetValue(food.SkillLinkId, out var e) ? e : null;

    /// <summary>How long a food keeps a pet fed, from its description ("for 10 minutes", "for 6 hours"); default an hour.</summary>
    public static double FedSeconds(ItemInfo food)
    {
        var m = Regex.Match(food.Description, @"for (\d+) (minute|hour)", RegexOptions.IgnoreCase);
        if (!m.Success) return 3600;
        var n = int.Parse(m.Groups[1].Value);
        return m.Groups[2].Value.StartsWith("hour", StringComparison.OrdinalIgnoreCase) ? n * 3600 : n * 60;
    }
}
