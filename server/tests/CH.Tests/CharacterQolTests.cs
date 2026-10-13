using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Client = CH.FakeClient.FakeClient;

namespace CH.Tests;

/// <summary>Deleting characters, the barbershop and expansions (docs/porting-status.md §2).</summary>
public sealed class CharacterQolTests : IDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-qol-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static void Say(Client c, string text) => c.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);

    [Fact]
    public async Task Characters_can_be_deleted_from_character_select()
    {
        await using var host = await ServerHost.StartAsync(new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        using var client = new Client();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("deleter");
        var keep = client.CreateCharacter("Keeper").CharacterId;
        var drop = client.CreateCharacter("Dropper").CharacterId;

        // A wrong name is refused.
        client.Send(Opcode.DeleteCharacter, b => { b.Write("Keeper"); b.WriteVariableUInt32(drop); });
        var refused = client.Expect(Opcode.DeleteCharacterReply);
        Assert.Equal((byte)CommandStatus.Rejected, refused.ReadByte());

        client.Send(Opcode.DeleteCharacter, b => { b.Write("Dropper"); b.WriteVariableUInt32(drop); });
        Assert.Equal((byte)CommandStatus.Accepted, client.Expect(Opcode.DeleteCharacterReply).ReadByte());
        Assert.Equal([keep], client.RequestCharacterList().Characters.Select(c => c.CharacterId));
    }

    [Fact]
    public async Task The_barbershop_changes_the_look_for_free_and_bank_expansions_add_slots()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        using var client = new Client();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("barber");
        var id = client.CreateCharacter("Ailbhe").CharacterId;
        var start = client.StartGame(id, "Ailbhe");
        client.FinishZoning();

        client.Send(Opcode.BarbershopMessage, b =>
        {
            b.WriteVariableInt32(1); b.WriteVariableInt32(500); // a price, ignored
            foreach (var v in new[] { 3, 2, 5, 4, 1, 1 }) b.WriteVariableInt32(v);
            b.Write(1.05f);
        });
        var reply = client.Expect(Opcode.BarbershopMessage);
        Assert.Equal(2, reply.ReadVariableInt32());
        Assert.Equal(1, reply.ReadByte()); // success
        reply.ReadString();
        reply.ReadByte();
        Assert.Equal(start.Coins, InventoryWithMoney.Read(reply).Coins); // free

        Say(client, ".give Bank Expansion 2");
        var stats = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
        Assert.Equal(120, stats.SoloBankItemLimit);

        client.Disconnect();
        using var again = new Client();
        again.Connect("127.0.0.1", host.Game.Port);
        again.VersionCheck();
        again.Login("barber");
        var look = again.RequestCharacterList().Characters.Single().Appearance;
        Assert.Equal((3, 5, 4), (look.Face, look.Hair, look.HairColour));
    }
}
