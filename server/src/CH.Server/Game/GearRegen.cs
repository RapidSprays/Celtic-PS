using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CH.Server.Game;

/// <summary>Regeneration per tick from one item. <see cref="OutOfCombatOnly"/> items (flutes, lutes…) stop in combat.</summary>
public readonly record struct Regen(int Health, int Energy, bool OutOfCombatOnly)
{
    public bool Any => Health > 0 || Energy > 0;
}

/// <summary>
/// Gear regeneration. The client ships no number for it (the live server keeps an item → permanent effect table), so it
/// is read from item descriptions: "regenerates the wearer's health by five points per tick", "gives 45 health and 30
/// energy regen", "health and energy regen by 50", "out of combat health regeneration by 20 points per tick"…
/// <b>Statically inferred</b>; 1,113 equippable items give regen this way (docs/decisions.md). Only the text before a
/// granted skill ("…grants the skill X which restores 1000 health") counts, and "up to N" (procs, drains) never does.
/// </summary>
public static class GearRegen
{
    /// <summary>Regen given by a description that names it but no amount (the Winter Flutes and Lutes).</summary>
    public const int UnstatedAmount = 10;

    private static readonly ConcurrentDictionary<int, Regen> Cache = new();

    private static readonly (string Word, int Value)[] NumberWords =
    [
        ("one hundred", 100), ("twenty-five", 25), ("hundred", 100), ("ninety", 90), ("eighty", 80), ("seventy", 70), ("sixty", 60),
        ("fifty", 50), ("forty", 40), ("thirty", 30), ("twenty", 20), ("fifteen", 15), ("twelve", 12), ("eleven", 11), ("ten", 10),
        ("nine", 9), ("eight", 8), ("seven", 7), ("six", 6), ("five", 5), ("four", 4), ("three", 3), ("two", 2), ("one", 1),
    ];

    private static readonly Regex Sentences = new(@"(?<=[.!])\s+", RegexOptions.Compiled);
    private static readonly Regex SkillClause = new(@"\bskill\b|\bwhich\b|\bspell\b", RegexOptions.Compiled);
    private static readonly Regex Trigger = new(@"regen|per tick|replenish", RegexOptions.Compiled);
    private static readonly Regex OutOfCombat = new(@"out of combat|not in combat", RegexOptions.Compiled);
    private static readonly Regex Numbers = new(@"(?<!up to )(?<!\d)(\d+)(?!\s*%|\d|\s*(?:second|minute|hour|cold|heat|fire|damage|poison|level|magic))", RegexOptions.Compiled);
    private static readonly Regex Keywords = new(@"health and energy|energy and health|health|energy", RegexOptions.Compiled);
    private static readonly Regex Adjacent = new(@"^\s*(?:points? of |extra |to |of )?$", RegexOptions.Compiled);

    public static Regen Of(ItemInfo item) => Cache.GetOrAdd(item.Id, _ => Parse(item.Description));

    public static Regen Parse(string description)
    {
        var text = description.ToLowerInvariant();
        foreach (var (word, value) in NumberWords) text = Regex.Replace(text, $@"\b{word}\b", value.ToString());
        int health = 0, energy = 0;
        var outOfCombat = false;
        foreach (var whole in Sentences.Split(text))
        {
            if (whole.Contains("when equipped with") || whole.Contains("occasionally")) continue; // set bonuses, procs
            var sentence = SkillClause.Split(whole)[0];
            if (!Trigger.IsMatch(sentence)) continue;
            if (OutOfCombat.IsMatch(sentence)) outOfCombat = true;
            var numbers = Numbers.Matches(sentence).Select(m => (Pos: m.Index, Value: int.Parse(m.Groups[1].Value))).ToList();
            var taken = new HashSet<int>();
            var got = new Dictionary<string, int>();
            foreach (Match k in Keywords.Matches(sentence))
            {
                var kind = k.Value.Contains(" and ") ? "both" : k.Value;
                if (got.ContainsKey(kind)) continue;
                var end = k.Index + k.Length;
                // "45 health" (a number right before) wins; otherwise the first free number after ("health … by 45").
                var before = numbers.LastOrDefault(n => !taken.Contains(n.Pos) && n.Pos < k.Index
                    && Adjacent.IsMatch(sentence[(n.Pos + n.Value.ToString().Length)..k.Index]));
                var after = numbers.FirstOrDefault(n => !taken.Contains(n.Pos) && n.Pos >= end && n.Pos - end < 70);
                var pick = before.Value > 0 ? before : after;
                if (pick.Value <= 0) continue;
                taken.Add(pick.Pos);
                got[kind] = pick.Value;
            }
            if (got.Count == 0 && outOfCombat && sentence.Contains("regeneration"))
            {
                if (sentence.Contains("health")) got["health"] = UnstatedAmount;
                if (sentence.Contains("energy")) got["energy"] = UnstatedAmount;
            }
            health += got.GetValueOrDefault("both") + got.GetValueOrDefault("health");
            energy += got.GetValueOrDefault("both") + got.GetValueOrDefault("energy");
        }
        return new Regen(health, energy, outOfCombat);
    }
}
