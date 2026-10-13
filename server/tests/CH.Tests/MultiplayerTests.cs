using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Client = CH.FakeClient.FakeClient;

namespace CH.Tests;

/// <summary>Two players on one server against the real client data (skips without it).</summary>
public sealed class MultiplayerTests : IDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-multi-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static (Client Client, uint Id) Join(ServerHost host, string user, string name, int @class = 1)
    {
        var client = new Client();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login(user);
        var id = client.CreateCharacter(name, @class: @class).CharacterId;
        client.StartGame(id, name);
        return (client, id);
    }

    /// <summary>EnteringNewZoneUpdate up to the other players: their server ids.</summary>
    private static List<uint> PlayersIn(NetIncomingMessage m)
    {
        for (var i = 0; i < 4; i++) m.ReadFloat();
        for (var list = 0; list < 2; list++)
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) m.ReadVariableInt32();
        var ids = new List<uint>();
        var count = m.ReadVariableUInt32();
        if (count > 0) ids.Add(m.ReadVariableUInt32()); // first entry's id; the rest isn't needed
        Assert.True(count <= 1, "test reads one player");
        return ids;
    }

    private static void Say(Client c, ChatChannel channel, string text) =>
        c.Send(Opcode.GeneralChat, new ChatMessage(channel, "", text).WriteFromClient);

    private static ChatMessage NextChat(Client c) => ChatMessage.ReadFromServer(c.Expect(Opcode.GeneralChat));

    [Fact]
    public async Task Two_players_see_move_talk_emote_party_and_leave()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);

        var (a, aId) = Join(host, "aoife", "Aoife");
        using var _a = a;
        a.Send(Opcode.FinishedZoning, _ => { });
        Assert.Empty(PlayersIn(a.Expect(Opcode.EnteringNewZoneUpdate)));
        a.Expect(Opcode.AllZoningMessagesSent);

        // Bran joins: he sees Aoife in the zone snapshot, and she is told he arrived.
        var (b, bId) = Join(host, "bran", "Bran", @class: 2);
        using var _b = b;
        b.Send(Opcode.FinishedZoning, _ => { });
        Assert.Equal([aId], PlayersIn(b.Expect(Opcode.EnteringNewZoneUpdate)));
        b.Expect(Opcode.AllZoningMessagesSent);
        var zoningIn = a.Expect(Opcode.CharacterZoningIn);
        Assert.Equal(bId, zoningIn.ReadVariableUInt32());

        // Aoife walks; Bran's position snapshots carry her (and only her).
        a.Move(-100f, -3.5f, -10f, 90f, speed: 5f);
        BatchZonePositionUpdate seen;
        do seen = BatchZonePositionUpdate.Read(b.Expect(Opcode.BatchZonePositionUpdate));
        while (seen.Players.Count == 0 || seen.Players[0].X != -100f);
        var aoife = Assert.Single(seen.Players);
        Assert.Equal((aId, -100f, -10f, false), (aoife.ServerId, aoife.X, aoife.Z, aoife.Stopped));

        // Whisper: Bran gets it as incoming from Aoife, Aoife sees her own outgoing line.
        Say(a, ChatChannel.WhisperOutgoing, "w bran, over here");
        var heard = NextChat(b);
        Assert.Equal((ChatChannel.WhisperIncoming, "Aoife", "over here"), (heard.Channel, heard.Sender, heard.Text));
        var echo = NextChat(a);
        Assert.Equal((ChatChannel.WhisperOutgoing, "Bran"), (echo.Channel, echo.Sender));

        // Emotes reach the whole zone, the sender included.
        a.Send(Opcode.CharacterPlayingEmote, new EmoteRequest(true, 66).Write);
        foreach (var c in new[] { a, b })
        {
            var emote = c.Expect(Opcode.CharacterPlayingEmote);
            Assert.Equal((aId, 66, true), (emote.ReadVariableUInt32(), emote.ReadVariableInt32(), emote.ReadBoolean()));
        }

        // Party: invite, accept, both get the two-member roster; party chat reaches both.
        a.Send(Opcode.PartyMessage, m => { m.WriteVariableInt32(0); m.WriteVariableInt32((int)bId); });
        var invite = b.Expect(Opcode.PartyMessage);
        Assert.Equal((1, (int)aId), (invite.ReadVariableInt32(), invite.ReadVariableInt32()));
        b.Send(Opcode.PartyMessage, m => { m.WriteVariableInt32(2); m.WriteVariableInt32((int)aId); m.WriteVariableInt32(1); });
        var result = a.Expect(Opcode.PartyMessage);
        Assert.Equal((3, (int)bId, 1), (result.ReadVariableInt32(), result.ReadVariableInt32(), result.ReadVariableInt32()));
        foreach (var c in new[] { a, b })
        {
            var roster = c.Expect(Opcode.PartyMessage);
            Assert.Equal((4, 2), (roster.ReadVariableInt32(), roster.ReadVariableInt32()));
        }
        Say(b, ChatChannel.Group, "g ready");
        foreach (var c in new[] { a, b })
        {
            var line = NextChat(c);
            Assert.Equal((ChatChannel.Group, "Bran", "ready"), (line.Channel, line.Sender, line.Text));
        }

        // Levelling up redraws Aoife for Bran (appearance update carries her level).
        Say(a, ChatChannel.Local, ".level 5");
        var look = b.Expect(Opcode.PlayerAppearanceUpdate);
        Assert.Equal(aId, look.ReadVariableUInt32());
        Assert.Equal(30, look.ReadVariableInt32()); // 25 equipment slots + headgear + fashion + level + PvP rank and rating

        // A kill near Bran shares the XP with him.
        Say(a, ChatChannel.Local, ".spawn Scrawny Coast Wolf 1");
        var wolf = a.Expect(Opcode.ZoneMonsterAppeared).ReadVariableUInt32();
        a.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.Mob, NetTime.Now, wolf).Write);
        var swing = b.Expect(Opcode.AttackState);
        Assert.Equal((true, false, (int)aId), (swing.ReadByte() == 1, swing.ReadByte() == 1, swing.ReadVariableInt32()));
        a.Expect(Opcode.WonCombat, TimeSpan.FromSeconds(20));
        string shared;
        do shared = b.Expect(Opcode.SimpleMessageForThePlayer, TimeSpan.FromSeconds(5)).ReadString();
        while (!shared.Contains("party"));
        Assert.Contains("Aoife's kill", shared);

        // Bran leaves the zone: Aoife is told, and his party survives the trip.
        Say(b, ChatChannel.Local, ".goto stonevale");
        b.Expect(Opcode.CorrectPlayerPosition);
        Assert.Equal(bId, a.Expect(Opcode.CharacterZoningOut).ReadVariableUInt32());

        // Logging in as Aoife again drops the first connection; Bran's party roster empties.
        using var again = new Client();
        again.Connect("127.0.0.1", host.Game.Port);
        again.VersionCheck();
        again.Login("aoife");
        Assert.Equal(CommandStatus.Accepted, again.StartGame(aId, "Aoife").Status);
        var emptied = b.Expect(Opcode.PartyMessage);
        Assert.Equal((4, 0), (emptied.ReadVariableInt32(), emptied.ReadVariableInt32()));
    }

    [Fact]
    public async Task First_login_claims_the_password_and_only_gms_run_gm_commands()
    {
        // LocalGm off: the test clients connect from loopback but should count as friends, not the host.
        var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = null, LocalGm = false }, NullLoggerFactory.Instance);

        using (var first = new Client())
        {
            first.Connect("127.0.0.1", host.Game.Port);
            first.VersionCheck();
            Assert.Equal(CommandStatus.Accepted, first.Login("cara", "HASH-ONE").Status);
        }
        using var friend = new Client();
        friend.Connect("127.0.0.1", host.Game.Port);
        friend.VersionCheck();
        var wrong = friend.Login("cara", "HASH-TWO");
        Assert.Equal(CommandStatus.Rejected, wrong.Status);
        Assert.Contains("Wrong password", wrong.Reason);
        Assert.Equal(CommandStatus.Rejected, friend.Login("cara", "").Status);
        Assert.Equal(CommandStatus.Accepted, friend.Login("cara", "HASH-ONE").Status);
        var id = friend.CreateCharacter("Cara").CharacterId;
        friend.StartGame(id, "Cara");
        friend.FinishZoning();
        // The first login of the day hands out the calendar's daily reward.
        Assert.Contains("Daily reward", friend.Expect(Opcode.SimpleMessageForThePlayer).ReadString());

        Say(friend, ChatChannel.Local, ".level 50");
        Assert.Contains("for GMs", friend.Expect(Opcode.SimpleMessageForThePlayer).ReadString());
        Say(friend, ChatChannel.Local, ".who");
        Assert.Equal("1 online: Cara (level 1, zone 2)", friend.Expect(Opcode.SimpleMessageForThePlayer).ReadString());

        friend.Disconnect();
        await host.DisposeAsync();

        // Restarted with --gm cara: GM commands work.
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = null, LocalGm = false };
        options.GmAccounts.Add("cara");
        await using var host2 = await ServerHost.StartAsync(options, NullLoggerFactory.Instance);
        using var again = new Client();
        again.Connect("127.0.0.1", host2.Game.Port);
        again.VersionCheck();
        Assert.Equal(CommandStatus.Accepted, again.Login("cara", "HASH-ONE").Status);
        again.StartGame(id, "Cara");
        again.FinishZoning();
        Say(again, ChatChannel.Local, ".level 50");
        again.Expect(Opcode.LevelUp);
    }

    [Fact]
    public async Task A_hidden_rogue_vanishes_for_others_and_buffs_are_shared()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        var (rogue, rogueId) = Join(host, "sionnach", "Sionnach", @class: 5);
        using var _r = rogue;
        rogue.FinishZoning();
        var (watcher, _) = Join(host, "eithne", "Eithne");
        using var _w = watcher;
        watcher.FinishZoning();

        // Visible at first: the rogue is in Eithne's snapshots.
        rogue.Move(-105f, -3.6f, -15f, 0, speed: 5f);
        BatchZonePositionUpdate seen;
        do seen = BatchZonePositionUpdate.Read(watcher.Expect(Opcode.BatchZonePositionUpdate));
        while (seen.Players.Count == 0);
        Assert.Equal(rogueId, seen.Players[0].ServerId);

        // Hide (skill 17): Eithne gets the rogue in the "hidden" list, and the next snapshots leave him out.
        rogue.Send(Opcode.UseSkillOnNPC, new UseSkill(TargetType.Self, 0, NetTime.Now, 17).Write);
        var effects = watcher.Expect(Opcode.OthersStatusEffect, TimeSpan.FromSeconds(5));
        Assert.Equal(0, effects.ReadVariableInt32());              // visible players
        Assert.Equal(1, effects.ReadVariableInt32());              // hidden players
        Assert.Equal((0, rogueId), (effects.ReadByte(), effects.ReadVariableUInt32()));
        Assert.Equal(1, effects.ReadVariableInt32());              // one effect
        Assert.Equal(9, effects.ReadVariableInt32());              // Hide
        rogue.Move(-104f, -3.6f, -15f, 0, speed: 5f);
        var empty = BatchZonePositionUpdate.Read(watcher.Expect(Opcode.BatchZonePositionUpdate));
        Assert.Empty(empty.Players);
    }

    [Fact]
    public async Task A_duel_counts_down_and_ends_in_victory_at_one_hp()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        var (a, aId) = Join(host, "fionn", "Fionn");
        using var _a = a;
        a.FinishZoning();
        var (b, bId) = Join(host, "oisin", "Oisin");
        using var _b = b;
        b.FinishZoning();
        Say(a, ChatChannel.Local, ".level 60");
        a.Expect(Opcode.LevelUp);

        // Challenge, prompt, accept.
        a.Send(Opcode.PVPMessage, m => { m.WriteVariableInt32(0); m.WriteVariableUInt32(bId); });
        var prompt = b.Expect(Opcode.PVPMessage);
        Assert.Equal((1, aId), (prompt.ReadVariableInt32(), prompt.ReadVariableUInt32()));
        b.Send(Opcode.PVPMessage, m => { m.WriteVariableInt32(2); m.Write((byte)1); m.WriteVariableUInt32(aId); });
        var reply = a.Expect(Opcode.PVPMessage);
        Assert.Equal((3, (byte)1, bId), (reply.ReadVariableInt32(), reply.ReadByte(), reply.ReadVariableUInt32()));

        // Both get the begin message with a countdown, then the opponent in their hate list.
        foreach (var (c, opponent) in new[] { (a, bId), (b, aId) })
        {
            var begin = c.Expect(Opcode.PVPMessage);
            Assert.Equal(4, begin.ReadVariableInt32());
            var start = begin.ReadDouble();
            Assert.True(begin.ReadDouble() > start);
            Assert.Equal(opponent, begin.ReadVariableUInt32());
        }
        // Attacking before the countdown ends does nothing.
        a.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.OtherPlayer, NetTime.Now, bId).Write);
        Assert.Throws<TimeoutException>(() => b.Expect(Opcode.CombatDamageMessage, TimeSpan.FromSeconds(2)));
        var hate = b.Expect(Opcode.PlayerHateList, TimeSpan.FromSeconds(6));
        Assert.Equal((1, aId), (hate.ReadVariableInt32(), hate.ReadVariableUInt32()));

        // Now the level 60 attacker wins; the loser ends on 1 HP.
        a.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.OtherPlayer, NetTime.Now, bId).Write);
        var won = a.Expect(Opcode.PVPMessage, TimeSpan.FromSeconds(30));
        Assert.Equal((5, bId, 2), (won.ReadVariableInt32(), won.ReadVariableUInt32(), won.ReadVariableInt32()));
        var lost = b.Expect(Opcode.PVPMessage);
        Assert.Equal((5, aId, 3), (lost.ReadVariableInt32(), lost.ReadVariableUInt32(), lost.ReadVariableInt32()));
        Assert.Equal(0, b.Expect(Opcode.PlayerHateList).ReadVariableInt32());
    }

    [Fact]
    public async Task Trading_swaps_offered_coins_and_items_when_both_confirm()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        var a = new Client();
        a.Connect("127.0.0.1", host.Game.Port);
        a.VersionCheck();
        a.Login("aine");
        var aId = a.CreateCharacter("Aine").CharacterId;
        var aStart = a.StartGame(aId, "Aine");
        using var _a = a;
        a.FinishZoning();
        var (b, bId) = Join(host, "ronan", "Ronan");
        using var _b = b;
        b.FinishZoning();
        var kitPotion = aStart.Inventory.First(i => i.TemplateId == CH.Server.Game.ClassKit.MinorHealthPotion);
        // Starter-kit items are bound; a GM-given Health Potion (17) stack is tradeable.
        Say(a, ChatChannel.Local, ".give 17 5");
        var potion = InventoryWithMoney.Read(a.Expect(Opcode.InventoryUpdate)).Items.First(i => i.TemplateId == 17);

        a.Send(Opcode.TradeMessage, m => { m.WriteVariableInt32(0); m.WriteVariableInt32((int)bId); m.Write((byte)0); });
        var ask = b.Expect(Opcode.TradeMessage);
        Assert.Equal((1, (int)aId), (ask.ReadVariableInt32(), ask.ReadVariableInt32()));
        b.Send(Opcode.TradeMessage, m => { m.WriteVariableInt32(0); m.WriteVariableInt32((int)aId); m.Write((byte)1); });
        foreach (var (c, other) in new[] { (a, bId), (b, aId) })
        {
            var open = c.Expect(Opcode.TradeMessage);
            Assert.Equal((10, (int)other), (open.ReadVariableInt32(), open.ReadVariableInt32()));
            c.Expect(Opcode.TradeMessage); // first screen
        }

        // Aine offers 30 coins and 2 potions; Ronan sees them as "their offer".
        a.Send(Opcode.TradeMessage, m => { m.WriteVariableInt32(4); m.Write((byte)1); m.WriteVariableInt32(30); });
        a.Expect(Opcode.TradeMessage);
        b.Expect(Opcode.TradeMessage);
        a.Send(Opcode.TradeMessage, m =>
        {
            m.WriteVariableInt32(4); m.Write((byte)0); m.Write((byte)1);
            m.WriteVariableInt32(kitPotion.TemplateId); m.WriteVariableInt32(kitPotion.ServerInventoryId); m.WriteVariableInt32(1);
        });
        var refused = a.Expect(Opcode.TradeMessage);
        Assert.Equal((5, (byte)0, "That item can't be traded."), (refused.ReadVariableInt32(), refused.ReadByte(), refused.ReadString()));
        b.Expect(Opcode.TradeMessage);
        a.Send(Opcode.TradeMessage, m =>
        {
            m.WriteVariableInt32(4); m.Write((byte)0); m.Write((byte)1);
            m.WriteVariableInt32(potion.TemplateId); m.WriteVariableInt32(potion.ServerInventoryId); m.WriteVariableInt32(2);
        });
        a.Expect(Opcode.TradeMessage);
        var screen = b.Expect(Opcode.TradeMessage);
        Assert.Equal((5, (byte)1), (screen.ReadVariableInt32(), screen.ReadByte()));
        InventoryWithMoney.Read(screen);                       // own backpack
        Assert.Empty(InventoryWithMoney.Read(screen).Items);   // own offer
        var theirs = InventoryWithMoney.Read(screen);
        Assert.Equal(30, theirs.Coins);
        Assert.Equal((potion.TemplateId, 2), (theirs.Items.Single().TemplateId, theirs.Items.Single().Quantity));

        // Both ready, both confirm: the trade completes for both.
        foreach (var c in new[] { a, b }) c.Send(Opcode.TradeMessage, m => { m.WriteVariableInt32(6); m.Write((byte)1); });
        a.Send(Opcode.TradeMessage, m => { m.WriteVariableInt32(9); m.WriteVariableInt32((int)bId); });
        b.Send(Opcode.TradeMessage, m => { m.WriteVariableInt32(9); m.WriteVariableInt32((int)aId); });
        InventoryWithMoney Done(Client c)
        {
            NetIncomingMessage m;
            do m = c.Expect(Opcode.TradeMessage);
            while (m.ReadVariableInt32() != 8);
            return InventoryWithMoney.Read(m);
        }
        var aAfter = Done(a);
        var bAfter = Done(b);
        Assert.Equal(aStart.Coins - 30, aAfter.Coins);
        Assert.Equal(potion.Quantity - 2, aAfter.Items.Single(i => i.TemplateId == potion.TemplateId).Quantity);
        Assert.Equal(aStart.Coins + 30, bAfter.Coins); // both started with the starter kit's coins
        Assert.Equal(2, bAfter.Items.Single(i => i.TemplateId == potion.TemplateId).Quantity); // Ronan had none
    }
}
