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
        Assert.InRange(count, 80u, 130u); // ~60 monsters plus Lir's Reach's generated NPCs
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

    private async Task<(ServerHost Host, CH.FakeClient.FakeClient Client, StartGameReply Start)> EnterAs(string user, string name, int @class)
    {
        var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = Path.Combine(_dir, user), BalancePath = BalancePath },
            NullLoggerFactory.Instance);
        var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login(user);
        var created = client.CreateCharacter(name, @class: @class);
        var start = client.StartGame(created.CharacterId, name);
        client.Send(Opcode.FinishedZoning, _ => { });
        client.Expect(Opcode.AllZoningMessagesSent);
        return (host, client, start);
    }

    private static (List<(int Id, int Level, int Seconds)> Effects, CharacterStats Stats) ReadEffects(NetIncomingMessage m)
    {
        var effects = new List<(int, int, int)>();
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++)
        {
            effects.Add((m.ReadVariableInt32(), m.ReadVariableInt32(), m.ReadVariableInt32()));
            m.ReadFloat();
            m.ReadFloat();
        }
        return (effects, CharacterStats.Read(m));
    }

    [Fact]
    public async Task Hide_shows_as_a_status_effect_keeps_mobs_off_and_breaks_when_attacking()
    {
        if (!File.Exists(BalancePath)) return;
        var (host, client, _) = await EnterAs("rogue", "Sionnach", @class: 5);
        await using var _h = host;
        using var _c = client;

        client.Send(Opcode.UseSkillOnNPC, new UseSkill(TargetType.Self, 0, NetTime.Now, 17).Write); // Hide
        var (effects, _) = ReadEffects(client.Expect(Opcode.StatusEffectUpdate, TimeSpan.FromSeconds(5)));
        var hide = Assert.Single(effects);
        Assert.Equal(9, hide.Id);
        Assert.InRange(hide.Seconds, 40, 45);

        // A 2-star (aggressive) level 4 Beastwalker right next to us doesn't notice a hidden rogue.
        Chat(client, ".spawn Beastwalker 4");
        var mobId = client.Expect(Opcode.ZoneMonsterAppeared).ReadVariableUInt32();
        Assert.Throws<TimeoutException>(() => client.Expect(Opcode.AttackState, TimeSpan.FromSeconds(2)));

        // Attacking breaks Hide: the effect list empties.
        client.Send(Opcode.StartAttackingNPC, new StartAttacking(TargetType.Mob, NetTime.Now, mobId).Write);
        var (after, _) = ReadEffects(client.Expect(Opcode.StatusEffectUpdate, TimeSpan.FromSeconds(3)));
        Assert.Empty(after);
    }

    [Fact]
    public async Task Protective_stance_raises_armour_and_gm_commands_change_level_and_give_items()
    {
        if (!File.Exists(BalancePath)) return;
        var (host, client, start) = await EnterAs("warrior", "Cathal", @class: 1);
        await using var _h = host;
        using var _c = client;
        var baseArmour = start.Stats.ArmourValue;

        client.Send(Opcode.UseSkillOnNPC, new UseSkill(TargetType.Self, 0, NetTime.Now, 2).Write); // Protective Stance
        var (effects, stats) = ReadEffects(client.Expect(Opcode.StatusEffectUpdate, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Assert.Single(effects).Id);
        Assert.True(stats.ArmourValue > baseArmour + 10, $"armour {stats.ArmourValue} should be well above {baseArmour}");

        Chat(client, ".give minor health potion 3");
        var inventory = client.Expect(Opcode.InventoryUpdate);
        inventory.ReadVariableInt32();
        var items = Enumerable.Range(0, inventory.ReadVariableInt32()).Select(_ => ItemSlot.Read(inventory)).ToList();
        Assert.Equal(8, items.Single(i => i.TemplateId == 10040).Quantity); // 5 from the kit + 3

        Chat(client, ".level +5");
        var up = client.Expect(Opcode.LevelUp);
        up.ReadVariableInt32();
        Assert.Equal(6, up.ReadVariableInt32());
        Chat(client, ".level -3");
        client.Expect(Opcode.LevelUp);
        Chat(client, ".xp 100000");
        var xpUp = client.Expect(Opcode.LevelUp);
        xpUp.ReadVariableInt32();
        Assert.True(xpUp.ReadVariableInt32() > 3);
    }

    private static List<SkillEntry> ReadSkillList(NetIncomingMessage m, out int points)
    {
        points = m.ReadVariableInt32();
        var skills = new List<SkillEntry>();
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++)
            skills.Add(new SkillEntry(m.ReadVariableInt32(), m.ReadVariableInt32(), m.ReadVariableInt32(), m.ReadFloat()));
        return skills;
    }

    [Fact]
    public async Task Levelling_grants_attribute_and_skill_points_that_can_be_spent()
    {
        if (!File.Exists(BalancePath)) return;
        var (host, client, start) = await EnterAs("leveller", "Aodhan", @class: 1);
        await using var _h = host;
        using var _c = client;
        Assert.Equal((0, 0), (start.Stats.SkillPoints, start.Stats.AttributePoints));

        Chat(client, ".level 20");
        client.Expect(Opcode.LevelUp);
        client.Expect(Opcode.BaseStats);
        var stats = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
        Assert.Equal((19, 57), (stats.SkillPoints, stats.AttributePoints));
        var skills = ReadSkillList(client.Expect(Opcode.SkillListUpdate), out var skillPoints);
        Assert.Equal(19, skillPoints);
        Assert.Equal(9, skills.Single(k => k.SkillId == 1).MaxLevel); // ranks 5-9 open at level 15

        // +5 strength, +3 vitality: more attack and health, 49 points left.
        client.Send(Opcode.AttributesUpdate, b => { b.WriteVariableUInt32(5); b.WriteVariableUInt32(0); b.WriteVariableUInt32(0); b.WriteVariableUInt32(3); b.WriteVariableUInt32(49); });
        var baseStats = client.Expect(Opcode.BaseStats);
        Assert.Equal(13, baseStats.ReadVariableInt32()); // vitality 10 + 3
        Assert.Equal(10, baseStats.ReadVariableInt32()); // strength 5 + 5
        var after = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
        Assert.Equal(49, after.AttributePoints);
        Assert.Equal(stats.MaxHealth + 18, after.MaxHealth);
        Assert.Equal(stats.Attack + 5, after.Attack);

        // Spending more than you have is refused.
        client.Send(Opcode.AttributesUpdate, b => { b.WriteVariableUInt32(100); b.WriteVariableUInt32(0); b.WriteVariableUInt32(0); b.WriteVariableUInt32(0); b.WriteVariableUInt32(0); });
        Assert.Contains("49 attribute points", NextSystemLine(client));

        // Rank Pummel up once, then several ranks in one purchase.
        client.Send(Opcode.BuySkill, b => { b.WriteVariableInt32(1); b.WriteVariableInt32(1); });
        var bought = client.Expect(Opcode.BuySkillReply);
        var points = bought.ReadVariableInt32();
        Assert.Equal(-1, bought.ReadVariableInt32());
        var list = Enumerable.Range(0, bought.ReadVariableInt32()).Select(_ => new SkillEntry(bought.ReadVariableInt32(), bought.ReadVariableInt32(), bought.ReadVariableInt32(), bought.ReadFloat())).ToList();
        Assert.Equal(18, points);
        Assert.Equal(1, list.Single(k => k.SkillId == 1).Level);
        client.Send(Opcode.BuySkill, b => { b.WriteVariableInt32(1); b.WriteVariableInt32(3); });
        var jumped = client.Expect(Opcode.BuySkillReply);
        Assert.Equal(16, jumped.ReadVariableInt32());
        jumped.ReadVariableInt32();
        var after3 = Enumerable.Range(0, jumped.ReadVariableInt32()).Select(_ => new SkillEntry(jumped.ReadVariableInt32(), jumped.ReadVariableInt32(), jumped.ReadVariableInt32(), jumped.ReadFloat())).ToList();
        Assert.Equal(3, after3.Single(k => k.SkillId == 1).Level);
    }

    private static float SpeedFromEffects(CH.FakeClient.FakeClient c)
    {
        var m = c.Expect(Opcode.StatusEffectUpdate, TimeSpan.FromSeconds(5));
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) { m.ReadVariableInt32(); m.ReadVariableInt32(); m.ReadVariableInt32(); m.ReadFloat(); m.ReadFloat(); }
        return CharacterStats.Read(m).MaxSpeed;
    }

    private static (List<ItemSlot> Equipment, List<ItemSlot> Backpack) ReadEquipReply(NetIncomingMessage m)
    {
        Assert.Equal((byte)CommandStatus.Accepted, m.ReadByte());
        var equipment = Enumerable.Range(0, 25).Select(_ => ItemSlot.Read(m)).ToList();
        var backpack = Enumerable.Range(0, m.ReadVariableInt32()).Select(_ => ItemSlot.Read(m)).ToList();
        return (equipment, backpack);
    }

    [Fact]
    public async Task Rogue_gear_requirements_blocked_slots_gearset_and_equip_skills()
    {
        if (!File.Exists(BalancePath)) return;
        var (host, client, start) = await EnterAs("gear", "Scathach", @class: 5);
        await using var _h = host;
        using var _c = client;

        // A level-225 dagger is refused at level 1 (by hand; .wear is a GM override).
        Chat(client, ".give 72443");
        var inv = client.Expect(Opcode.InventoryUpdate);
        inv.ReadVariableInt32();
        var dagger = Enumerable.Range(0, inv.ReadVariableInt32()).Select(_ => ItemSlot.Read(inv)).Single(i => i.TemplateId == 72443);
        client.Send(Opcode.EquipItem, new EquipItemRequest(true, 72443, dagger.ServerInventoryId, 1, 0).Write);
        var refused = client.Expect(Opcode.EquipItemReply);
        Assert.Equal((byte)CommandStatus.Rejected, refused.ReadByte());
        Assert.Contains("225 level", refused.ReadString());

        // A two-handed staff blocks the offhand: wearing it bumps the offhand item, and a buckler bumps the staff back.
        Chat(client, ".wear 10054"); // Buckler
        Assert.Equal(10054, ReadEquipReply(client.Expect(Opcode.EquipItemReply)).Equipment[5].TemplateId);
        Chat(client, ".wear 10052"); // Short Staff
        var staff = ReadEquipReply(client.Expect(Opcode.EquipItemReply));
        Assert.Equal((10052, -1), (staff.Equipment[0].TemplateId, staff.Equipment[5].TemplateId));
        var buckler = staff.Backpack.First(i => i.TemplateId == 10054);
        client.Send(Opcode.EquipItem, new EquipItemRequest(true, 10054, buckler.ServerInventoryId, 1, 5).Write);
        var bumped = ReadEquipReply(client.Expect(Opcode.EquipItemReply));
        Assert.Equal(10054, bumped.Equipment[5].TemplateId);
        Assert.NotEqual(10052, bumped.Equipment[0].TemplateId);

        // .gearset 225 dresses a level-225 rogue: dagger, leather, rings and bangles, and the stats follow the gear.
        Chat(client, ".level 225");
        client.Expect(Opcode.LevelUp);
        var before = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
        Chat(client, ".gearset");
        var kit = ReadEquipReply(client.Expect(Opcode.EquipItemReply)).Equipment;
        Assert.All(new[] { 0, 1, 2, 3, 4, 6, 8, 9, 10, 11, 12, 13, 14 }, slot => Assert.True(kit[slot].TemplateId > 0, $"slot {slot} empty"));
        var after = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
        Assert.True(after.Dexterity > before.Dexterity + 100, $"dexterity {before.Dexterity} -> {after.Dexterity}");
        Assert.True(after.ArmourValue > before.ArmourValue && after.WeaponDamage > before.WeaponDamage);

        // The Condemned Dagger of Slaughter grants Virulent Sunder (20062) while worn.
        Chat(client, ".wear 72443");
        client.Expect(Opcode.EquipItemReply);
        client.Expect(Opcode.StatsUpdate);
        var skills = ReadSkillList(client.Expect(Opcode.SkillListUpdate), out _);
        Assert.Contains(skills, k => k.SkillId == 20062);
    }

    [Fact]
    public async Task Mounting_fills_slot_22_speeds_you_up_and_redraws_the_model()
    {
        if (!File.Exists(BalancePath)) return;
        var (host, client, start) = await EnterAs("rider", "Medb", @class: 5);
        await using var _h = host;
        using var _c = client;
        Assert.Equal(5f, start.Stats.MaxSpeed);

        Chat(client, ".mount wolf");
        var equipped = ReadEquipReply(client.Expect(Opcode.EquipItemReply)).Equipment;
        Assert.True(equipped[22].TemplateId > 0);
        client.Expect(Opcode.AppearanceOnlyUpdate); // what makes the client build the mount
        // Riding speed comes with the "Mounted" status effect (at the mount's own rate), which carries the stats.
        Assert.True(SpeedFromEffects(client) > 5f);

        Chat(client, ".mount off");
        Assert.Equal(-1, ReadEquipReply(client.Expect(Opcode.EquipItemReply)).Equipment[22].TemplateId);
        Assert.Equal(5f, SpeedFromEffects(client));

        // Resists from a level-225 kit reach the stats block as client BonusType ids (0-8 resists).
        Chat(client, ".level 225");
        client.Expect(Opcode.LevelUp);
        Chat(client, ".gearset");
        client.Expect(Opcode.EquipItemReply);
        var stats = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
        Assert.Contains(stats.Bonuses, b => b.Id is >= 0 and <= 8 && b.Value != 0);
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
