using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using CH.Server.Game;
using Microsoft.Extensions.Logging.Abstractions;

namespace CH.Tests;

/// <summary>Gear regeneration read from item descriptions, and Sigils of Health and Energy.</summary>
public sealed class RegenTests : IAsyncDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private static readonly Lazy<Balance> Data = new(() => Balance.Load(BalancePath));

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-regen-").FullName;

    [Theory]
    [InlineData("This bronze amulet regenerates the wearer's health by five points per tick, even in combat.", 5, 0, false)]
    [InlineData("This dazzling amulet regenerates both the wearer's health and energy by twenty points per tick, even in combat.", 20, 20, false)]
    [InlineData("Increase your health regeneration by 65 and your energy regeneration by 45 per tick.", 65, 45, false)]
    [InlineData("This amulet gives 45 health and 30 energy regen and grants the skill 'starstone shield' which protects the wearer from up to 400 damage for 15 seconds.", 45, 30, false)]
    [InlineData("It increases your movement speed by 70%, grants 60 energy regeneration per tick and can drain the lifeforce from your target, stealing up to 500 health.", 0, 60, false)]
    [InlineData("It plays a rousing tune increasing your out of combat health regeneration by 15 points per tick.", 15, 0, true)]
    [InlineData("This magical off hand focus regenerates ten energy and ten health per tick when equipped, even in combat.", 10, 10, false)]
    [InlineData("These polar wings increase your movement speed by 70%, health and energy regen by 50 and can unleash arctic gale dealing up to 500 cold damage.", 50, 50, false)]
    [InlineData("It draws upon the good will of Yule to replenish good spirits, increasing your energy regeneration when out of combat.", 0, GearRegen.UnstatedAmount, true)]
    [InlineData("While equipped, your enduring guard skill will restore 50 health, in addition to its normal effect.", 0, 0, false)]
    [InlineData("It grants the skill \"Necral Vigor\" which restores 1000 health to yourself or an ally.", 0, 0, false)]
    [InlineData("Wearing the armlet increases the health restored by your bandage wounds skill by 150.", 0, 0, false)]
    public void Regen_is_read_from_descriptions(string description, int health, int energy, bool outOfCombat)
    {
        var r = GearRegen.Parse(description);
        Assert.Equal((health, energy), (r.Health, r.Energy));
        if (health + energy > 0) Assert.Equal(outOfCombat, r.OutOfCombatOnly);
    }

    [Fact]
    public void Over_a_thousand_equippable_items_give_regen()
    {
        if (!File.Exists(BalancePath)) return;
        var regen = Data.Value.Items.Values.Where(i => i.Slot >= 0).Select(GearRegen.Of).Count(r => r.Any);
        Assert.InRange(regen, 1050, 1200);
        Assert.Equal(new Regen(100, 0, false), GearRegen.Of(Data.Value.Items[56679])); // Vital Perceptor Pendant: "gives 100 health regen"
        Assert.Equal(new Regen(25, 25, false), GearRegen.Of(Data.Value.Items[56687])); // Keen Perceptor Ring
    }

    [Fact]
    public async Task Sigils_are_applied_counted_and_kept_and_regen_gear_shows_its_buff()
    {
        if (!File.Exists(BalancePath)) return;
        var options = new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath };
        uint id;
        await using (var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance))
        {
            using var client = new CH.FakeClient.FakeClient();
            client.Connect("127.0.0.1", host.Game.Port);
            client.VersionCheck();
            client.Login("sigils");
            id = client.CreateCharacter("Brigid", @class: 2).CharacterId;
            client.StartGame(id, "Brigid");
            client.FinishZoning();

            // A crate opens into five Sigils of Health, applied to the character (not the backpack).
            Chat(client, ".give Sigils of Health Crate");
            var crate = InventoryWithMoney.Read(client.Expect(Opcode.InventoryUpdate)).Items.Single(i => i.TemplateId == 34781);
            client.Send(Opcode.UseItem, new UseItemRequest(crate.ServerInventoryId, TargetType.Self, 0).Write);
            Assert.Equal((0, 5), ReadSigils(client.Expect(Opcode.SigilsCount)));

            Chat(client, ".give Sigil of Energy 3");
            Assert.Equal((3, 5), ReadSigils(client.Expect(Opcode.SigilsCount)));

            // The stats page asks for the counts.
            client.Send(Opcode.SigilsCount, _ => { });
            Assert.Equal((3, 5), ReadSigils(client.Expect(Opcode.SigilsCount)));

            // Heroic Spirit Amulet: 20 health and 20 energy per tick, shown as the permanent regen-boost effects at +20.
            Chat(client, ".wear Heroic Spirit Amulet");
            List<(int Id, int Level)> effects;
            do
            {
                var m = client.Expect(Opcode.StatusEffectUpdate, TimeSpan.FromSeconds(5));
                effects = [];
                for (int i = 0, n = m.ReadVariableInt32(); i < n; i++) { effects.Add((m.ReadVariableInt32(), m.ReadVariableInt32())); m.ReadVariableInt32(); m.ReadFloat(); m.ReadFloat(); }
            } while (!effects.Any(e => e.Id == Sigils.GearHealthRegenEffect));
            Assert.Contains((Sigils.GearHealthRegenEffect, 3), effects); // level 3 = +20
            Assert.Contains((Sigils.GearEnergyRegenEffect, 3), effects);
        }

        // Sigils survive a restart.
        await using (var host = await ServerHost.StartAsync(options, NullLoggerFactory.Instance))
        {
            using var client = new CH.FakeClient.FakeClient();
            client.Connect("127.0.0.1", host.Game.Port);
            client.VersionCheck();
            client.Login("sigils");
            client.StartGame(id, "Brigid");
            client.FinishZoning();
            client.Send(Opcode.SigilsCount, _ => { });
            Assert.Equal((3, 5), ReadSigils(client.Expect(Opcode.SigilsCount)));
        }
    }

    [Fact]
    public async Task Gear_regen_ticks_in_combat()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("ticker");
        var id = client.CreateCharacter("Lorcan", @class: 1).CharacterId;
        client.StartGame(id, "Lorcan");
        client.FinishZoning();

        // Pummel at rank 49 costs 137 energy, so one cast leaves room for regen to show.
        Chat(client, ".level 100");
        client.Expect(Opcode.LevelUp);
        client.Send(Opcode.BuySkill, b => { b.WriteVariableInt32(1); b.WriteVariableInt32(49); });
        client.Expect(Opcode.BuySkillReply);
        Chat(client, ".wear Heroic Spirit Amulet"); // 20 health and 20 energy per tick, even in combat
        client.Expect(Opcode.EquipItemReply);
        Chat(client, ".spawn Scrawny Coast Wolf 1");
        var wolf = client.Expect(Opcode.ZoneMonsterAppeared).ReadVariableUInt32();
        client.Send(Opcode.UseSkillOnNPC, new UseSkill(TargetType.Mob, wolf, Lidgren.Network.NetTime.Now, 1).Write);
        client.Expect(Opcode.SkillUpdate, TimeSpan.FromSeconds(5));

        // The energy after the cast, then the next regen tick (3 s, still in combat): +1 base +20 from the amulet.
        int? NextEnergy()
        {
            var m = client.Expect(Opcode.StatsUpdate, TimeSpan.FromSeconds(5));
            if (m.ReadByte() != 0) return null; // a full stats block
            int? energy = null;
            for (int i = 0, n = m.ReadVariableInt32(); i < n; i++)
            {
                var key = (StatKey)m.ReadVariableInt32();
                var value = m.ReadVariableInt32();
                if (key == StatKey.Energy) energy = value;
            }
            return energy;
        }
        int? afterCast;
        while ((afterCast = NextEnergy()) is null) { }
        int? ticked;
        while ((ticked = NextEnergy()) is null) { }
        Assert.Equal(afterCast + 21, ticked);
    }

    [Fact]
    public void A_sheet_adds_up_regen_from_everything_equipped()
    {
        if (!File.Exists(BalancePath)) return;
        var items = new List<CH.Persistence.ItemRecord>
        {
            new(1, 1, 56679, 1, (int)EquipSlot.Neck, false, 0, false),  // Vital Perceptor Pendant: 100 health
            new(2, 1, 56687, 1, (int)EquipSlot.RingR1, false, 0, false), // Keen Perceptor Ring: 25 health, 25 energy
        };
        var sheet = new CharacterSheet(new CH.Persistence.CharacterRecord(1, 1, "Test", 1, 0, 0, 0, 0, 0, 0, 0, 1f, 0, 1, 200, 2, 0, 0, 0, 0),
            new CH.Persistence.CharacterProgress(200, 0, 0, -1, -1, "", true), items, [], Data.Value);
        Assert.Equal((125, 25, 0, 0), sheet.GearRegenPerTick);
    }

    private static (int Energy, int Health) ReadSigils(Lidgren.Network.NetIncomingMessage m) => (m.ReadVariableInt32(), m.ReadVariableInt32());

    private static void Chat(CH.FakeClient.FakeClient c, string text) =>
        c.Send(Opcode.GeneralChat, new ChatMessage(ChatChannel.Local, "", text).WriteFromClient);

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
