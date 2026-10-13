using System.Text.Json;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Client = CH.FakeClient.FakeClient;

namespace CH.Tests;

/// <summary>Fishing, ground pickups and cooking.</summary>
public sealed class GatheringTests : IDisposable
{
    private static readonly string Extracted = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510");

    private static readonly string BalancePath = Path.Combine(Extracted, "balance-client.json");

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-gathering-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool HaveData(string file) => File.Exists(BalancePath) && File.Exists(Path.Combine(Extracted, file));

    private Task<ServerHost> Start() => ServerHost.StartAsync(new ServerOptions
    {
        HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath, CookingTimeScale = 0,
    }, NullLoggerFactory.Instance);

    private static (Client Client, uint Id) Join(ServerHost host, string user, string name)
    {
        var client = new Client();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login(user);
        var id = client.CreateCharacter(name).CharacterId;
        client.StartGame(id, name);
        client.FinishZoning();
        return (client, id);
    }

    private static void Say(Client c, string text) => c.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);

    private static JsonElement FirstPoint(string file, string list)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Extracted, file)));
        var zone = doc.RootElement.GetProperty("zones").GetProperty("2");
        return (list == "" ? zone : zone.GetProperty(list))[0].Clone();
    }

    [Fact]
    public async Task A_rod_lands_a_fish_for_fishing_xp()
    {
        if (!HaveData("fishing.json")) return;
        await using var host = await Start();
        var (c, id) = Join(host, "angler", "Muirin");
        using var _c = c;
        var spot = FirstPoint("fishing.json", "spots");
        c.Move(spot.GetProperty("x").GetSingle() + 2, spot.GetProperty("y").GetSingle(), spot.GetProperty("z").GetSingle(), 0);
        c.Flush(); // moves travel on their own channel
        Say(c, ".wear 58396"); // the test rod: one cast lands a level-1 fish
        while (!c.Expect(Opcode.SimpleMessageForThePlayer).ReadString().StartsWith("Equipped")) { }
        c.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.Mob, NetTime.Now, 60200).Write);
        var won = c.Expect(Opcode.WonCombat, TimeSpan.FromSeconds(15));
        won.ReadVariableInt32();
        Assert.Equal(60200, won.ReadVariableInt32());
        Assert.Equal((int)id, won.ReadVariableInt32());
        Assert.Equal(1, won.ReadVariableInt32()); // fishing
        Assert.True(won.ReadVariableInt32() > 0);
        won.ReadVariableInt32();
        won.ReadFloat();
        won.ReadVariableInt32();
        won.ReadVariableInt32();
        Assert.Equal(1, won.ReadVariableInt32()); // one catch
    }

    [Fact]
    public async Task Ground_items_are_picked_up_once_and_vanish_for_everyone()
    {
        if (!HaveData("item-spawns.json")) return;
        await using var host = await Start();
        var (a, _) = Join(host, "picker", "Sorcha");
        var (b, _) = Join(host, "watcher", "Tadhg");
        using var _a = a;
        using var _b = b;
        var point = FirstPoint("item-spawns.json", "");
        var template = point.GetProperty("items")[0][0].GetInt32();
        a.Move(point.GetProperty("x").GetSingle(), point.GetProperty("y").GetSingle(), point.GetProperty("z").GetSingle(), 0);
        a.Flush();
        a.Send(Opcode.PickupItem, m => m.WriteVariableInt32(2000));
        var reply = a.Expect(Opcode.PickupItemReply);
        Assert.Equal(1, reply.ReadByte());
        Assert.Equal(template, reply.ReadVariableInt32());
        Assert.Equal(1, reply.ReadVariableInt32());
        Assert.Contains(InventoryWithMoney.Read(reply).Items, i => i.TemplateId == template);
        Assert.Equal(2000, b.Expect(Opcode.ItemDespawn).ReadVariableInt32());

        a.Send(Opcode.PickupItem, m => m.WriteVariableInt32(2000));
        Assert.Equal(0, a.Expect(Opcode.PickupItemReply).ReadByte());
    }

    [Fact]
    public async Task Cooking_turns_ingredients_into_a_dish_and_cooking_xp()
    {
        if (!HaveData("cooking.json")) return;
        await using var host = await Start();
        var (c, _) = Join(host, "cook", "Ultan");
        using var _c = c;
        Say(c, ".give 60541 1"); // Barley
        c.Expect(Opcode.InventoryUpdate);
        Say(c, ".give 60545 1"); // Onion
        c.Expect(Opcode.InventoryUpdate);

        c.Send(Opcode.CraftingMessage, m => m.WriteVariableInt32(1));
        var known = c.Expect(Opcode.CraftingMessage);
        Assert.Equal(2u, known.ReadVariableUInt32());
        var recipes = new List<int>();
        for (int i = 0, n = known.ReadVariableInt32(); i < n; i++)
        {
            recipes.Add(known.ReadVariableInt32());
            known.ReadBoolean();
        }
        Assert.Contains(1, recipes); // Gruel

        c.Send(Opcode.TokenVendorMessage, m => { m.WriteVariableInt32(1); m.WriteVariableInt32(221); });
        var vendor = c.Expect(Opcode.TokenVendorMessage);
        Assert.Equal(2, vendor.ReadVariableInt32());
        vendor.ReadByte();
        Assert.Equal(221, vendor.ReadVariableInt32());
        var stock = new Dictionary<int, (int Item, int Cost)>();
        for (int i = 0, n = vendor.ReadVariableInt32(); i < n; i++)
        {
            var stockId = vendor.ReadVariableInt32();
            var item = vendor.ReadVariableInt32();
            vendor.ReadVariableInt32();
            stock[stockId] = (item, vendor.ReadVariableInt32());
        }
        Assert.Equal((60650, 840), stock[1]); // Gruel under its CraftingTemplate cost id

        c.Send(Opcode.CraftingMessage, m => { m.WriteVariableInt32(3); m.WriteVariableInt32(1); m.WriteVariableInt32(-1); });
        var cooked = c.Expect(Opcode.CraftingMessage);
        Assert.Equal(4u, cooked.ReadVariableUInt32());
        Assert.Contains(cooked.ReadVariableInt32(), new[] { 60580, 60650, 60720, 60790 }); // burnt, normal, exquisite, perfected
        cooked.ReadVariableInt32();
        cooked.ReadFloat();
        Assert.True(cooked.ReadVariableInt32() > 0);

        // The page then "buys" the dish: answered without charge.
        c.Send(Opcode.TokenVendorMessage, m => { m.WriteVariableInt32(3); m.WriteVariableInt32(221); m.WriteVariableInt32(1); m.WriteVariableInt32(1); });
        var bought = c.Expect(Opcode.TokenVendorMessage);
        Assert.Equal(4, bought.ReadVariableInt32());
        Assert.Equal(1, bought.ReadByte());
        bought.ReadVariableInt32();
        var inventory = InventoryWithMoney.Read(bought);
        Assert.DoesNotContain(inventory.Items, i => i.TemplateId is 60541 or 60545);
        Assert.Contains(inventory.Items, i => i.TemplateId is 60580 or 60650 or 60720 or 60790);
    }
}
