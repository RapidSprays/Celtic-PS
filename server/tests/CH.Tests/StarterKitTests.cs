using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace CH.Tests;

/// <summary>
/// Starter kit and inventory against the real client balance data (~/CelticHeroesAnalysis/extracted/24928510). The data
/// is never in the repo, so without it these tests pass without checking anything.
/// </summary>
public sealed class StarterKitTests : IAsyncDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-kit-").FullName;

    [Fact]
    public async Task Warrior_gets_class_gear_skills_potions_and_books_and_can_use_them()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath }, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        client.VersionCheck();
        client.Login("kit");
        var created = client.CreateCharacter("Conall", race: 1, @class: 1);

        var start = client.StartGame(created.CharacterId, "Conall");
        Assert.Equal(CommandStatus.Accepted, start.Status);
        Assert.Equal([1, 47, 2, 3], start.Skills.Select(k => k.SkillId));
        Assert.All(start.Skills, k => Assert.Equal((0, 4), (k.Level, k.MaxLevel)));
        Assert.Equal(10000, start.Equipment[0].TemplateId); // Tempered Small Sword
        Assert.Equal(10005, start.Equipment[5].TemplateId); // Training Buckler
        Assert.Equal(10000, start.Character.Equipment[0]);   // appearance uses template ids
        Assert.Equal(-1, start.Character.Equipment[1]);
        Assert.Contains(start.Inventory, i => i.TemplateId == 10040 && i.Quantity == 5);
        var books = start.Inventory.Where(i => i.TemplateId is > 1000 and < 1057).ToList();
        Assert.Equal(2, books.Count); // two class skill books for skills the kit doesn't already teach
        var book = books[0];
        Assert.Equal(50, start.Coins);
        Assert.Equal(2, start.HudSets.Count);
        Assert.Equal(new IdValue(1, 1), start.HudSets[0].Slots[0]);

        // Full stats follow StartGameReply (the client drops most of them from the reply itself).
        var stats = CharacterStats.Read(client.Expect(Opcode.StatsUpdate));
        Assert.Equal((9, 17, 2012), (stats.WeaponDamage, stats.ArmourValue, stats.AttackSpeed));
        client.Expect(Opcode.GameDataMessage);

        // Learn a skill from a book.
        client.Send(Opcode.UseItem, new UseItemRequest(book.ServerInventoryId, TargetType.Self, 0).Write);
        Assert.Equal((byte)CommandStatus.Accepted, client.Expect(Opcode.UseItemReply).ReadByte());
        var learned = client.Expect(Opcode.BuySkillReply);
        learned.ReadVariableInt32();
        Assert.Equal(book.TemplateId, learned.ReadVariableInt32());
        Assert.Equal(5, learned.ReadVariableInt32()); // 4 starter skills + 1

        // Unequip the sword: attack speed falls back to unarmed.
        var sword = start.Equipment[0];
        client.Send(Opcode.EquipItem, new EquipItemRequest(false, sword.TemplateId, sword.ServerInventoryId, 1, 0).Write);
        Assert.Equal((byte)CommandStatus.Accepted, client.Expect(Opcode.EquipItemReply).ReadByte());
        Assert.Equal(1000, CharacterStats.Read(client.Expect(Opcode.StatsUpdate)).AttackSpeed);

        // A potion can't be equipped.
        var potion = start.Inventory.First(i => i.TemplateId == 10040);
        client.Send(Opcode.EquipItem, new EquipItemRequest(true, potion.TemplateId, potion.ServerInventoryId, 1, 0).Write);
        Assert.Equal((byte)CommandStatus.Rejected, client.Expect(Opcode.EquipItemReply).ReadByte());

        // Drinking a potion uses one up.
        client.Send(Opcode.UseItem, new UseItemRequest(potion.ServerInventoryId, TargetType.Self, 0).Write);
        var used = client.Expect(Opcode.UseItemReply);
        Assert.Equal((byte)CommandStatus.Accepted, used.ReadByte());
        Assert.Equal(10f, used.ReadFloat());
        used.ReadVariableInt32(); // coins
        var backpack = Enumerable.Range(0, used.ReadVariableInt32()).Select(_ => ItemSlot.Read(used)).ToList();
        Assert.Equal(4, backpack.Single(i => i.TemplateId == 10040).Quantity);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
