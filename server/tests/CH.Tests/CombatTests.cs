using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace CH.Tests;

/// <summary>
/// Zone population, GM spawning and the combat loop against the real client balance data (skips without it).
/// </summary>
public sealed class CombatTests : IAsyncDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-combat-").FullName;

    private static void Chat(CH.FakeClient.FakeClient c, string text) =>
        c.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);

    private static string NextSystemLine(CH.FakeClient.FakeClient c) => c.Expect(Opcode.SimpleMessageForThePlayer).ReadString();

    [Fact]
    public async Task Zone_populates_gm_spawns_and_a_wolf_dies_to_auto_attack_and_skills()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("fighter");
        var created = client.CreateCharacter("Fiadh", @class: 1);
        client.StartGame(created.CharacterId, "Fiadh");

        // Zone entry sends the auto-populated monsters before AllZoningMessagesSent.
        client.Send(Opcode.FinishedZoning, _ => { });
        client.Expect(Opcode.EnteringNewZoneUpdate);
        var batch = client.Expect(Opcode.BatchZoneMonsterUpdate);
        var count = batch.ReadVariableUInt32();
        Assert.InRange(count, 40u, 80u);
        client.Expect(Opcode.AllZoningMessagesSent);

        Chat(client, ".where");
        Assert.StartsWith("Zone 2 (Lir's Reach)", NextSystemLine(client));

        // Level up so the fight is short, then spawn a scrawny wolf on top of us.
        Chat(client, ".level 20");
        client.Expect(Opcode.LevelUp);
        Chat(client, ".spawn Scrawny Coast Wolf 1");
        var appeared = client.Expect(Opcode.ZoneMonsterAppeared);
        var wolfId = appeared.ReadVariableUInt32();
        Assert.Equal(70003u, appeared.ReadVariableUInt32());
        Assert.Contains("Scrawny Coast Wolf", NextSystemLine(client));

        // A skill first: Pummel (1) on the wolf completes with a cooldown and damages it.
        client.Send(Opcode.UseSkillOnNPC, new UseSkill(TargetType.Mob, wolfId, NetTime.Now, 1).Write);
        var skill = client.Expect(Opcode.SkillUpdate, TimeSpan.FromSeconds(5));
        Assert.Equal(1, skill.ReadVariableInt32());
        skill.ReadVariableInt32();
        Assert.True(skill.ReadFloat() > 0, "a successful cast reports its cooldown");

        // Then auto-attack until the wolf dies: damage messages carry its falling HP, then the kill reward arrives.
        client.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.Mob, NetTime.Now, wolfId).Write);
        var won = client.Expect(Opcode.WonCombat, TimeSpan.FromSeconds(20));
        Assert.Equal(70003, won.ReadVariableInt32());
        Assert.Equal((int)wolfId, won.ReadVariableInt32());
        won.ReadVariableInt32(); // killer
        won.ReadVariableInt32(); // level type
        Assert.True(won.ReadVariableInt32() > 0, "xp gained");

        // The corpse is removed after a few seconds.
        var gone = client.Expect(Opcode.ZoneMonsterDisappeared, TimeSpan.FromSeconds(8));
        Assert.Equal(wolfId, gone.ReadVariableUInt32());

        // The GM spawn is saved and listed with its id.
        Chat(client, ".near 5");
        Assert.Contains("spawn #1: Scrawny Coast Wolf", NextSystemLine(client));
        Chat(client, ".delete 1");
        client.Expect(Opcode.ZoneMonsterDisappeared);
    }

    [Fact]
    public void Npc_conversations_are_matched_by_name()
    {
        if (!File.Exists(BalancePath)) return;
        var balance = Balance.Load(BalancePath);
        Assert.Equal("cv_private_duncan", balance.ConversationFor(balance.Mobs[9])!.Filename);
        Assert.Equal("cv_quigley_merchant", balance.ConversationFor(balance.Mobs[70])!.Filename);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
