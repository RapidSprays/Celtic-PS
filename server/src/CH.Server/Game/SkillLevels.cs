namespace CH.Server.Game;

/// <summary>
/// Owner's rule: no rank caps on player skills, so skill points are the only limit. A player skill (one with an ability
/// and more than one rank) gets its level table extended to <see cref="Ranks"/> ranks, and every rank's minimum
/// character level is cleared. The client mod does the same to its own templates (CHPrivateMods.SkillRanks, patch
/// P11), because the client indexes Levels[rank] without a bounds check. Both sides must use this exact rule.
/// </summary>
public static class SkillLevels
{
    /// <summary>250 ranks covers every skill point a level-250 character has, plus gear rank bonuses (up to +10).</summary>
    public const int Ranks = 260;

    public static bool IsPlayerSkill(int abilityId, int levelCount) => abilityId >= 0 && levelCount > 1;

    /// <summary>
    /// New ranks copy the last real one; initial and base damage keep growing by the template's own last step (heals
    /// keep growing too, as negative damage). Energy cost, cast time and recharge stay at the last rank's values.
    /// </summary>
    public static List<SkillLevelInfo> Extend(IReadOnlyList<SkillLevelInfo> levels)
    {
        var result = levels.Select(l => l with { MinLevel = 0 }).ToList();
        if (result.Count < 2) return result;
        var last = result[^1];
        var prev = result[^2];
        var (initialStep, baseStep) = (last.InitialDamage - prev.InitialDamage, last.BaseDamage - prev.BaseDamage);
        for (var rank = result.Count; rank < Ranks; rank++)
        {
            var n = rank - (levels.Count - 1);
            result.Add(last with { TemplateLevel = rank, InitialDamage = last.InitialDamage + initialStep * n, BaseDamage = last.BaseDamage + baseStep * n });
        }
        return result;
    }
}
