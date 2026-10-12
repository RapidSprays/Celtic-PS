using CH.Server.Game;

namespace CH.Tests;

/// <summary>Zone population and travel against the real client data (skips without it).</summary>
public sealed class ZoneTests
{
    private static readonly string Extracted = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510");
    private static readonly string BalancePath = Path.Combine(Extracted, "balance-client.json");
    private static readonly string PopulationPath = Path.Combine(Extracted, "zone-population.json");

    private static readonly Lazy<Balance> Data = new(() => Balance.Load(BalancePath));

    [Fact]
    public void Every_combat_zone_gets_monsters_inside_its_bounds_with_unique_ids()
    {
        if (!File.Exists(BalancePath) || !File.Exists(PopulationPath)) return;
        var b = Data.Value;
        var generated = ZonePopulation.LoadGenerated(PopulationPath);
        int[] combatZones = [3, 4, 5, 6, 7, 10, 11, 12, 13, 15, 16, 17, 18, 19, 20, 22, 24, 27];
        Assert.All(combatZones, z => Assert.True(generated.ContainsKey(z), $"zone {z} has generated camps"));

        foreach (var zoneId in combatZones.Append(2))
        {
            var zone = b.Zones[zoneId];
            var mobs = ZonePopulation.Build(zoneId, b, 0, generated);
            Assert.True(mobs.Count >= 10, $"zone {zoneId} has {mobs.Count} monsters");
            Assert.Equal(mobs.Count, mobs.Select(m => m.ServerId).Distinct().Count());
            Assert.All(mobs, m =>
            {
                Assert.InRange(m.HomeX, zone.Min.X, zone.Max.X);
                Assert.InRange(m.HomeZ, zone.Min.Z, zone.Max.Z);
                Assert.True(m.Level >= 1);
                Assert.True(m.ServerId < ZonePopulation.FirstAutoServerId + (uint)(zoneId + 1) * 1000, "ids stay in the zone's block");
            });
        }

        // Lir's Reach keeps its hand-made camps; hubs stay peaceful.
        Assert.Equal(ZonePopulation.Build(2, b, 0).Count, ZonePopulation.Build(2, b, 0, generated).Count);
        Assert.Empty(ZonePopulation.Build(93, b, 0, generated));
        Assert.Empty(ZonePopulation.Build(9, b, 0, generated));

        // Deeper zones are higher level.
        double Avg(int z) => ZonePopulation.Build(z, b, 0, generated).Average(m => m.Level);
        Assert.True(Avg(4) < Avg(3) && Avg(3) < Avg(10) && Avg(10) < Avg(24));
    }
}
