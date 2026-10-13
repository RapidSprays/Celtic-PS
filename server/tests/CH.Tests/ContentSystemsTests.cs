using System.Text.Json;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Client = CH.FakeClient.FakeClient;

namespace CH.Tests;

/// <summary>PvP zones and ranks, leaderboards, the living calendar, tarot and tutorials.</summary>
public sealed class ContentSystemsTests : IDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-content-").FullName;

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

    private static List<int> HateList(NetIncomingMessage m)
    {
        var list = new List<int>();
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) list.Add((int)m.ReadVariableUInt32());
        return list;
    }

    [Fact]
    public async Task In_the_arena_everyone_is_an_enemy_and_a_kill_earns_pvp_experience()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await Start();
        var (a, aId) = Join(host, "gladiator", "Aodh");
        var (b, bId) = Join(host, "victim", "Breasal");
        using var _a = a;
        using var _b = b;
        Say(a, ".level 120");
        foreach (var c in new[] { a, b })
        {
            Say(c, ".goto 8");
            c.FinishZoning();
        }
        Assert.NotNull(Await(a, Opcode.PlayerHateList, m => HateList(m).Contains((int)bId) ? "" : null));
        a.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.OtherPlayer, NetTime.Now, bId).Write);
        Assert.NotNull(Await(b, Opcode.SimpleMessageForThePlayer, m => m.ReadString() == "Aodh defeated Breasal." ? "" : null, seconds: 30));
        Assert.NotNull(Await(a, Opcode.StatsUpdate, m => m.ReadByte() == 0 && m.ReadVariableInt32() == 1 && m.ReadVariableInt32() == 31 && m.ReadVariableInt32() > 0 ? "" : null));

        // The kill is on the leaderboard (the client's JSON web service).
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"http://127.0.0.1:{host.Patch.Port}/stats/RankingPage.aspx?type=ranking_pvp_kills&playerWorldId=2&charId={aId}&page=1&numRecs=20");
        using var doc = JsonDocument.Parse(json);
        var top = doc.RootElement.GetProperty("RankingDataList")[0];
        Assert.Equal(("Aodh", 1.0), (top.GetProperty("Name").GetString(), top.GetProperty("RankingValue").GetDouble()));
        Assert.Equal(1, doc.RootElement.GetProperty("PlayerRankingData").GetProperty("Rank").GetInt32());
        var global = await http.GetStringAsync($"http://127.0.0.1:{host.Patch.Port}/stats/GlobalPage.aspx");
        Assert.Contains("\"ClassDictionary\"", global);
    }

    [Fact]
    public async Task The_calendar_offers_one_reward_a_day()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await Start();
        var (c, _) = Join(host, "daily", "Cliodhna");
        using var _c = c;
        var full = c.Expect(Opcode.LivingCalendarInfo);
        Assert.Equal(0, full.ReadByte());
        Assert.Equal(1u, full.ReadVariableUInt32());
        Assert.Equal(1u, full.ReadVariableUInt32());

        Say(c, ".gold 0");
        var before = InventoryWithMoney.Read(c.Expect(Opcode.InventoryUpdate)).Coins;
        c.Send(Opcode.LivingCalendarAction, m => { m.Write((byte)1); m.WriteVariableUInt32(1); });
        Assert.Equal(before + 500, InventoryWithMoney.Read(c.Expect(Opcode.InventoryUpdate)).Coins); // day 1: 5 Bags of Gold
        var granted = c.Expect(Opcode.LivingCalendarInfo);
        Assert.Equal(3, granted.ReadByte());
        Assert.Equal(1u, granted.ReadVariableUInt32());
        Assert.Equal(0, granted.ReadInt32());
        Assert.True(granted.ReadBoolean());

        c.Send(Opcode.LivingCalendarAction, m => { m.Write((byte)1); m.WriteVariableUInt32(1); });
        Assert.Equal(2, c.Expect(Opcode.LivingCalendarInfo).ReadByte()); // already claimed today
    }

    [Fact]
    public async Task Tarot_deals_a_board_of_pairs_and_remembers_the_game()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await Start();
        var (c, _) = Join(host, "seer", "Damhnait");
        using var _c = c;
        c.Send(Opcode.TarotGameMessage, m => m.WriteVariableInt32(1));
        var decks = c.Expect(Opcode.TarotGameMessage);
        Assert.Equal(1, decks.ReadVariableInt32());
        Assert.Equal(3, decks.ReadVariableInt32());

        c.Send(Opcode.TarotGameMessage, m => { m.WriteVariableInt32(2); m.WriteVariableInt32(2); }); // Premium needs a Tarot Token
        var refused = c.Expect(Opcode.TarotGameMessage);
        Assert.Equal((2, 2), (refused.ReadVariableInt32(), refused.ReadVariableInt32()));
        Assert.False(refused.ReadBoolean());

        c.Send(Opcode.TarotGameMessage, m => { m.WriteVariableInt32(2); m.WriteVariableInt32(0); });
        var started = c.Expect(Opcode.TarotGameMessage);
        Assert.Equal((2, 0), (started.ReadVariableInt32(), started.ReadVariableInt32()));
        Assert.True(started.ReadBoolean());
        Assert.True(started.ReadBoolean()); // new
        Assert.Equal(18, started.ReadVariableInt32());

        // Flip two cards: the first shows its face, the second completes a move.
        int Flip(int index, bool second)
        {
            c.Send(Opcode.TarotGameMessage, m => { m.WriteVariableInt32(3); m.WriteVariableInt32(0); m.WriteVariableInt32(index); });
            var r = c.Expect(Opcode.TarotGameMessage);
            Assert.Equal(3, r.ReadVariableInt32());
            r.ReadVariableInt32();
            r.ReadVariableInt32();
            var face = r.ReadVariableInt32();
            Assert.Equal(second, r.ReadBoolean());
            if (!second) return face;
            r.ReadVariableInt32();
            var face2 = r.ReadVariableInt32();
            r.ReadBoolean(); r.ReadBoolean(); r.ReadBoolean();
            Assert.Equal(17, r.ReadVariableInt32());
            return face2;
        }
        Assert.InRange(Flip(0, false), 0, 8);
        Assert.InRange(Flip(1, true), 0, 8);

        // Coming back to the deck continues the game.
        c.Send(Opcode.TarotGameMessage, m => { m.WriteVariableInt32(2); m.WriteVariableInt32(0); });
        var resumed = c.Expect(Opcode.TarotGameMessage);
        resumed.ReadVariableInt32(); resumed.ReadVariableInt32();
        Assert.True(resumed.ReadBoolean());
        Assert.False(resumed.ReadBoolean()); // not new
        resumed.ReadBoolean();
        for (var i = 0; i < 18; i++) resumed.ReadVariableInt32();
        Assert.Equal(17, resumed.ReadVariableInt32());
    }

    [Fact]
    public async Task New_characters_get_the_tutorial_and_can_turn_it_off()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await Start();
        var (c, _) = Join(host, "newbie", "Eimear");
        using var _c = c;
        Assert.NotNull(Await(c, Opcode.FirstTime, m => m.ReadVariableInt32() == 100 ? "" : null));
        Say(c, ".tutorial off");
        Assert.NotNull(Await(c, Opcode.SimpleMessageForThePlayer, m => m.ReadString().Contains("off") ? "" : null));
    }
}
