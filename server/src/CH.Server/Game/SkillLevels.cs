namespace CH.Server.Game;

/// <summary>
/// Skill ranks. Training stops at <see cref="TrainableRanks"/> (the owner's rule, 2026-10-14: at most 50 skill points in
/// one skill). Gear rank bonuses (Exalted Dragonlord armour +8, Ring of Sharp Shot +15…) go on top, so a player skill
/// (one with an ability and more than one rank) has its level table extended to <see cref="Ranks"/> ranks, and every
/// rank's minimum character level is cleared. The client mod does the same to its own templates (CHPrivateMods.SkillRanks,
/// patch P11), because the client indexes Levels[rank] without a bounds check. Both sides must use this exact rule.
/// </summary>
public static class SkillLevels
{
    /// <summary>Table length: room for a fully trained skill plus every gear rank bonus.</summary>
    public const int Ranks = 260;

    /// <summary>The highest rank skill points can buy (rank 50 is index 49).</summary>
    public const int TrainableRanks = 50;

    public static bool IsPlayerSkill(int abilityId, int levelCount) => abilityId >= 0 && levelCount > 1;

    /// <summary>
    /// New ranks copy the last real one; initial and base damage keep growing by the template's own last step (heals
    /// keep growing too, as negative damage). Energy cost, cast time and recharge stay at the last rank's values.
    /// </summary>
    /// <summary>A skill's status effect, extended the same way: amount, base amount and duration keep their last step.</summary>
    public static List<StatusEffectLevel> ExtendEffect(IReadOnlyList<StatusEffectLevel> levels)
    {
        var result = levels.ToList();
        var (last, prev) = (levels[^1], levels[^2]);
        for (var n = 1; result.Count < Ranks; n++)
            result.Add(new StatusEffectLevel(last.Amount + (last.Amount - prev.Amount) * n, last.BaseAmount + (last.BaseAmount - prev.BaseAmount) * n,
                last.Duration + (last.Duration - prev.Duration) * n));
        return result;
    }

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
