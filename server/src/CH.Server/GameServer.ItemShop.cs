using System.Text.Json;
using CH.Protocol;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// The platinum item shop (ItemShop 18001, CH_ItemShopNetworkHandler; docs/economy-notes.md). The catalogue is our own
/// (server/content/item-shop.json); there is no real-money store, so PlatinumBundleInfo offers nothing to buy with cash.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Auction listings a character may have at once before buying Auction House Slots (invented).</summary>
    public const int AuctionFreeSlots = 5;

    public sealed record ShopStock(int Id, int Template, int Quantity, int Price, int Category);

    public static IReadOnlyList<ShopStock> LoadItemShop(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.GetProperty("stock").EnumerateArray().Select(e => new ShopStock(e.GetProperty("id").GetInt32(),
            e.GetProperty("template").GetInt32(), e.GetProperty("quantity").GetInt32(), e.GetProperty("price").GetInt32(), e.GetProperty("category").GetInt32())).ToList();
    }

    /// <summary>The catalogue entries whose items the client knows (an unknown template crashes its shop screen).</summary>
    private IEnumerable<ShopStock> ShopStockItems => _itemShop.Where(s => _balance.Items.ContainsKey(s.Template) && s.Price > 0 && s.Quantity > 0);

    private void OnItemShop(Session s, NetIncomingMessage msg)
    {
        switch (msg.ReadVariableInt32())
        {
            case 0: // catalogue: vi 1, u8 status, vi n, n × (vi stockId, vi template, vi qty, vi price, str name, str description, vi category)
            {
                var stock = ShopStockItems.ToList();
                Send(s, Opcode.ItemShop, b =>
                {
                    b.WriteVariableInt32(1);
                    b.Write((byte)1);
                    b.WriteVariableInt32(stock.Count);
                    foreach (var item in stock)
                    {
                        var info = _balance.Items[item.Template];
                        b.WriteVariableInt32(item.Id);
                        b.WriteVariableInt32(item.Template);
                        b.WriteVariableInt32(item.Quantity);
                        b.WriteVariableInt32(item.Price);
                        b.Write(item.Quantity > 1 ? $"{item.Quantity} × {info.Name}" : info.Name);
                        b.Write(info.Description);
                        b.WriteVariableInt32(item.Category);
                    }
                });
                break;
            }
            case 2: // buy: vi stockId, vi count → vi 3, u8 success, str text, vi platinum, [InventoryWithMoney]
            {
                var stockId = msg.ReadVariableInt32();
                var count = msg.ReadVariableInt32();
                var item = ShopStockItems.FirstOrDefault(x => x.Id == stockId);
                var platinum = _store.GetProgress(s.Character!.Id).Platinum;
                count = item is not null && _balance.Items[item.Template].Stackable ? Math.Clamp(count, 1, 100) : 1;
                var cost = item is null ? 0 : (long)item.Price * count;
                var problem = item is null ? "That isn't for sale." : cost > platinum ? "You don't have enough platinum." : null;
                if (problem is null)
                {
                    _store.AddCurrency(s.Character.Id, 0, -(int)cost);
                    GrantItem(s.Character.Id, item!.Template, item.Quantity * count);
                    _log.LogInformation("{Name} bought {Count} × stock {Stock} ({Item}) for {Cost} platinum", s.Name, count, item.Id, _balance.Items[item.Template].Name, cost);
                }
                var sheet = RefreshSheet(s);
                Send(s, Opcode.ItemShop, b =>
                {
                    b.WriteVariableInt32(3);
                    b.Write((byte)(problem is null ? 1 : 0));
                    b.Write(problem ?? $"You bought {(count * item!.Quantity > 1 ? $"{count * item.Quantity} × " : "")}{_balance.Items[item.Template].Name}.");
                    b.WriteVariableInt32(sheet.Progress.Platinum);
                    if (problem is null) sheet.InventoryWithMoney().Write(b);
                });
                SendPlatinum(s);
                break;
            }
        }
    }

    private void SendPlatinum(Session s) =>
        Send(s, Opcode.CurrencyInfo, b => { b.Write((byte)1); b.WriteVariableInt32(_store.GetProgress(s.Character!.Id).Platinum); });

    /// <summary>PlatinumBundleInfo (19005): no cash bundles, no buy-platinum buttons, no special offers.</summary>
    private void SendNoCashOffers(Session s) => Send(s, Opcode.PlatinumBundleInfo, b =>
    {
        b.WriteVariableInt32(0);
        b.Write((byte)0);
        b.WriteVariableInt32(0);
    });

    /// <summary>Bags of Gold (100 gold each) and Auction House Slots are applied when received, never stored.</summary>
    private bool TryGrantCurrencyItem(long characterId, int templateId, int quantity)
    {
        const int BagOfGold = 14, AuctionHouseSlot = 93;
        if (templateId == BagOfGold)
        {
            _store.AddCurrency(characterId, 100 * quantity, 0);
            return true;
        }
        if (templateId == AuctionHouseSlot)
        {
            _store.AddAuctionSlots(characterId, quantity);
            if (Online.FirstOrDefault(p => p.Character?.Id == characterId) is { } s)
                Tell(s, $"{quantity} Auction House Slot{(quantity == 1 ? "" : "s")} added: you can list {AuctionFreeSlots + _store.GetAuctionSlots(characterId)} items at once.");
            return true;
        }
        return false;
    }
}
