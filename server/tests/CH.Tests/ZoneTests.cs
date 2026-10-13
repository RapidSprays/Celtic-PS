using CH.Protocol;
using CH.Protocol.Messages;
using Lidgren.Network;
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

    private static readonly string WorldPath = Path.Combine(Extracted, "world.json");

    private static List<int> AvailableQuests(Lidgren.Network.NetIncomingMessage m)
    {
        var list = new List<int>();
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) list.Add(m.ReadVariableInt32());
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) m.ReadVariableInt32();
        return list;
    }

    [Fact]
    public async Task Mangy_Mutts_kills_count_report_back_and_the_next_quest_opens()
    {
        if (!File.Exists(BalancePath) || !File.Exists(WorldPath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        await using var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("questor");
        var id = client.CreateCharacter("Muirne").CharacterId;
        client.StartGame(id, "Muirne");
        client.Send(Opcode.FinishedZoning, _ => { });
        var entering = client.Expect(Opcode.EnteringNewZoneUpdate);
        for (var i = 0; i < 4; i++) entering.ReadFloat();
        var available = AvailableQuests(entering);
        Assert.Contains(2, available);       // Mangy Mutts
        Assert.DoesNotContain(3, available); // Beastly Behaviour needs Mangy Mutts first
        // Lir's Reach's quest givers stand in the world: Private Duncan's conversation is offered.
        client.Expect(Opcode.AllZoningMessagesSent);

        client.Send(Opcode.QuestStart, b => b.WriteVariableInt32(2));
        var started = client.Expect(Opcode.QuestStartReply);
        Assert.Equal(1, started.ReadByte());
        Assert.Equal((2, 2), (started.ReadVariableInt32(), started.ReadVariableInt32()));
        started.ReadBoolean();
        Assert.Equal((0, 1, 0, 0), (started.ReadByte(), started.ReadByte(), started.ReadByte(), started.ReadByte())); // stage 0 active

        // Three Direhounds (70000): "2 remaining", "1 remaining", then stage 0 done and stage 1 (report back) opens.
        client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", ".level 10").WriteFromClient);
        client.Expect(Opcode.LevelUp);
        for (var kill = 0; kill < 3; kill++)
        {
            client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", ".spawn Direhound 1").WriteFromClient);
            var hound = client.Expect(Opcode.ZoneMonsterAppeared).ReadVariableUInt32();
            client.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.Mob, NetTime.Now, hound).Write);
            var progress = client.Expect(Opcode.QuestStageProgressReply, TimeSpan.FromSeconds(20));
            Assert.Equal(1, progress.ReadByte());
            var mode = progress.ReadVariableInt32();
            Assert.Equal((2, 0), (progress.ReadVariableInt32(), progress.ReadVariableInt32()));
            if (kill < 2)
            {
                Assert.Equal(0, mode);
                Assert.Equal(0, progress.ReadVariableInt32()); // MobKill
                progress.ReadVariableInt32();
                Assert.Equal(2 - kill, progress.ReadVariableInt32());
            }
            else
            {
                Assert.Equal(1, mode);
                Assert.Equal((1, 1), (progress.ReadVariableInt32(), progress.ReadVariableInt32()));
            }
        }

        // Duncan's STAGE_COMPLETE 2 1: the quest completes with rewards, and Beastly Behaviour opens.
        client.Send(Opcode.QuestStageComplete, b => { b.WriteVariableInt32(2); b.WriteVariableInt32(1); });
        client.Expect(Opcode.QuestStageProgressReply);
        var done = client.Expect(Opcode.QuestCompleteReply);
        Assert.Equal((1, 2), (done.ReadByte(), done.ReadVariableInt32()));
        Assert.True(done.ReadVariableInt32() > 0, "xp reward");
        done.ReadFloat();
        Assert.True(done.ReadVariableInt32() > 0, "coin reward");
        done.ReadVariableInt32();
        done.ReadVariableInt32();
        for (int i = 0, n = done.ReadVariableInt32(); i < n; i++) { done.ReadVariableInt32(); done.ReadVariableInt32(); }
        done.ReadByte();
        InventoryWithMoney.Read(done);
        Assert.Contains(3, AvailableQuests(done));

        // After a restart the quest is in the completed list.
        client.Flush();
        await Task.Delay(200);
        client.Disconnect();
        await host.DisposeAsync();
        await using var host2 = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var again = new CH.FakeClient.FakeClient();
        again.Connect("127.0.0.1", host2.Game.Port);
        again.VersionCheck();
        again.Login("questor");
        Assert.Contains(2, again.StartGame(id, "Muirne").CompletedQuests);
    }

    [Fact]
    public async Task A_lirs_reach_merchant_sells_and_buys_back()
    {
        if (!File.Exists(BalancePath) || !File.Exists(WorldPath)) return;
        // A shop whose seller stands in Lir's Reach.
        using var world = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(WorldPath));
        var npcs = world.RootElement.GetProperty("npcs").EnumerateArray()
            .Where(n => n.GetProperty("zone").GetInt32() == 2)
            .GroupBy(n => n.GetProperty("conversation").GetInt32())
            .ToDictionary(g => g.Key, g => g.First().GetProperty("template").GetInt32());
        var shop = world.RootElement.GetProperty("shops").EnumerateObject()
            .First(sh => npcs.ContainsKey(sh.Value.GetProperty("conversation").GetInt32()) && sh.Value.GetProperty("category").GetString() != "stamps");
        var shopId = int.Parse(shop.Name);
        var npcTemplate = npcs[shop.Value.GetProperty("conversation").GetInt32()];

        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        await using var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("shopper");
        var id = client.CreateCharacter("Ethna").CharacterId;
        var start = client.StartGame(id, "Ethna");
        client.FinishZoning();

        (int Coins, List<(int Template, int Buy, int Sell)> Prices, List<int> Stock, List<ItemSlot> Items) Reply()
        {
            var m = client.Expect(Opcode.RequestShopReply);
            Assert.Equal(1, m.ReadByte());
            if (m.ReadByte() == 1) { m.ReadVariableInt32(); m.ReadVariableInt32(); }
            var inv = InventoryWithMoney.Read(m);
            var stock = new List<int>();
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) { m.ReadVariableInt32(); stock.Add(m.ReadVariableInt32()); m.ReadVariableInt32(); }
            var prices = new List<(int, int, int)>();
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) prices.Add((m.ReadVariableInt32(), m.ReadVariableInt32(), m.ReadVariableInt32()));
            return (inv.Coins, prices, stock, inv.Items.ToList());
        }

        client.Send(Opcode.RequestShop, b => { b.WriteVariableInt32(npcTemplate); b.WriteVariableInt32(shopId); b.WriteVariableInt32(0); });
        var opened = Reply();
        Assert.NotEmpty(opened.Stock);
        Assert.Equal(start.Coins, opened.Coins);
        var cheapest = opened.Prices.Where(p => p.Buy > 0).MinBy(p => p.Buy);
        Assert.True(cheapest.Buy <= start.Coins, $"cheapest item costs {cheapest.Buy}");

        client.Send(Opcode.PurchaseItem, b =>
        {
            b.WriteVariableInt32(npcTemplate); b.WriteVariableInt32(shopId); b.WriteVariableInt32(cheapest.Template); b.WriteVariableInt32(1);
            b.WriteVariableInt32(0);
        });
        var bought = Reply();
        Assert.Equal(start.Coins - cheapest.Buy, bought.Coins);
        var item = bought.Items.Last(i => i.TemplateId == cheapest.Template);

        client.Send(Opcode.SellItem, b =>
        {
            b.WriteVariableInt32(npcTemplate); b.WriteVariableInt32(shopId); b.WriteVariableInt32(item.TemplateId);
            b.WriteVariableInt32(item.ServerInventoryId); b.WriteVariableInt32(1); b.WriteVariableInt32(0);
        });
        var sold = Reply();
        Assert.Equal(bought.Coins + cheapest.Sell, sold.Coins);
        Assert.True(cheapest.Sell < cheapest.Buy);
    }

    [Fact]
    public async Task Glamora_in_Highshore_sells_sigil_crates_for_100k()
    {
        if (!File.Exists(BalancePath) || !File.Exists(WorldPath)) return;
        using var world = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(WorldPath));
        var glamora = world.RootElement.GetProperty("npcs").EnumerateArray()
            .First(n => n.GetProperty("conversation").GetInt32() == 528).GetProperty("template").GetInt32();
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        await using var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("crates");
        var id = client.CreateCharacter("Niamh", @class: 3).CharacterId; // a Mage: Glamora's Mage manuals shop
        client.StartGame(id, "Niamh");
        client.FinishZoning();
        client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", ".gold 150000").WriteFromClient);
        var coins = InventoryWithMoney.Read(client.Expect(Opcode.InventoryUpdate)).Coins;

        foreach (var shopId in new[] { 5, 1031, 1036, 1041, 1046, 1051, 1056 })
        {
            client.Send(Opcode.RequestShop, b => { b.WriteVariableInt32(glamora); b.WriteVariableInt32(shopId); b.WriteVariableInt32(0); });
            var m = client.Expect(Opcode.RequestShopReply);
            m.ReadByte();
            if (m.ReadByte() == 1) { m.ReadVariableInt32(); m.ReadVariableInt32(); }
            InventoryWithMoney.Read(m);
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) { m.ReadVariableInt32(); m.ReadVariableInt32(); m.ReadVariableInt32(); }
            var prices = new Dictionary<int, int>();
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) { var t = m.ReadVariableInt32(); prices[t] = m.ReadVariableInt32(); m.ReadVariableInt32(); }
            Assert.Equal(100_000, prices[34781]);
            Assert.Equal(100_000, prices[34782]);
        }

        // Buying one costs exactly 100,000; a second can't be afforded.
        void Buy(int template) => client.Send(Opcode.PurchaseItem, b =>
        {
            b.WriteVariableInt32(glamora); b.WriteVariableInt32(1041); b.WriteVariableInt32(template); b.WriteVariableInt32(1); b.WriteVariableInt32(0);
        });
        Buy(34782);
        var bought = client.Expect(Opcode.RequestShopReply);
        bought.ReadByte(); bought.ReadByte();
        var after = InventoryWithMoney.Read(bought);
        Assert.Equal(coins - 100_000, after.Coins);
        Assert.Contains(after.Items, i => i.TemplateId == 34782);
        Buy(34781);
        Assert.Contains("enough gold", client.Expect(Opcode.SimpleMessageForThePlayer).ReadString());
    }

    [Fact]
    public async Task The_bank_stores_and_returns_items()
    {
        if (!File.Exists(BalancePath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        await using var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("saver");
        var id = client.CreateCharacter("Aodh").CharacterId;
        var start = client.StartGame(id, "Aodh");
        client.FinishZoning();
        var potions = start.Inventory.First(i => i.TemplateId == CH.Server.Game.ClassKit.MinorHealthPotion);

        (List<ItemSlot> Backpack, List<ItemSlot> Bank) Reply()
        {
            var m = client.Expect(Opcode.BankingMessageReply);
            Assert.Equal(1, m.ReadByte());
            List<ItemSlot> Read() => Enumerable.Range(0, m.ReadVariableInt32()).Select(_ => ItemSlot.Read(m)).ToList();
            return (Read(), Read());
        }

        client.Send(Opcode.BankingMessage, b => b.Write((byte)0));
        Assert.Empty(Reply().Bank);

        client.Send(Opcode.BankingMessage, b =>
        {
            b.Write((byte)1); b.Write((byte)1);
            b.WriteVariableInt32(potions.ServerInventoryId); b.WriteVariableInt32(potions.TemplateId); b.WriteVariableInt32(2);
        });
        var deposited = Reply();
        Assert.Equal(potions.Quantity - 2, deposited.Backpack.Single(i => i.TemplateId == potions.TemplateId).Quantity);
        var banked = deposited.Bank.Single();
        Assert.Equal((potions.TemplateId, 2), (banked.TemplateId, banked.Quantity));

        client.Send(Opcode.BankingMessage, b =>
        {
            b.Write((byte)1); b.Write((byte)0);
            b.WriteVariableInt32(banked.ServerInventoryId); b.WriteVariableInt32(banked.TemplateId); b.WriteVariableInt32(1);
        });
        var withdrawn = Reply();
        Assert.Equal(potions.Quantity - 1, withdrawn.Backpack.Single(i => i.TemplateId == potions.TemplateId).Quantity);
        Assert.Equal(1, withdrawn.Bank.Single().Quantity);
    }

    [Fact]
    public async Task A_trainer_teaches_an_ability_once_for_its_price()
    {
        if (!File.Exists(BalancePath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        await using var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("learner");
        var id = client.CreateCharacter("Cian", @class: 1).CharacterId;
        var start = client.StartGame(id, "Cian");
        client.FinishZoning();
        var ability = Enumerable.Range(1, 60).First(a => !ClassKit.For(1).Abilities.Contains(a));

        client.Send(Opcode.LearnAbility, b => { b.WriteVariableInt32(ability); b.WriteVariableInt32(10); });
        var learnt = client.Expect(Opcode.LearnAbilityReply);
        Assert.Equal((1, ability, start.Coins - 10), (learnt.ReadByte(), learnt.ReadVariableInt32(), learnt.ReadVariableInt32()));

        client.Send(Opcode.LearnAbility, b => { b.WriteVariableInt32(ability); b.WriteVariableInt32(10); });
        var again = client.Expect(Opcode.LearnAbilityReply);
        Assert.Equal((0, "You already know that."), (again.ReadByte(), again.ReadString()));
    }

    [Fact]
    public void Pet_families_and_food_times_come_from_the_item_data()
    {
        if (!File.Exists(BalancePath)) return;
        var b = Data.Value;
        Assert.Equal((5588, 5589), Pets.FamilyOf(b.Items[58665]));   // Tiny Brown Mastiff → dog
        Assert.Equal(600, Pets.FedSeconds(b.Items[58836]));          // "for 10 minutes"
        Assert.Equal(Pets.NewFedEffect, Pets.FedEffectOf(b.Items[58836]));
        Assert.Equal(Pets.WhistleSkill, b.Items[60359].SkillLinkId);
    }

    [Fact]
    public async Task A_pet_arrives_fed_eats_and_the_pet_vendor_trades_tokens()
    {
        if (!File.Exists(BalancePath) || !File.Exists(WorldPath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        await using var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("petowner");
        var id = client.CreateCharacter("Oscar").CharacterId;
        client.StartGame(id, "Oscar");
        client.FinishZoning();
        void Say(string text) => client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);
        List<int> Effects()
        {
            var m = client.Expect(Opcode.StatusEffectUpdate, TimeSpan.FromSeconds(5));
            var ids = new List<int>();
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) { ids.Add(m.ReadVariableInt32()); m.ReadVariableInt32(); m.ReadVariableInt32(); m.ReadFloat(); m.ReadFloat(); }
            return ids;
        }

        // Equipping a mastiff: it arrives fed (Well Fed dog), so the client draws it.
        Say(".wear Tiny Brown Mastiff");
        Assert.Contains(5589, Effects());

        // New Pet Food is eaten (consumed, 3 s recharge) and the generic "New Fed" effect replaces the welcome meal.
        Say(".give 58836 2");
        var food = InventoryWithMoney.Read(client.Expect(Opcode.InventoryUpdate)).Items.First(i => i.TemplateId == 58836);
        client.Send(Opcode.UseItem, new UseItemRequest(food.ServerInventoryId, TargetType.Self, 0).Write);
        var fed = client.Expect(Opcode.UseItemReply);
        Assert.Equal(1, fed.ReadByte());
        Assert.Equal(3f, fed.ReadFloat());
        Assert.Contains(Pets.NewFedEffect, Effects());

        // A Mount Whistle does nothing while the mount is with you.
        Say(".give 60359");
        var whistle = InventoryWithMoney.Read(client.Expect(Opcode.InventoryUpdate)).Items.First(i => i.TemplateId == 60359);
        client.Send(Opcode.UseItem, new UseItemRequest(whistle.ServerInventoryId, TargetType.Self, 0).Write);
        Assert.Equal(0, client.Expect(Opcode.UseItemReply).ReadByte());

        // Pet vendor 26 ("Buy a Hound"): a Tiny Brown Mastiff for a Pet Token.
        Say(".give 56051");
        client.Expect(Opcode.InventoryUpdate);
        client.Send(Opcode.TokenVendorMessage, b => { b.WriteVariableInt32(1); b.WriteVariableInt32(26); });
        var stock = client.Expect(Opcode.TokenVendorMessage);
        Assert.Equal((2, (byte)1, 26), (stock.ReadVariableInt32(), stock.ReadByte(), stock.ReadVariableInt32()));
        Assert.True(stock.ReadVariableInt32() >= 1);
        var stockId = stock.ReadVariableInt32();
        Assert.Equal(58665, stock.ReadVariableInt32());
        client.Send(Opcode.TokenVendorMessage, b => { b.WriteVariableInt32(3); b.WriteVariableInt32(26); b.WriteVariableInt32(stockId); b.WriteVariableInt32(1); });
        var bought = client.Expect(Opcode.TokenVendorMessage);
        Assert.Equal((4, (byte)1), (bought.ReadVariableInt32(), bought.ReadByte()));
        bought.ReadVariableInt32();
        var inventory = InventoryWithMoney.Read(bought);
        Assert.Contains(inventory.Items, i => i.TemplateId == 58665);
        Assert.DoesNotContain(inventory.Items, i => i.TemplateId == 56051);
    }

    [Fact]
    public void Every_usable_item_resolves_to_a_behaviour()
    {
        if (!File.Exists(BalancePath)) return;
        var b = Data.Value;
        var chests = ChestCatalog.Build(b);
        var unresolved = b.Items.Values
            .Where(i => i.Usable && !i.IsPlaceholder && !System.Text.RegularExpressions.Regex.IsMatch(i.Name, "TEST|OVERWRITE|DO NOT USE|UNUSED"))
            .Where(i => chests.For(i.Id) is null && b.SkillTaughtBy(i) is null && b.RecipeTaughtBy(i) is null && !b.Skills.ContainsKey(i.SkillLinkId))
            .Select(i => $"{i.Id} {i.Name} ({i.SkillLink})").ToList();
        Assert.True(unresolved.Count == 0, string.Join("; ", unresolved.Take(30)));
        Assert.NotNull(b.SkillTaughtBy(b.Items.Values.First(i => i.Name == "'Natures Breath' Spell Tome")));
        Assert.Equal("Broth", b.Recipes[b.RecipeTaughtBy(b.Items.Values.First(i => i.Name == "'Broth' Recipe Book"))!.Value]);
    }

    [Fact]
    public async Task Consumables_restore_buff_strike_teach_and_refuse_special_targets()
    {
        if (!File.Exists(BalancePath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        await using var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("consumer");
        var id = client.CreateCharacter("Deirdre").CharacterId;
        client.StartGame(id, "Deirdre");
        client.FinishZoning();
        var b = Data.Value;
        void Say(string text) => client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);
        ItemSlot Give(string name)
        {
            var template = b.Items.Values.First(i => i.Name == name && i.Usable).Id;
            Say($".give {template}");
            return InventoryWithMoney.Read(client.Expect(Opcode.InventoryUpdate)).Items.Last(i => i.TemplateId == template);
        }
        (byte Status, string Reason) Use(ItemSlot item, TargetType type = TargetType.Self, uint target = 0)
        {
            client.Send(Opcode.UseItem, new UseItemRequest(item.ServerInventoryId, type, target).Write);
            var reply = client.Expect(Opcode.UseItemReply);
            var status = reply.ReadByte();
            return (status, status == 0 ? reply.ReadString() : "");
        }

        // Gruel restores energy (its energy cost is −20).
        Assert.Equal(1, Use(Give("Gruel")).Status);
        // Elixir of Attack applies its buff.
        var elixir = Give("Elixir of Attack");
        client.Send(Opcode.UseItem, new UseItemRequest(elixir.ServerInventoryId, TargetType.Self, 0).Write);
        var effects = client.Expect(Opcode.StatusEffectUpdate); // the buff arrives before the use reply
        Assert.True(effects.ReadVariableInt32() >= 1);
        Assert.Equal(1, client.Expect(Opcode.UseItemReply).ReadByte());
        // A Firebolt scroll needs an enemy, then hits it.
        var scroll = Give("Minor Summerspark Scroll");
        Assert.Equal((0, "You need to target an enemy."), Use(scroll));
        Say(".spawn Scrawny Coast Wolf 1");
        var wolf = client.Expect(Opcode.ZoneMonsterAppeared).ReadVariableUInt32();
        Assert.Equal(1, Use(scroll, TargetType.Mob, wolf).Status);
        client.Expect(Opcode.CombatDamageMessage);
        // A recipe book teaches once.
        var book = Give("'Broth' Recipe Book");
        Assert.Equal(1, Use(book).Status);
        Assert.Contains("already know", Use(Give("'Broth' Recipe Book")).Reason);
        // Fireworks are cosmetic: accepted.
        Assert.Equal(1, Use(Give("7th Anniversary Red Firework")).Status);
        // Spirit keys only work on spirit-bound locks: refused, not used up.
        var key = Give("Weeping Key");
        Assert.Equal((0, "That only works on something special."), Use(key, TargetType.Mob, wolf));
    }

    [Fact]
    public async Task Riding_shows_the_mounted_buff_skills_take_many_ranks_and_cooldowns_follow_the_rules()
    {
        if (!File.Exists(BalancePath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        await using var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("rider");
        var id = client.CreateCharacter("Fergus", @class: 1).CharacterId;
        var start = client.StartGame(id, "Fergus");
        client.FinishZoning();
        void Say(string text) => client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);

        // A Tame Brown War Wolf (+10% speed in its description): the Mounted buff at its +10% level, and faster running.
        Say(".wear Tame Brown War Wolf");
        Lidgren.Network.NetIncomingMessage m;
        List<(int Id, int Level)> effects;
        do
        {
            m = client.Expect(Opcode.StatusEffectUpdate, TimeSpan.FromSeconds(5));
            effects = new();
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) { effects.Add((m.ReadVariableInt32(), m.ReadVariableInt32())); m.ReadVariableInt32(); m.ReadFloat(); m.ReadFloat(); }
        } while (!effects.Any(e => e.Id == Pets.MountedEffect));
        Assert.Contains((Pets.MountedEffect, 1), effects); // level index 1 = +10%
        Assert.True(CharacterStats.Read(m).MaxSpeed > 5.4f);

        // Several ranks in one purchase, limited only by the free points (no rank caps).
        Say(".level 12");
        client.Expect(Opcode.LevelUp);
        var skill = start.Skills.First().SkillId;
        client.Send(Opcode.BuySkill, b => { b.WriteVariableInt32(skill); b.WriteVariableInt32(99); });
        var reply = client.Expect(Opcode.BuySkillReply);
        reply.ReadVariableInt32(); // points left
        reply.ReadVariableInt32();
        var skills = Enumerable.Range(0, reply.ReadVariableInt32())
            .Select(_ => new SkillEntry(reply.ReadVariableInt32(), reply.ReadVariableInt32(), reply.ReadVariableInt32(), reply.ReadFloat())).ToList();
        Assert.Equal(11, skills.First(k => k.SkillId == skill).Level); // all 11 points of a level-12 character

        // Consumables recharge in 3 s; elixirs have no cooldown.
        float Recharge(string name)
        {
            var template = Data.Value.Items.Values.First(i => i.Name == name && i.Usable).Id;
            Say($".give {template}");
            var item = InventoryWithMoney.Read(client.Expect(Opcode.InventoryUpdate)).Items.Last(i => i.TemplateId == template);
            client.Send(Opcode.UseItem, new UseItemRequest(item.ServerInventoryId, TargetType.Self, 0).Write);
            var used = client.Expect(Opcode.UseItemReply);
            Assert.Equal(1, used.ReadByte());
            return used.ReadFloat();
        }
        Assert.Equal(3f, Recharge("Gruel"));
        Assert.Equal(0f, Recharge("Elixir of Defence"));
    }

    [Fact]
    public void The_rainbow_trident_is_a_greater_black_trident_under_its_own_id()
    {
        if (!File.Exists(BalancePath)) return;
        var content = Path.Combine(AppContext.BaseDirectory, "content", "custom-items.json");
        var b = Balance.Load(BalancePath, content);
        var trident = b.Items[90001];
        var looks = b.Items[21526];   // Greater Black Trident: slot, spear animations, icon
        var stats = b.Items[72448];   // Accursed Spear of Entropy: level 230 stats
        Assert.Equal("Rainbow Trident", trident.Name);
        Assert.Equal((looks.Slot, looks.Subtype), (trident.Slot, trident.Subtype));
        Assert.Equal((stats.Damage, stats.Speed, stats.LevelRequirement, stats.EquipSkillId, stats.ProcSkillId), (trident.Damage, trident.Speed, trident.LevelRequirement, trident.EquipSkillId, trident.ProcSkillId));
        Assert.Equal(230, trident.LevelRequirement);
        Assert.Equal(90001, trident.ModelId);
        var line = Assert.Single(CH.Patcher.ClientMods.ItemLines());
        Assert.StartsWith("90001|21526|rainbow|Rainbow Trident|", line);
        Assert.EndsWith("|72448|2789|90001|100:RightWeapon:-1.17,0,0.02:1;101:RightWeapon:-1.17,0,0.02:1;104:RightWeapon:-1.17,0,0.02:1;695:RightWeapon:-1.17,0,0.02:1.2", line);
    }
}
