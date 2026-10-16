using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Client = CH.FakeClient.FakeClient;

namespace CH.Tests;

/// <summary>Skills on other players, party and area skills, ability progression, monster tagging and specials.</summary>
public sealed class CombatGapsTests : IDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private static readonly Lazy<Balance> Data = new(() => Balance.Load(BalancePath));

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-combatgaps-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const uint ShieldOfBark = 36, NaturesTouch = 5, NaturesBreath = 124, Firestorm = 126;

    private static (Client Client, uint Id) Join(ServerHost host, string user, string name, int @class)
    {
        var client = new Client();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login(user);
        var id = client.CreateCharacter(name, @class: @class).CharacterId;
        client.StartGame(id, name);
        client.FinishZoning();
        return (client, id);
    }

    private static void Say(Client c, string text) => c.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);

    /// <summary>The next message of a kind that matches, within the time limit.</summary>
    private static T? Await<T>(Client c, Opcode opcode, Func<NetIncomingMessage, T?> read, double seconds = 8) where T : class
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

    private static void Cast(Client c, uint skill, TargetType type, uint target) =>
        c.Send(Opcode.UseSkillOnNPC, new UseSkill(type, target, NetTime.Now, skill).Write);

    [Fact]
    public async Task A_druid_buffs_and_heals_another_player_and_heals_the_party()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        var (druid, druidId) = Join(host, "druid", "Saoirse", @class: 2);
        var (warrior, warriorId) = Join(host, "warrior", "Conall", @class: 1);
        using var _d = druid;
        using var _w = warrior;
        foreach (var skill in new[] { ShieldOfBark, NaturesTouch, NaturesBreath }) Say(druid, $".learn {skill}");

        // Shield of Bark on the warrior: the effect lands on him.
        Cast(druid, ShieldOfBark, TargetType.OtherPlayer, warriorId);
        var shield = Data.Value.Skills[(int)ShieldOfBark].StatusEffectId;
        Assert.NotNull(Await(warrior, Opcode.StatusEffectUpdate, m =>
        {
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++)
            {
                var id = m.ReadVariableInt32(); m.ReadVariableInt32(); m.ReadVariableInt32(); m.ReadFloat(); m.ReadFloat();
                if (id == shield) return "found";
            }
            return null;
        }));

        // Natures Touch on the warrior: a heal from the druid to him.
        Cast(druid, NaturesTouch, TargetType.OtherPlayer, warriorId);
        bool HealFromDruid(NetIncomingMessage m) => CombatDamage.Read(m).Entries.Any(e => e.CasterId == (int)druidId && e.TargetId == (int)warriorId && e.Damage < 0);
        Assert.NotNull(Await(warrior, Opcode.CombatDamageMessage, m => HealFromDruid(m) ? "healed" : null));

        // Natures Breath is a group heal: once they're in a party it reaches the warrior too.
        druid.Send(Opcode.PartyMessage, m => { m.WriteVariableInt32(0); m.WriteVariableInt32((int)warriorId); });
        warrior.Expect(Opcode.PartyMessage);
        warrior.Send(Opcode.PartyMessage, m => { m.WriteVariableInt32(2); m.WriteVariableInt32((int)druidId); m.WriteVariableInt32(1); });
        druid.Expect(Opcode.PartyMessage);
        Cast(druid, NaturesBreath, TargetType.Self, druidId);
        Assert.NotNull(Await(warrior, Opcode.CombatDamageMessage, m => HealFromDruid(m) ? "healed" : null));

        // Defensive Formation (a group buff) from the warrior: the druid gets the effect, icon and all.
        Say(warrior, ".learn 227");
        Cast(warrior, 227, TargetType.Self, warriorId);
        var formation = Data.Value.Skills[227].StatusEffectId;
        Assert.NotNull(Await(druid, Opcode.StatusEffectUpdate, m =>
        {
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++)
            {
                var id = m.ReadVariableInt32(); m.ReadVariableInt32(); m.ReadVariableInt32(); m.ReadFloat(); m.ReadFloat();
                if (id == formation) return "found";
            }
            return null;
        }));
    }

    [Fact]
    public async Task Firestorm_hits_every_monster_around_its_target_and_the_caster_owns_them()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        var (mage, _) = Join(host, "mage", "Niamh", @class: 3);
        var (other, _) = Join(host, "watcher", "Ronan", @class: 1);
        using var _m = mage;
        using var _o = other;
        Say(mage, ".level 30");
        Say(mage, $".learn {Firestorm}");
        var wolves = new List<uint>();
        for (var i = 0; i < 3; i++)
        {
            Say(mage, ".spawn Scrawny Coast Wolf 30");
            wolves.Add(mage.Expect(Opcode.ZoneMonsterAppeared).ReadVariableUInt32());
        }
        Cast(mage, Firestorm, TargetType.Mob, wolves[0]);
        // Damage to all three wolves, and the caster owning (3) the first one; the other player sees it locked (2).
        var hit = new HashSet<int>();
        int? ownerSees = null;
        var until = DateTime.UtcNow.AddSeconds(8);
        while ((hit.Count < 3 || ownerSees is null) && DateTime.UtcNow < until)
        {
            var (op, m) = mage.ExpectAny([Opcode.CombatDamageMessage, Opcode.CombatEntityLocked], until - DateTime.UtcNow);
            if (op == Opcode.CombatDamageMessage)
                foreach (var e in CombatDamage.Read(m).Entries.Where(e => e.Kind == AttackKind.Skill && e.SkillId == (int)Firestorm)) hit.Add(e.TargetId);
            else if (m.ReadVariableInt32() == 1 && m.ReadVariableInt32() == (int)wolves[0]) ownerSees = m.ReadVariableInt32();
        }
        Assert.Equal(wolves.Select(w => (int)w).ToHashSet(), hit);
        Assert.Equal(3, ownerSees);
        var otherSees = Await(other, Opcode.CombatEntityLocked, m => m.ReadVariableInt32() == 1 && m.ReadVariableInt32() == (int)wolves[0] ? new Box<int>(m.ReadVariableInt32()) : null);
        Assert.Equal(2, otherSees?.Value);
    }

    [Fact]
    public async Task Fighting_raises_abilities()
    {
        if (!File.Exists(BalancePath)) return;
        // Every landed auto-attack trains (the real rate is 1%, server/content/ability-gain.json).
        await using var host = await ServerHost.StartAsync(new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath, CombatAbilityChance = 1 }, NullLoggerFactory.Instance);
        var (warrior, _) = Join(host, "fighter", "Fiach", @class: 1);
        using var _w = warrior;
        Say(warrior, ".level 40");
        Say(warrior, ".spawn Scrawny Coast Wolf 40");
        var wolf = warrior.Expect(Opcode.ZoneMonsterAppeared).ReadVariableUInt32();
        warrior.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.Mob, NetTime.Now, wolf).Write);
        var update = Await(warrior, Opcode.AbilityUpdate, m => new Box<(int, int)>((m.ReadVariableInt32(), m.ReadVariableInt32())), seconds: 30);
        Assert.NotNull(update);
        Assert.Equal(2, update!.Value.Item2); // up from level 1
        // Auto-attacks train the weapon's ability (or Critical Strike on a crit); Melee Combat comes from skills.
        Assert.Contains(update.Value.Item1, new[] { 0, 1, 2, GameServer.CriticalStrike, GameServer.HandToHand });
    }

    [Fact]
    public void Skill_ranks_carry_area_and_charge_protection()
    {
        if (!File.Exists(BalancePath)) return;
        Assert.Equal(6, Data.Value.Skills[(int)Firestorm].AreaOfEffect);
        Assert.InRange(Data.Value.Skills[(int)NaturesTouch].Levels[0].ChargingProtection, 0, 100);
    }

    private sealed class Box<T>(T value)
    {
        public T Value { get; } = value;
    }
}
