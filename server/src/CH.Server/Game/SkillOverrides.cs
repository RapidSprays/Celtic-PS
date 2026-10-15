using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's own skill numbers (server/content/skill-overrides.json), applied over the client's skill templates at
/// start-up: a damage multiplier for every rank, exact initial/base damage for single ranks (numbered from 1, as in
/// game), and cooldown, energy cost and cast time for every rank. PvP ranks get the same changes. The client keeps its
/// own copy, so skill tooltips still show the original numbers.
/// </summary>
public static class SkillOverrides
{
    /// <summary>Applies the file to <paramref name="balance"/>. Returns the number of skills changed.</summary>
    public static int Apply(Balance balance, string? path)
    {
        if (path is null || !File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var changed = 0;
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Name.StartsWith('_') || !int.TryParse(entry.Name, out var id) || !balance.Skills.TryGetValue(id, out var skill)) continue;
            var o = entry.Value;
            balance.ReplaceSkill(skill with { Levels = Change(skill.Levels, o), PvpLevels = Change(skill.PvpLevels, o) });
            if (skill.StatusEffectId > 0 && balance.StatusEffects.TryGetValue(skill.StatusEffectId, out var effect) && HasDotFields(o))
                balance.ReplaceStatusEffect(ChangeEffect(effect, o));
            changed++;
        }
        return changed;
    }

    private static bool HasDotFields(JsonElement o) =>
        o.TryGetProperty("dotMultiplier", out _) || o.TryGetProperty("dotRanks", out _) || o.TryGetProperty("dotDuration", out _) || o.TryGetProperty("dotTick", out _);

    /// <summary>
    /// The skill's status effect (Rupture's bleed, a poison…): dotMultiplier scales the per-tick amount at every rank,
    /// dotRanks sets amount and base for single ranks (from 1), dotDuration and dotTick set every rank's length and the
    /// seconds between ticks. Per tick = ceil(base + amount × (1 + √(ability ÷ 100) + √(stat ÷ divisor))).
    /// </summary>
    private static StatusEffectInfo ChangeEffect(StatusEffectInfo effect, JsonElement o)
    {
        float? Float(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
        var multiplier = Float(o, "dotMultiplier") ?? 1f;
        var ranks = o.TryGetProperty("dotRanks", out var r) && r.ValueKind == JsonValueKind.Object
            ? r.EnumerateObject().Where(x => int.TryParse(x.Name, out _)).ToDictionary(x => int.Parse(x.Name) - 1, x => x.Value)
            : [];
        var levels = effect.Levels.Select((level, i) =>
        {
            var l = level with { Amount = level.Amount * multiplier, BaseAmount = level.BaseAmount * multiplier, Duration = Float(o, "dotDuration") ?? level.Duration };
            if (ranks.TryGetValue(i, out var exact))
                l = l with { Amount = Float(exact, "amount") ?? l.Amount, BaseAmount = Float(exact, "base") ?? l.BaseAmount };
            return l;
        }).ToList();
        return effect with { Levels = levels, TickSeconds = Float(o, "dotTick") ?? effect.TickSeconds };
    }

    private static List<SkillLevelInfo> Change(IReadOnlyList<SkillLevelInfo> levels, JsonElement o)
    {
        float? Float(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
        var multiplier = Float(o, "damageMultiplier") ?? 1f;
        var ranks = o.TryGetProperty("ranks", out var r) && r.ValueKind == JsonValueKind.Object
            ? r.EnumerateObject().Where(x => int.TryParse(x.Name, out _)).ToDictionary(x => int.Parse(x.Name) - 1, x => x.Value)
            : [];
        return levels.Select((level, i) =>
        {
            var l = level with
            {
                InitialDamage = level.InitialDamage * multiplier,
                BaseDamage = level.BaseDamage * multiplier,
                RechargeTime = Float(o, "cooldown") ?? level.RechargeTime,
                EnergyCost = (int?)Float(o, "energy") ?? level.EnergyCost,
                CastingTime = Float(o, "castTime") ?? level.CastingTime,
            };
            if (ranks.TryGetValue(i, out var exact))
                l = l with { InitialDamage = Float(exact, "initial") ?? l.InitialDamage, BaseDamage = Float(exact, "base") ?? l.BaseDamage };
            return l;
        }).ToList();
    }
}
