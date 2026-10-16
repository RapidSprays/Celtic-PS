namespace CH.Server.Game;

/// <summary>Deals a kill's drops out to a group like cards (owner's rule, 2026-10-16).</summary>
public static class LootDeal
{
    /// <summary>
    /// The group in a random order, the drops shuffled, then dealt round one at a time: everyone gets one before anyone
    /// gets a second, and the extras land on random members.
    /// </summary>
    public static List<(TMember Owner, TDrop Drop)> Deal<TMember, TDrop>(IReadOnlyList<TMember> group, IReadOnlyList<TDrop> drops, Random rng)
    {
        if (group.Count == 0) return [];
        var order = group.OrderBy(_ => rng.Next()).ToList();
        return drops.OrderBy(_ => rng.Next()).Select((d, i) => (order[i % order.Count], d)).ToList();
    }
}
