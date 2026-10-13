namespace CH.Server.Game;

/// <summary>
/// PvP rank (the client's "ranking", 0-29: ranking titles by class) and rating (0-12: Pathetic … Invincible). The
/// official formulas and PvpRatingMaxValues are server-only, so these are <b>our own</b>: rank comes from PvP
/// experience earned by kills, rating from the kill/death record.
/// </summary>
public static class PvpRanks
{
    public const int MaxRank = 29, MaxRating = 12;

    public static int XpToNext(int rank) => (int)(100 * Math.Pow(rank + 1, 1.3));

    /// <summary>Experience for a kill: more for victims above you and for higher-ranked victims.</summary>
    public static int KillXp(int killerLevel, int victimLevel, int victimRank) =>
        20 + 4 * Math.Clamp(victimLevel - killerLevel + 5, 0, 20) + 5 * victimRank;

    public static (int Rank, int Xp) Add(int rank, int xp, int gained)
    {
        xp += gained;
        while (rank < MaxRank && xp >= XpToNext(rank))
        {
            xp -= XpToNext(rank);
            rank++;
        }
        return (rank, rank >= MaxRank ? 0 : xp);
    }

    /// <summary>4 ("Average") at an even record, ±4 per doubling of kills over deaths (smoothed by 5 each).</summary>
    public static int Rating(long kills, long deaths) =>
        Math.Clamp((int)Math.Round(4 + 4 * Math.Log2((kills + 5.0) / (deaths + 5.0))), 0, MaxRating);

    /// <summary>Progress to the next rank as the client's stat 31 wants it: percent × 100.</summary>
    public static int Progress(int rank, int xp) => rank >= MaxRank ? 0 : Math.Clamp(xp * 10000 / XpToNext(rank), 0, 10000);
}
