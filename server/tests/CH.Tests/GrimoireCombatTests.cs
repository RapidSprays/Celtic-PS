using CH.Server.Game;

namespace CH.Tests;

/// <summary>Master's Grimoire's combat formulas, checked against numbers measured on live.</summary>
public sealed class GrimoireCombatTests
{
    // Akais' Spawn (101024): level 150, 1,200 pierce and 500 heat resist.
    private static readonly MobCombat Spawn = new(150, 147600, 1495, 300, [(2, 1200)])
    {
        Resists = new Dictionary<int, (int, double)> { [0] = (1200, 1.0), [3] = (500, 1.0), [7] = (0, 0.0) },
    };

    [Fact]
    public void Resists_cut_damage_by_the_grimoire_formula_and_immunity_stops_it()
    {
        // A level 200 attacker: 1 − 1200 / (1200 + 6 + 600) of the pierce gets through.
        Assert.Equal(1472 * (1 - 1200.0 / 1806), Spawn.AfterResist(0, 1472, 200), 6);
        Assert.Equal(0, Spawn.AfterResist(7, 150, 200)); // immune to divine
        Assert.Equal(500, Spawn.AfterResist(6, 500, 200)); // no poison resist listed
    }

    [Fact]
    public void A_lure_lowers_the_flat_resist_but_never_below_zero()
    {
        var plain = Spawn.AfterResist(3, 200, 200);
        var lured = Spawn.AfterResist(3, 200, 200, change: -300); // 500 heat resist → 200
        Assert.Equal(200 * (1 - 200.0 / 806), lured, 6);
        Assert.True(lured > plain);
        Assert.Equal(200, Spawn.AfterResist(3, 200, 200, change: -900)); // can't go below 0: full damage
    }
}
