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

    [Fact]
    public void Levels_take_the_live_games_xp()
    {
        Progression.LoadXpTable(Path.Combine(AppContext.BaseDirectory, "content", "xp-levels.json"));
        Assert.Equal(433, Progression.XpToNextLevel(1));
        Assert.Equal(8_820_673, Progression.XpToNextLevel(200));
        Assert.Equal(25_880_828, Progression.XpToNextLevel(230));
        // The Grimoire author's formula gives the same table.
        Assert.All(Enumerable.Range(1, 250), level => Assert.Equal(Progression.XpToNextLevel(level), Progression.OfficialXp(level)));
    }

    [Fact]
    public void Class_kits_load_from_the_content_file_with_the_same_lists()
    {
        var kits = ClassKit.Read(Path.Combine(AppContext.BaseDirectory, "content", "class-kits.json"))!;
        // Owner's warrior kit (2026-10-15): the starter skills plus Shatter, Rupture, Defensive Formation, Double Attack, Shield Bash, Frenzy, Taunt, Warcry.
        Assert.Equal([1, 47, 2, 3, 215, 214, 227, 212, 46, 45, 33, 128], kits[CharacterClass.Warrior].Skills);
        Assert.Contains(14, kits[CharacterClass.Warrior].Abilities); // First Aid, for every class
        Assert.Equal([11, 42, 43, 55, 40, 41, 226, 225, 133, 222, 139, 140, 137, 126, 221], kits[CharacterClass.Mage].Skills);
        Assert.Equal([53, 18, 16, 17, 49, 192, 127, 220, 219, 212], kits[CharacterClass.Rogue].Skills);
        Assert.Equal([13, 52, 14, 3, 218, 252, 132, 130, 211], kits[CharacterClass.Ranger].Skills);
        Assert.Equal([38, 8, 5, 3, 134, 224, 39, 37, 124, 10, 148, 149, 150, 151, 152, 153, 36, 223], kits[CharacterClass.Druid].Skills);
        foreach (var (cls, kit) in ClassKit.Defaults.Where(k => k.Key is not (CharacterClass.Warrior or CharacterClass.Mage or CharacterClass.Rogue or CharacterClass.Ranger or CharacterClass.Druid)))
        {
            Assert.Equal(kit.Skills, kits[cls].Skills);
            Assert.Equal(kit.Abilities.Order(), kits[cls].Abilities.Order());
            Assert.Empty(kits[cls].StartingLevels);
        }
    }

    [Fact]
    public void Kill_xp_rises_for_monsters_above_you_and_for_stars()
    {
        var bonus = XpBonus.Load(Path.Combine(AppContext.BaseDirectory, "content", "xp-bonus.json"));
        Assert.Equal(1.0, bonus.Multiplier(10, 10, 1), 6);   // same level, 1 star
        Assert.Equal(1.10, bonus.Multiplier(4, 5, 1), 6);    // 1 level above
        Assert.Equal(1.80, bonus.Multiplier(4, 9, 1), 6);    // 5 above
        Assert.Equal(1.80, bonus.Multiplier(4, 20, 1), 6);   // more than 5 above: the 5-level bonus
        Assert.Equal(1.0, bonus.Multiplier(20, 10, 1), 6);   // below you: no bonus (the 25% a level cut is separate)
        Assert.Equal(1.25 * 1.75, bonus.Multiplier(10, 12, 4), 6); // 2 above, 4 stars
        Assert.Equal(2.25, bonus.Multiplier(10, 10, 6), 6);
        Assert.Equal(1.0, bonus.Multiplier(10, 10, 0), 6);   // power 0 counts as 1 star
    }
}
