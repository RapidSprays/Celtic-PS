using CH.Server.Game;

namespace CH.Tests;

/// <summary>Approximated monster drops against the real client data (skips without it).</summary>
public sealed class LootTests
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    [Fact]
    public void Drops_are_tradeable_gear_near_the_monsters_level_and_bosses_always_drop()
    {
        if (!File.Exists(BalancePath)) return;
        var b = Balance.Load(BalancePath);
        var loot = new LootTable(b);
        Assert.True(loot.PoolSize > 3000, $"pool {loot.PoolSize}");

        var rng = new Random(11);
        var drops = Enumerable.Range(0, 400).SelectMany(_ => loot.Roll(40, 1, CharacterClass.Rogue, rng)).ToList();
        Assert.InRange(drops.Count, 20, 70); // ~10% for one-star monsters
        Assert.All(drops, id =>
        {
            var item = b.Items[id];
            Assert.True(item.Tradeable && item.StarLevel < 0);
            Assert.InRange(item.LevelRequirement, 34, 41);
        });
        var rogueUsable = drops.Count(id => b.Items[id].Classes.Count == 0 || b.Items[id].Classes.Contains("Rogue"));
        Assert.True(rogueUsable > drops.Count / 2, $"{rogueUsable}/{drops.Count} usable by the killer's class");

        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal(2, loot.Roll(100, 5, CharacterClass.Mage, rng).Count));
        Assert.NotEmpty(loot.Roll(1, 4, CharacterClass.Warrior, rng)); // level 1 boss still finds something
    }
}
