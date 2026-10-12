using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace CH.Tests;

/// <summary>The passive tree (docs/passive-tree.md): the shipped layout, its rules, and the bonuses in play.</summary>
public sealed class PassiveTreeTests : IAsyncDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private static readonly string TreePath = Path.Combine(AppContext.BaseDirectory, "content", "passive-tree.json");

    private static readonly Lazy<PassiveTree> Tree = new(() => PassiveTree.Load(TreePath));

    private const int Warrior = 1, WarriorStart = 100, Strength1 = 101, Vitality2 = 102, MightPath = 114, CleavingFury = 117, IronHide = 120;

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-tree-").FullName;

    [Fact]
    public void The_shipped_tree_is_valid_with_five_class_starts_and_five_keystones()
    {
        var tree = Tree.Value;
        Assert.Empty(tree.Validate());
        Assert.InRange(tree.Nodes.Count, 80, 110);
        Assert.Equal(5, tree.Nodes.Values.Count(n => n.Kind == "start"));
        Assert.Equal(PassiveTree.KnownKeystones, tree.Nodes.Values.Where(n => n.Kind == "keystone").Select(n => n.Keystone!).ToHashSet());
        Assert.Equal(WarriorStart, tree.StartFor(Warrior));
        Assert.Equal(50, tree.PointsFor(250));
        // Experience and gold stay small: at most about 15% each for the whole tree.
        Assert.InRange(tree.Totals(tree.Nodes.Keys).Stats["xpPct"], 1, 15);
        Assert.InRange(tree.Totals(tree.Nodes.Keys).Stats["goldPct"], 1, 15);
    }

    [Fact]
    public void Nodes_are_taken_next_to_what_you_have_and_given_back_only_if_nothing_hangs_off_them()
    {
        var tree = Tree.Value;
        var owned = new HashSet<int>();
        Assert.Contains("no passive points", tree.CannotAllocate(owned, Warrior, 4, Strength1));
        Assert.Null(tree.CannotAllocate(owned, Warrior, 10, Strength1));
        Assert.Contains("isn't connected", tree.CannotAllocate(owned, Warrior, 10, Vitality2));
        Assert.Contains("isn't connected", tree.CannotAllocate(owned, Warrior, 50, IronHide));
        Assert.Contains("free", tree.CannotAllocate(owned, Warrior, 10, WarriorStart));
        Assert.Contains("isn't connected", tree.CannotAllocate(owned, 5, 10, Strength1)); // a Rogue starts elsewhere

        owned.Add(Strength1);
        Assert.Null(tree.CannotAllocate(owned, Warrior, 10, Vitality2));
        owned.Add(Vitality2);
        Assert.Contains("no passive points", tree.CannotAllocate(owned, Warrior, 10, MightPath)); // 2 points at level 10
        Assert.Contains("depend on", tree.CannotRefund(owned, Warrior, Strength1));
        Assert.Null(tree.CannotRefund(owned, Warrior, Vitality2));

        // Nodes that lose their connection don't count.
        Assert.Equal(new HashSet<int> { Strength1, Vitality2 }, tree.Connected([Strength1, Vitality2, 9999], Warrior));
        Assert.Empty(tree.Connected([Vitality2], Warrior));

        var totals = tree.Totals(owned);
        Assert.Equal((15, 15), (totals["str"], totals["vit"]));
        Assert.Contains("+15 Strength", tree.Describe(tree.Nodes[Strength1]));
    }

    [Fact]
    public async Task Passives_change_stats_skills_and_cooldowns_and_survive_a_restart()
    {
        if (!File.Exists(BalancePath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        uint id;
        await using (var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance))
        {
            using var client = new CH.FakeClient.FakeClient();
            client.Connect("127.0.0.1", host.Game.Port);
            client.VersionCheck();
            client.Login("treewalker");
            id = client.CreateCharacter("Cian", @class: Warrior).CharacterId;
            client.StartGame(id, "Cian");
            client.FinishZoning();

            // Level 20: four points.
            Chat(client, ".level 20");
            client.Expect(Opcode.LevelUp);
            var before = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
            Chat(client, ".tree");
            var layout = TreeLayout.Read(client.Expect(PrivateOpcode.TreeLayout));
            Assert.Equal(Tree.Value.Nodes.Count, layout.Nodes.Count);
            Assert.Contains(layout.Nodes, n => n.Id == CleavingFury && n.Kind == TreeNodeKind.Notable && n.Lines.Any(l => l.StartsWith("Warrior skills:")));
            var state = TreeState.Read(client.Expect(PrivateOpcode.TreeState));
            Assert.True(state.Open);
            Assert.Equal((WarriorStart, 4, 4), (state.Start, state.PointsFree, state.PointsTotal));

            // +15 Strength: a bonus on the stats page (base unchanged), and more attack for a melee weapon.
            Chat(client, $".tree alloc {Strength1}");
            var baseStats = client.Expect(Opcode.BaseStats);
            baseStats.ReadVariableInt32();
            Assert.Equal(5, baseStats.ReadVariableInt32()); // base strength unchanged
            var after = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
            Assert.Equal(before.Strength + 15, after.Strength);
            Assert.Equal(before.Attack + 15, after.Attack);
            Assert.Equal(new[] { Strength1 }, TreeState.Read(client.Expect(PrivateOpcode.TreeState)).Owned);

            // Not connected yet: refused, with the reason in chat.
            Chat(client, $".tree alloc {CleavingFury}");
            Assert.Contains("isn't connected", NextSystemLine(client));

            // Reset, then walk to Cleaving Fury: Warrior skills get +2 ranks and a 10% shorter cooldown.
            Chat(client, ".tree reset");
            Assert.Contains("refunded", NextSystemLine(client));
            foreach (var node in new[] { MightPath, MightPath + 1, MightPath + 2, CleavingFury })
            {
                Chat(client, $".tree alloc {node}");
                client.Expect(PrivateOpcode.TreeState);
            }
            Chat(client, ".tree list");
            Assert.Contains("0 of 4 points free", NextSystemLine(client));

            Chat(client, ".spawn Scrawny Coast Wolf 1");
            var wolf = client.Expect(Opcode.ZoneMonsterAppeared).ReadVariableUInt32();
            client.Send(Opcode.UseSkillOnNPC, new UseSkill(TargetType.Mob, wolf, NetTime.Now, 1).Write); // Pummel, 10 s
            var cast = client.Expect(Opcode.SkillUpdate, TimeSpan.FromSeconds(5));
            Assert.Equal(1, cast.ReadVariableInt32());
            Assert.Equal(2, cast.ReadVariableInt32()); // rank 0 trained + 2 from the notable
            Assert.Equal(9f, cast.ReadFloat(), 3);
        }

        // After a restart the passives are still there.
        await using (var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance))
        {
            using var client = new CH.FakeClient.FakeClient();
            client.Connect("127.0.0.1", host.Game.Port);
            client.VersionCheck();
            client.Login("treewalker");
            client.StartGame(id, "Cian");
            client.FinishZoning();
            Chat(client, ".tree");
            var state = TreeState.Read(client.Expect(PrivateOpcode.TreeState));
            Assert.Equal(new[] { MightPath, MightPath + 1, MightPath + 2, CleavingFury }, state.Owned);
            Assert.Equal(0, state.PointsFree);
            Assert.Contains(state.Summary, l => l.StartsWith("Warrior skills: +20% damage"));
        }
    }

    [Fact]
    public void Keystones_and_percentages_change_the_sheet()
    {
        if (!File.Exists(BalancePath)) return;
        var balance = Balance.Load(BalancePath);
        CharacterSheet Sheet(TreeBonuses tree) => new(
            new CH.Persistence.CharacterRecord(1, 1, "Test", 1, 0, 0, 0, 0, 0, 0, 0, 1f, 0, Warrior, 100, 2, 0, 0, 0, 0),
            new CH.Persistence.CharacterProgress(100, 0, 0, -1, -1, "", true), [], [(1, 0)], balance, tree);
        TreeBonuses With(Dictionary<string, double> stats, params string[] keystones) => new(stats, [], keystones.ToHashSet());

        var plain = Sheet(TreeBonuses.None);
        var tough = Sheet(With(new() { ["healthPct"] = 10, ["str"] = 15 }));
        Assert.Equal((int)(plain.MaxHealth * 1.1), tough.MaxHealth);
        Assert.Equal(plain.CombinedStrength + 15, tough.CombinedStrength);
        Assert.Equal(plain.Strength, tough.Strength);

        var assassin = Sheet(With(new() { ["critChance"] = 5 }, PassiveTree.AssassinsMark));
        Assert.Equal((int)(plain.MaxHealth * 0.75), assassin.MaxHealth);
        Assert.Equal(0.2, assassin.CritChance, 6);
        Assert.Equal(2.0, assassin.CritMultiplier, 6);
        Assert.Equal((0.05, 1.5), (plain.CritChance, plain.CritMultiplier));

        var runner = Sheet(With(new() { ["attackSpeedPct"] = 5 }, PassiveTree.Windrunner));
        Assert.Equal((30, 25), (runner.AttackSpeedPercent, runner.RunSpeedPercent));
    }

    private static void Chat(CH.FakeClient.FakeClient c, string text) =>
        c.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);

    private static string NextSystemLine(CH.FakeClient.FakeClient c) => c.Expect(Opcode.SimpleMessageForThePlayer).ReadString();

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
