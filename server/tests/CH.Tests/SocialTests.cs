using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Client = CH.FakeClient.FakeClient;

namespace CH.Tests;

/// <summary>Friends, the block list, mail and clans.</summary>
public sealed class SocialTests : IDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-social-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Task<ServerHost> Start() =>
        ServerHost.StartAsync(new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);

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

    /// <summary>The next message of a kind that the reader accepts (returns non-null for), within the time limit.</summary>
    private static T? Await<T>(Client c, Opcode opcode, Func<NetIncomingMessage, T?> read, double seconds = 5) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            NetIncomingMessage m;
            try { m = c.Expect(opcode, until - DateTime.UtcNow); }
            catch (TimeoutException) { return null; }
            if (read(m) is { } found) return found;
        }
        return null;
    }

    private static string? Text(Client c, string contains) =>
        Await(c, Opcode.SimpleMessageForThePlayer, m => m.ReadString() is var t && t.Contains(contains) ? t : null);

    [Fact]
    public async Task Friends_need_a_yes_and_blocking_silences_a_player()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await Start();
        var (a, aId) = Join(host, "aoife", "Aoife");
        var (b, bId) = Join(host, "bran", "Bran");
        using var _a = a;
        using var _b = b;

        a.Send(Opcode.ClientFriendRequest, m => m.WriteVariableInt32((int)bId));
        var request = b.Expect(Opcode.ServerFriendRequest);
        Assert.Equal((int)aId, request.ReadVariableInt32());
        Assert.Equal("Aoife", request.ReadString());
        b.Send(Opcode.ClientFriendReply, m => { m.WriteVariableInt32((int)aId); m.WriteVariableInt32(1); });
        var reply = a.Expect(Opcode.ServerFriendReply);
        Assert.Equal("Bran", reply.ReadString());
        Assert.Equal(1, reply.ReadVariableInt32());
        var list = Await(a, Opcode.FriendList, m => FriendListMessage.Read(m) is { Count: 1 } l ? l : null);
        Assert.NotNull(list);
        Assert.Equal(("Bran", (int)bId, true), (list![0].Name, list[0].Id, list[0].Online));

        // Bran blocks Aoife: the friendship ends, her whispers and requests go nowhere.
        b.Send(Opcode.BlockCharacter, m => m.WriteVariableInt32((int)aId));
        var blocked = Await(b, Opcode.BlockedList, m => BlockedListMessage.Read(m) is { Count: 1 } l ? l : null);
        Assert.Equal(((int)aId, "Aoife"), blocked![0]);
        Assert.NotNull(Await(a, Opcode.FriendList, m => FriendListMessage.Read(m).Count == 0 ? "" : null));
        Say(a, "w Bran, hello?");
        Assert.NotNull(Text(a, "not online"));
        a.Send(Opcode.ClientFriendRequest, m => m.WriteVariableInt32((int)bId));
        var refused = a.Expect(Opcode.ServerFriendReply);
        refused.ReadString();
        Assert.Equal(2, refused.ReadVariableInt32());

        b.Send(Opcode.UnblockCharacter, m => { m.WriteVariableInt32((int)aId); m.WriteVariableInt32((int)aId); });
        Assert.NotNull(Await(b, Opcode.BlockedList, m => BlockedListMessage.Read(m).Count == 0 ? "" : null));
    }

    [Fact]
    public async Task Mail_carries_gold_and_items_for_stamps()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await Start();
        var (a, _) = Join(host, "sender", "Cian");
        var (b, bId) = Join(host, "reader", "Dara");
        using var _a = a;
        using var _b = b;
        Say(a, ".give 21948 3");
        a.Expect(Opcode.InventoryUpdate);
        Say(a, ".gold 500");
        a.Expect(Opcode.InventoryUpdate);
        Say(a, ".give 60542 4"); // Wheat
        var inventory = InventoryWithMoney.Read(a.Expect(Opcode.InventoryUpdate));
        var wheat = inventory.Items.Single(i => i.TemplateId == 60542);

        a.Send(Opcode.MailMessage, m => { m.WriteVariableInt32(10); m.Write("Dara"); m.WriteVariableInt32(5); m.WriteVariableInt32(100); m.WriteVariableInt32(1); });
        var info = a.Expect(Opcode.MailMessage);
        Assert.Equal(4, info.ReadVariableInt32());
        Assert.Equal("Dara", info.ReadString());
        info.ReadVariableInt32(); info.ReadVariableInt32(); info.ReadVariableInt32();
        Assert.Equal(1, info.ReadByte());
        Assert.Equal((int)bId, SocialCharacter.Read(info).Id);
        Assert.Equal(2, info.ReadVariableInt32()); // postage: 1 + 1 item

        a.Send(Opcode.MailMessage, m =>
        {
            m.WriteVariableInt32(11);
            m.WriteVariableInt32(2);
            m.WriteVariableInt32((int)bId);
            m.Write("Bread");
            m.Write("Some wheat for the oven.");
            m.WriteVariableInt32(100);
            m.WriteVariableInt32(2);
            m.WriteVariableInt32(wheat.ServerInventoryId); m.WriteVariableInt32(60542); m.WriteVariableInt32(4);
            m.WriteVariableInt32(-1); m.WriteVariableInt32(-1); m.WriteVariableInt32(-1);
        });
        var after = InventoryWithMoney.Read(a.Expect(Opcode.InventoryUpdate));
        Assert.Equal(inventory.Coins - 100, after.Coins);
        Assert.DoesNotContain(after.Items, i => i.TemplateId == 60542);
        Assert.Equal(1, after.Items.Where(i => i.TemplateId == 21948).Sum(i => i.Quantity));

        Assert.Equal(2, b.Expect(Opcode.MailMessage).ReadVariableInt32()); // new mail
        b.Send(Opcode.MailMessage, m => m.WriteVariableInt32(5));
        var box = b.Expect(Opcode.MailMessage);
        Assert.Equal(1, box.ReadVariableInt32());
        for (var i = 0; i < 5; i++) box.ReadVariableInt32();
        Assert.Equal(1, box.ReadVariableInt32());
        var mailId = box.ReadVariableInt32();
        box.ReadVariableInt32();
        Assert.Equal("Cian", box.ReadString());
        Assert.Equal("Bread", box.ReadString());
        Assert.Equal(100, box.ReadVariableInt32());
        Assert.Equal(1, box.ReadByte());

        Say(b, ".gold 0");
        var before = InventoryWithMoney.Read(b.Expect(Opcode.InventoryUpdate)).Coins;
        b.Send(Opcode.MailMessage, m => { m.WriteVariableInt32(9); m.WriteVariableInt32(mailId); });
        var taken = InventoryWithMoney.Read(b.Expect(Opcode.InventoryUpdate));
        Assert.Equal(before + 100, taken.Coins);
        Assert.Equal(4, taken.Items.Where(i => i.TemplateId == 60542).Sum(i => i.Quantity));
    }

    private static ClanBlock? AwaitClan(Client c, Func<ClanBlock, bool> match) =>
        Await(c, Opcode.ClanMessage, m => m.ReadVariableInt32() == 1 && ClanBlock.Read(m) is var block && match(block) ? block : null);

    [Fact]
    public async Task A_clan_is_founded_grows_chats_shares_a_bank_and_can_be_left()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await Start();
        var (a, aId) = Join(host, "leader", "Eoghan");
        var (b, bId) = Join(host, "recruit", "Fiadh");
        using var _a = a;
        using var _b = b;

        a.Send(Opcode.ClanMessage, m => { m.WriteVariableInt32(0); m.Write("Chickens"); });
        Assert.NotNull(Text(a, "Clan Banner"));
        Say(a, ".give 6 1");
        a.Send(Opcode.ClanMessage, m => { m.WriteVariableInt32(0); m.Write("Chickens"); });
        var founded = AwaitClan(a, c => c.ClanId >= 0);
        Assert.Equal("Chickens", founded!.Name);
        Assert.Equal(((int)aId, 4), (founded.Members[0].Member.Id, founded.Members[0].Rank));

        a.Send(Opcode.ClanMessage, m => { m.WriteVariableInt32(2); m.WriteVariableInt32((int)bId); });
        var invite = Await(b, Opcode.ClanMessage, m => m.ReadVariableInt32() == 8 ? m : null)!;
        var clanId = invite.ReadVariableInt32();
        Assert.Equal("Chickens", invite.ReadString());
        b.Send(Opcode.ClanMessage, m => { m.WriteVariableInt32(3); m.WriteVariableInt32(clanId); m.WriteVariableInt32(1); m.WriteVariableInt32((int)aId); });
        Assert.NotNull(AwaitClan(b, c => c.Members.Count == 2 && c.Members.Any(x => x.Member.Id == (int)bId && x.Rank == 0)));
        Assert.NotNull(AwaitClan(a, c => c.Members.Count == 2));

        // Clan chat ("c " prefix on channel 2).
        b.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Clan, "", "c cluck").WriteFromClient);
        Assert.NotNull(Await(a, Opcode.GeneralChat, m => ChatMessage.ReadFromServer(m) is { Channel: ChatChannel.Clan, Text: "cluck" } x ? x : null));

        // The Leader donates; a Recruit may look but not withdraw.
        Say(a, ".give 60542 5");
        var wheat = InventoryWithMoney.Read(a.Expect(Opcode.InventoryUpdate)).Items.Single(i => i.TemplateId == 60542);
        a.Send(Opcode.ClanBankingMessage, m => { m.Write((byte)1); m.Write((byte)1); m.WriteVariableInt32(wheat.ServerInventoryId); m.WriteVariableInt32(60542); m.WriteVariableInt32(5); });
        var bank = ReadClanBank(a.Expect(Opcode.ClanBankingMessageReply));
        Assert.Equal(5, bank.Single(i => i.TemplateId == 60542).Quantity);
        b.Send(Opcode.ClanBankingMessage, m => { m.Write((byte)1); m.Write((byte)0); m.WriteVariableInt32(bank[0].ServerInventoryId); m.WriteVariableInt32(60542); m.WriteVariableInt32(5); });
        Assert.Single(ReadClanBank(b.Expect(Opcode.ClanBankingMessageReply)));
        Assert.NotNull(Text(b, "can't withdraw"));

        // A Recruit demoting themselves leaves.
        b.Send(Opcode.ClanMessage, m => { m.WriteVariableInt32(5); m.WriteVariableInt32((int)bId); m.WriteVariableInt32(0); });
        Assert.NotNull(AwaitClan(b, c => c.ClanId < 0));
        Assert.NotNull(AwaitClan(a, c => c.Members.Count == 1));
    }

    private static List<ItemSlot> ReadClanBank(NetIncomingMessage m)
    {
        Assert.Equal(1, m.ReadByte());
        Assert.Equal(1, m.ReadVariableInt32());
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) ItemSlot.Read(m);
        var bank = new List<ItemSlot>();
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) bank.Add(ItemSlot.Read(m));
        return bank;
    }
}
