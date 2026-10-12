using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace CH.Tests;

/// <summary>Legacy chests against the real client data (skips without it).</summary>
public sealed class ChestTests : IAsyncDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private static readonly Lazy<Balance> Data = new(() => Balance.Load(BalancePath));

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-chest-").FullName;

    [Fact]
    public void Every_container_has_contents_and_fixed_packs_match_their_descriptions()
    {
        if (!File.Exists(BalancePath)) return;
        var b = Data.Value;
        var catalog = ChestCatalog.Build(b);
        var containers = b.Items.Values.Count(i => i.Usable && ChestCatalog.ContainerLinks.Contains(i.SkillLinkId));
        Assert.Equal(containers, catalog.Chests.Count);
        Assert.True(containers >= 300);

        ChestRoll Open(int id) => ChestCatalog.Roll(catalog.For(id)!, new Random(7));
        string Name((int TemplateId, int Quantity) x) => b.Items[x.TemplateId].Name;

        Assert.Equal((5, "Super Combination Elixir"), Open(34777).Items.Select(x => (x.Quantity, Name(x))).Single());       // crate of five
        Assert.Equal((48, "Mount Token"), Open(60216).Items.Select(x => (x.Quantity, Name(x))).Single());                   // "Fourty-Eight"
        Assert.Equal(6, Open(219).Items.Count);                                                                              // Deluxe starter kit
        Assert.EndsWith("Pirate Shirt", Name(Open(23564).Items.Single()));                                                   // one random pirate shirt
        Assert.InRange(Open(34784).Platinum, 5, 330);                                                                        // Platinum Lockbox
        for (var i = 0; i < 50; i++)
        {
            var mystery = ChestCatalog.Roll(catalog.For(36762)!, new Random(i));
            var count = mystery.Items.Count + (mystery.Platinum > 0 ? 1 : 0) + (mystery.Coins > 0 ? 1 : 0);
            Assert.InRange(count, 1, 3);
            Assert.DoesNotContain(mystery.Items, x => catalog.For(x.TemplateId) is not null); // no chests inside chests
        }
    }

    [Fact]
    public async Task Mystery_chest_opens_on_the_chest_screen_and_next_chest_opens_another()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("opener");
        var created = client.CreateCharacter("Eithne", @class: 2);
        client.StartGame(created.CharacterId, "Eithne");
        client.Send(Opcode.FinishedZoning, _ => { });
        client.Expect(Opcode.AllZoningMessagesSent);

        client.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", ".chest mystery chest 2").WriteFromClient);
        var inv = client.Expect(Opcode.InventoryUpdate);
        inv.ReadVariableInt32();
        var chest = Enumerable.Range(0, inv.ReadVariableInt32()).Select(_ => ItemSlot.Read(inv)).Single(i => i.TemplateId == 36762);
        Assert.Equal(2, chest.Quantity);

        client.Send(Opcode.UseItem, new UseItemRequest(chest.ServerInventoryId, TargetType.Self, 0).Write);
        Assert.Equal((byte)CommandStatus.Accepted, client.Expect(Opcode.UseItemReply).ReadByte());
        var screens = ReadScreens(client);
        Assert.Equal("popup_magbox_open_prefab", screens[0].Prefab);
        Assert.Equal("popup_magbox_final_prefab", screens[^1].Prefab);
        Assert.All(screens.Skip(1), p => Assert.Single(p.Text));

        // "Next Chest" (option 1 on the final popup) opens the second one.
        client.Send(Opcode.InGamePopup, b => { b.WriteVariableInt32(2); b.WriteVariableInt32(42); b.WriteVariableInt32(1); });
        client.Expect(Opcode.UseItemReply);
        Assert.Equal("popup_magbox_open_prefab", ReadScreens(client)[0].Prefab);
    }

    private static List<(int Id, string Prefab, List<string> Text)> ReadScreens(CH.FakeClient.FakeClient client)
    {
        var screens = new List<(int, string, List<string>)>();
        while (true)
        {
            var m = client.Expect(Opcode.InGamePopup);
            Assert.Equal(1, m.ReadVariableInt32());
            var id = m.ReadVariableInt32();
            m.ReadByte();
            Assert.Equal(1, m.ReadVariableInt32());
            var prefab = m.ReadString();
            var text = Enumerable.Range(0, m.ReadVariableInt32()).Select(_ => m.ReadString()).ToList();
            screens.Add((id, prefab, text));
            if (prefab == "popup_magbox_final_prefab") return screens;
        }
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
