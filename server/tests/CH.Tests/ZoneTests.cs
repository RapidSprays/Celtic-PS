using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using CH.Server.Game;
using Microsoft.Extensions.Logging.Abstractions;

namespace CH.Tests;

/// <summary>Zone population and travel against the real client data (skips without it).</summary>
public sealed class ZoneTests : IDisposable
{
    private static readonly string ZonePointsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "zone-points.json");

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-zone-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

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

    [Fact]
    public void Every_live_zone_is_reachable_from_Lirs_Reach_and_arrivals_land_outside_the_return_portal()
    {
        if (!File.Exists(BalancePath) || !File.Exists(ZonePointsPath)) return;
        var b = Data.Value;
        var travel = ZoneTravel.Load(ZonePointsPath, b);
        Assert.True(travel.ExitCount >= 60);

        var seen = new HashSet<int> { 2 };
        var queue = new Queue<int>([2]);
        while (queue.TryDequeue(out var zone))
        {
            foreach (var exit in travel.ExitsOf(zone).Where(e => e.Y > -500))
            {
                var to = travel.ArrivalFor(exit);
                Assert.Equal(exit.DestZone, to.Zone);
                var info = b.Zones[to.Zone];
                Assert.InRange(to.X, info.Min.X, info.Max.X);
                Assert.InRange(to.Z, info.Min.Z, info.Max.Z);
                // Not inside any exit trigger of the destination, so the "travel back?" prompt doesn't pop up.
                Assert.All(travel.ExitsOf(to.Zone), e =>
                    Assert.True(MathF.Sqrt((e.X - to.X) * (e.X - to.X) + (e.Z - to.Z) * (e.Z - to.Z)) > e.Radius,
                        $"arrival from {zone}#{exit.Id} lands inside {to.Zone}#{e.Id}"));
                if (seen.Add(exit.DestZone)) queue.Enqueue(exit.DestZone);
            }
        }
        var live = b.Zones.Values.Where(z => z.Accessibility == "L").Select(z => z.Id);
        Assert.Empty(live.Except(seen));
        Assert.Contains(8, seen);  // Global Arena, via Farcrag Castle
        Assert.Contains(9, seen);  // Tavern
        Assert.Contains(269, travel.GateQuests); // Stonevale → Otherworld
    }

    [Fact]
    public async Task Walking_through_an_exit_goto_and_leystone_fast_travel()
    {
        if (!File.Exists(BalancePath) || !File.Exists(ZonePointsPath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        uint characterId;
        await using (var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance))
        using (var client = new CH.FakeClient.FakeClient())
        {
            client.Connect("127.0.0.1", host.Game.Port);
            client.VersionCheck();
            client.Login("traveller");
            characterId = client.CreateCharacter("Siobhan").CharacterId;
            var start = client.StartGame(characterId, "Siobhan");
            Assert.Empty(start.Teleports);
            Assert.Contains(269, start.CompletedQuests);
            client.FinishZoning();

            // Starting on Highshore Village's leystone discovers it.
            Assert.Equal(2003, client.Expect(Opcode.PlayerSpawnPointDiscovered).ReadVariableInt32());

            // Walk to Lir's Reach exit #11 (Crookback Hollow) and confirm the prompt.
            client.Move(-181f, 9.4f, -226f, 0);
            client.Send(Opcode.CharacterZoningRequest, new ZoningRequest(-181f, 9.4f, -226f, 11).Write);
            var moved = CorrectPlayerPosition.Read(client.Expect(Opcode.CorrectPlayerPosition));
            Assert.Equal(4u, moved.Zone);
            Assert.Equal((int)characterId, moved.CharacterId);
            // Just outside Crookback Hollow's way back (#0 at 26, 119, radius 3), towards North Passage.
            var gap = MathF.Sqrt((moved.X - 26.01f) * (moved.X - 26.01f) + (moved.Z - 119.33f) * (moved.Z - 119.33f));
            Assert.InRange(gap, 3.5f, 13f);

            // Nothing for the new zone until the client has loaded it; then the full snapshot.
            client.Send(Opcode.FinishedZoning, _ => { });
            client.Expect(Opcode.EnteringNewZoneUpdate);
            var mobs = client.Expect(Opcode.BatchZoneMonsterUpdate).ReadVariableUInt32();
            Assert.True(mobs >= 10, $"{mobs} monsters in Crookback Hollow");
            client.Expect(Opcode.AllZoningMessagesSent);
            client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", ".where").WriteFromClient);
            Assert.StartsWith("Zone 4 (Crookback Hollow)", client.Expect(Opcode.SimpleMessageForThePlayer).ReadString());

            // An exit id from another zone is ignored.
            client.Send(Opcode.CharacterZoningRequest, new ZoningRequest(moved.X, moved.Y, moved.Z, 12).Write);
            Assert.Throws<TimeoutException>(() => client.Expect(Opcode.CorrectPlayerPosition, TimeSpan.FromSeconds(1)));

            // .goto by name lands on a leystone.
            client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", ".goto stonevale northern woods").WriteFromClient);
            var gone = CorrectPlayerPosition.Read(client.Expect(Opcode.CorrectPlayerPosition));
            Assert.Equal((3u, 183.58f, 163.69f), (gone.Zone, gone.X, gone.Z));
            client.FinishZoning();
            Assert.Equal(3004, client.Expect(Opcode.PlayerSpawnPointDiscovered).ReadVariableInt32());

            // Fast travel to an undiscovered leystone is refused, but still answered so the loading screen closes.
            client.Send(Opcode.TeleportRequest, new TeleportRequest(3, 3001).Write);
            var refused = CorrectPlayerPosition.Read(client.Expect(Opcode.CorrectPlayerPosition));
            Assert.Equal((3u, 183.58f, 163.69f), (refused.Zone, refused.X, refused.Z));

            // To a discovered one in another zone: a full zone change.
            client.Send(Opcode.TeleportRequest, new TeleportRequest(2, 2003).Write);
            var home = CorrectPlayerPosition.Read(client.Expect(Opcode.CorrectPlayerPosition));
            Assert.Equal((2u, -109.51f, -16.86f), (home.Zone, home.X, home.Z));
            client.FinishZoning();
            client.Flush();
            await Task.Delay(200);
            client.Disconnect();
        }

        // Discovered leystones and the new position survive a restart.
        await using (var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance))
        using (var client = new CH.FakeClient.FakeClient())
        {
            client.Connect("127.0.0.1", host.Game.Port);
            client.VersionCheck();
            client.Login("traveller");
            var start = client.StartGame(characterId, "Siobhan");
            Assert.Equal(2, start.Character.Zone);
            Assert.Equal([2003, 3004, 4002], start.Teleports); // North Passage is next to where Crookback Hollow is entered
        }
    }
}
