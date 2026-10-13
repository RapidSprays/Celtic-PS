using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Client = CH.FakeClient.FakeClient;

namespace CH.Tests;

/// <summary>The platinum item shop, factions, bounty boards and the auction house.</summary>
public sealed class EconomyTests : IDisposable
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis", "extracted", "24928510", "balance-client.json");

    private static readonly string WorldPath = Path.Combine(Path.GetDirectoryName(BalancePath)!, "world.json");

    private static readonly Lazy<Balance> Data = new(() => Balance.Load(BalancePath));

    private readonly string _dir = Directory.CreateTempSubdirectory("ch-economy-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Task<ServerHost> Start() => ServerHost.StartAsync(new ServerOptions
    {
        HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = BalancePath,
    }, NullLoggerFactory.Instance);

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

    private static InventoryWithMoney Inventory(Client c) => InventoryWithMoney.Read(c.Expect(Opcode.InventoryUpdate));

    [Fact]
    public async Task The_item_shop_sells_for_platinum_and_bags_of_gold_turn_into_gold()
    {
        if (!File.Exists(BalancePath)) return;
        await using var host = await Start();
        var (c, _) = Join(host, "shopper", "Brigid");
        using var _c = c;
        Say(c, ".platinum 20");
        Assert.Equal(20, ReadPlatinum(c.Expect(Opcode.CurrencyInfo)));

        c.Send(Opcode.ItemShop, b => b.WriteVariableInt32(0));
        var catalogue = c.Expect(Opcode.ItemShop);
        Assert.Equal(1, catalogue.ReadVariableInt32());
        Assert.Equal(1, catalogue.ReadByte());
        var stock = new Dictionary<int, int>();
        for (int i = 0, n = catalogue.ReadVariableInt32(); i < n; i++)
        {
            var id = catalogue.ReadVariableInt32();
            catalogue.ReadVariableInt32(); catalogue.ReadVariableInt32();
            stock[id] = catalogue.ReadVariableInt32();
            catalogue.ReadString(); catalogue.ReadString(); catalogue.ReadVariableInt32();
        }
        Assert.Equal(8, stock[51]); // 100 Bags of Gold

        c.Send(Opcode.ItemShop, b => { b.WriteVariableInt32(2); b.WriteVariableInt32(51); b.WriteVariableInt32(1); });
        var bought = c.Expect(Opcode.ItemShop);
        Assert.Equal(3, bought.ReadVariableInt32());
        Assert.Equal(1, bought.ReadByte());
        bought.ReadString();
        Assert.Equal(12, bought.ReadVariableInt32());
        var inventory = InventoryWithMoney.Read(bought);
        Assert.True(inventory.Coins >= 10_000);
        Assert.DoesNotContain(inventory.Items, i => i.TemplateId == 14); // applied, never stored

        // Too dear: refused, nothing taken.
        c.Send(Opcode.ItemShop, b => { b.WriteVariableInt32(2); b.WriteVariableInt32(3); b.WriteVariableInt32(1); });
        var first = c.Expect(Opcode.ItemShop);
        first.ReadVariableInt32();
        Assert.Equal(0, first.ReadByte());
        first.ReadString();
        Assert.Equal(12, first.ReadVariableInt32());

        // An Auction House Slot (30 platinum) after topping up: one more listing slot.
        Say(c, ".platinum 100");
        c.Expect(Opcode.CurrencyInfo);
        c.Send(Opcode.ItemShop, b => { b.WriteVariableInt32(2); b.WriteVariableInt32(3); b.WriteVariableInt32(1); });
        var slot = c.Expect(Opcode.ItemShop);
        slot.ReadVariableInt32();
        Assert.Equal(1, slot.ReadByte());
        c.Send(Opcode.AuctionHouse, b => { b.WriteVariableInt32(5); b.WriteVariableInt32(0); b.WriteVariableInt32(0); b.WriteVariableInt32(30); });
        var mine = c.Expect(Opcode.AuctionHouse);
        Assert.Equal(11, mine.ReadVariableInt32());
        Assert.Equal(0, mine.ReadVariableInt32());
        Assert.Equal(0, mine.ReadVariableInt32());
        Assert.Equal(0, mine.ReadVariableInt32()); // used
        Assert.Equal(1, mine.ReadVariableInt32()); // extra
    }

    private static int ReadPlatinum(NetIncomingMessage m)
    {
        m.ReadByte();
        return m.ReadVariableInt32();
    }

    [Fact]
    public async Task Factions_are_sent_at_login_and_levels_match_the_client()
    {
        if (!File.Exists(BalancePath)) return;
        var liches = Data.Value.Factions[4];
        Assert.Equal(0, liches.LevelAt(0)!.Value);
        Assert.Equal(-1, liches.LevelAt(-1)!.Value);
        Assert.Equal(-2, liches.LevelAt(-3000)!.Value);
        Assert.Equal(1, liches.LevelAt(2500)!.Value);
        Assert.Equal(4, liches.LevelAt(95_000)!.Value);

        var rules = FactionRules.Load(new ServerOptions().FactionsPath);
        var quest = new QuestInfo(3002, "The Call of Lichdom", 24, 150, 1, [], true, false, null, []);
        Assert.Equal([(4, 1500), (5, -750)], rules.ForQuest(quest).ToArray());

        await using var host = await Start();
        var (c, _) = Join(host, "factions", "Ailbhe");
        using var _c = c;
        var m = c.Expect(Opcode.FactionsMessage);
        Assert.Equal(1u, m.ReadVariableUInt32());
        var ids = new List<int>();
        for (int i = 0, n = m.ReadVariableInt32(); i < n; i++)
        {
            ids.Add(m.ReadVariableInt32());
            Assert.Equal(0, m.ReadVariableInt32());
        }
        Assert.Equal([4, 5, 6], ids);
    }

    private sealed record Board(int MaxConcurrent, List<(string Name, int Quest, int Cost, int Status)> Bounties);

    private static Board ReadBoard(Client c)
    {
        var m = c.Expect(Opcode.RecServerDynamicData);
        m.ReadByte();
        var parts = m.ReadString().Split('~', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("BB-DATA", parts[0]);
        Assert.Equal("217", parts[1]);
        return new Board(int.Parse(parts[3]), parts.Skip(7).Select(p => p.Split('^')).Select(p => (p[0], int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]))).ToList());
    }

    [Fact]
    public async Task The_bounty_board_offers_a_daily_set_one_at_a_time_and_sells_more_for_pledges()
    {
        if (!File.Exists(BalancePath) || !File.Exists(WorldPath)) return;
        await using var host = await Start();
        var (c, _) = Join(host, "hunter", "Fionn");
        using var _c = c;
        Say(c, ".level 60");
        void Request(string what) => c.Send(Opcode.ReqServerDynamicData, b => b.Write(what));

        Request("BB-DATA");
        var board = ReadBoard(c);
        Assert.Equal(5, board.Bounties.Count);
        Assert.Equal(3, board.Bounties.Count(b => b.Status == 2 && b.Cost == 0));
        Assert.Equal(2, board.Bounties.Count(b => b.Status == 1 && b.Cost == 1));
        Assert.All(board.Bounties, b => Assert.StartsWith("[Bounty]", b.Name));

        // Accepting is a normal quest start; the board comes back with it started. A second one must wait.
        var (first, second) = (board.Bounties[0].Quest, board.Bounties[1].Quest);
        c.Send(Opcode.QuestStart, b => b.WriteVariableInt32(first));
        Assert.Equal(1, c.Expect(Opcode.QuestStartReply).ReadByte());
        Assert.Equal(3, ReadBoard(c).Bounties.Single(b => b.Quest == first).Status);
        c.Send(Opcode.QuestStart, b => b.WriteVariableInt32(second));
        Assert.Equal(0, c.Expect(Opcode.QuestStartReply).ReadByte());

        // Abandon it at the board: dropped for the day, and the next one can start.
        c.Send(Opcode.BountyBoard, b => { b.Write((byte)0); b.WriteVariableInt32(1); b.WriteVariableInt32(first); });
        Assert.Equal(6, ReadBoard(c).Bounties.Single(b => b.Quest == first).Status);

        // "Get More Bounties": without a pledge nothing changes; with one, the first paid bounty opens.
        Request("BB-BUY");
        Assert.Equal(2, ReadBoard(c).Bounties.Count(b => b.Status == 1));
        Say(c, ".give 217 1");
        Inventory(c);
        Request("BB-BUY");
        var after = ReadBoard(c);
        Assert.Equal(1, after.Bounties.Count(b => b.Status == 1));
        Assert.Contains(after.Bounties, b => b.Cost == 1 && b.Status == 2);
    }

    private static void Listing(Client c, int template, int inventoryId, int quantity, int startBid, int buyout) =>
        c.Send(Opcode.AuctionHouse, b =>
        {
            b.WriteVariableInt32(1);
            b.WriteVariableInt32(template);
            b.WriteVariableInt32(inventoryId);
            b.WriteVariableInt32(quantity);
            b.WriteVariableInt32(0);
            b.WriteVariableInt32(0); // 12 hours
            b.WriteVariableInt32(0);
            b.WriteVariableInt32(startBid);
            b.WriteVariableInt32(buyout);
        });

    private static (int Type, string Text) AuctionText(Client c)
    {
        var m = c.Expect(Opcode.AuctionHouse);
        return (m.ReadVariableInt32(), m.ReadString());
    }

    [Fact]
    public async Task An_item_listed_at_the_auction_house_is_bid_on_and_bought_out()
    {
        if (!File.Exists(BalancePath)) return;
        var goods = Data.Value.Items.Values.First(i => i.Subtype == "Cooking" && i.Tradeable && i.Stackable && !i.ImportantItem && !i.IsPlaceholder);
        await using var host = await Start();
        var (seller, _) = Join(host, "seller", "Eithne");
        var (buyer, _) = Join(host, "buyer", "Lorcan");
        using var _s = seller;
        using var _b = buyer;
        Say(seller, ".gold 1000");
        var sellerGold = Inventory(seller).Coins;
        Say(buyer, ".gold 5000");
        var buyerGold = Inventory(buyer).Coins;
        Say(seller, $".give {goods.Id} 3");
        var held = Inventory(seller).Items.Single(i => i.TemplateId == goods.Id);

        Listing(seller, goods.Id, held.ServerInventoryId, 3, 1000, 500); // buyout below the start: refused
        Assert.Equal(15, AuctionText(seller).Type);
        Listing(seller, goods.Id, held.ServerInventoryId, 3, 1000, 4000);
        var afterListing = Inventory(seller);
        Assert.Equal(sellerGold - 50, afterListing.Coins); // 50 gold minimum deposit
        Assert.DoesNotContain(afterListing.Items, i => i.TemplateId == goods.Id);
        Assert.Equal(0, AuctionText(seller).Type);

        buyer.Send(Opcode.AuctionHouse, b =>
        {
            b.WriteVariableInt32(4);
            b.WriteVariableInt32(0);
            b.Write(goods.Name.Split(' ')[0]);
            b.WriteVariableInt32(7);
            b.WriteVariableInt32(1);
            b.WriteVariableInt32(-1);
            b.WriteVariableInt32(-1);
            b.WriteVariableInt32(1);
            b.WriteVariableInt32(20);
        });
        var found = buyer.Expect(Opcode.AuctionHouse);
        Assert.Equal(10, found.ReadVariableInt32());
        Assert.Equal(1, found.ReadVariableInt32());
        Assert.Equal(1, found.ReadVariableInt32());
        var listingId = found.ReadVariableInt32();
        Assert.Equal(goods.Id, found.ReadVariableInt32());
        Assert.Equal(3, found.ReadVariableInt32());
        Assert.True(TimeSpan.Parse(found.ReadString()) > TimeSpan.FromHours(11));
        Assert.Equal("Eithne", found.ReadString());
        Assert.Equal(1000, found.ReadVariableInt32());
        Assert.Equal("NONE", found.ReadString());
        Assert.Equal(4000, found.ReadVariableInt32());

        void Bid(int amount) => buyer.Send(Opcode.AuctionHouse, b => { b.WriteVariableInt32(2); b.WriteVariableInt32(listingId); b.WriteVariableInt32(0); b.WriteVariableInt32(amount); });
        Bid(999);
        Assert.Equal(14, AuctionText(buyer).Type);
        Bid(1000);
        Assert.Equal(buyerGold - 1000, Inventory(buyer).Coins); // the bid is held
        Assert.Equal(5, AuctionText(buyer).Type);

        buyer.Send(Opcode.AuctionHouse, b => { b.WriteVariableInt32(3); b.WriteVariableInt32(listingId); b.WriteVariableInt32(0); });
        var bought = Inventory(buyer);
        Assert.Equal(buyerGold - 4000, bought.Coins); // bid returned, buyout paid
        Assert.Equal(3, bought.Items.Where(i => i.TemplateId == goods.Id).Sum(i => i.Quantity));
        Assert.Equal(6, AuctionText(buyer).Type);
        Assert.Equal(sellerGold + 3800, Inventory(seller).Coins); // 95% of the price, and the deposit back
        Assert.Equal(9, AuctionText(seller).Type);
    }
}
