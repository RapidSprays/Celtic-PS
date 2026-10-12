using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// NPC merchants (CH_ShopNetworkHandler; docs/economy-notes.md). A conversation's SHOP action opens the shop with
/// RequestShop; buying and selling are answered with a refreshed RequestShopReply. Stock and prices: Game/Shops.cs.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>How close the player must be to an NPC of the template that opened the shop.</summary>
    private const float ShopRange = 25f;

    private void OnShopMessage(Session s, Opcode opcode, NetIncomingMessage msg)
    {
        var npcTemplate = msg.ReadVariableInt32();
        var shopId = msg.ReadVariableInt32();
        switch (opcode)
        {
            case Opcode.RequestShop:
                SendShop(s, npcTemplate, shopId, ReadTemplateList(msg), header: true);
                break;
            case Opcode.PurchaseItem:
            {
                var template = msg.ReadVariableInt32();
                var quantity = msg.ReadVariableInt32();
                var listed = ReadTemplateList(msg);
                var problem = NearMerchant(s, npcTemplate) ? Buy(s, shopId, template, quantity) : "You are too far from the merchant.";
                SendShop(s, npcTemplate, shopId, listed, header: false, problem);
                break;
            }
            case Opcode.SellItem:
            {
                var template = msg.ReadVariableInt32();
                var itemId = msg.ReadVariableInt32();
                var quantity = msg.ReadVariableInt32();
                var listed = ReadTemplateList(msg);
                var problem = NearMerchant(s, npcTemplate) ? Sell(s, template, itemId, quantity) : "You are too far from the merchant.";
                SendShop(s, npcTemplate, shopId, listed, header: false, problem);
                break;
            }
        }
    }

    private static List<int> ReadTemplateList(NetIncomingMessage msg)
    {
        var list = new List<int>();
        for (int i = 0, n = msg.ReadVariableInt32(); i < n; i++) list.Add(msg.ReadVariableInt32());
        return list;
    }

    private bool NearMerchant(Session s, int npcTemplate) =>
        _zones.TryGetValue(s.Position.Zone, out var zone) && zone.Mobs.Values.Any(m => m.IsNpc && m.Template.Id == npcTemplate
            && Dist2D(m.HomeX, m.HomeZ, s.Position.X, s.Position.Z) <= ShopRange);

    private string? Buy(Session s, int shopId, int template, int quantity)
    {
        if (!_shops.Stock(shopId).Contains(template) || !_balance.Items.TryGetValue(template, out var item)) return "That isn't for sale here.";
        quantity = Math.Clamp(quantity, 1, item.Stackable ? 100 : 10);
        var cost = _shops.BuyPrice(item) * quantity;
        var coins = _store.GetProgress(s.Character!.Id).Coins;
        if (cost > coins) return "You don't have enough gold.";
        _store.AddCurrency(s.Character.Id, -cost, 0);
        GrantItem(s.Character.Id, template, quantity);
        _log.LogInformation("{Name} bought {Qty} × {Item} for {Cost}", s.Name, quantity, item.Name, cost);
        return null;
    }

    private string? Sell(Session s, int template, int itemId, int quantity)
    {
        var record = _store.ListItems(s.Character!.Id).FirstOrDefault(i => i.Id == itemId && i.Slot == ItemRecord.Backpack && i.TemplateId == template);
        if (record is null || !_balance.Items.TryGetValue(template, out var item)) return "That item isn't in your backpack.";
        quantity = Math.Clamp(quantity, 1, record.Quantity);
        var paid = _shops.SellPrice(item) * quantity;
        _store.SetItemQuantity(record.Id, record.Quantity - quantity);
        _store.AddCurrency(s.Character.Id, paid, 0);
        _log.LogInformation("{Name} sold {Qty} × {Item} for {Paid}", s.Name, quantity, item.Name, paid);
        return null;
    }

    /// <summary>
    /// RequestShopReply (18003): inventory, the shop's stock, and buy/sell prices for the stock and for everything
    /// the client listed (its own templates, so the sell tab has prices).
    /// </summary>
    private void SendShop(Session s, int npcTemplate, int shopId, IReadOnlyList<int> listed, bool header, string? problem = null)
    {
        if (problem is not null) Tell(s, problem);
        var sheet = RefreshSheet(s);
        var stock = _shops.Stock(shopId);
        Send(s, Opcode.RequestShopReply, b =>
        {
            b.Write((byte)1);
            b.Write((byte)(header ? 1 : 0));
            if (header)
            {
                b.WriteVariableInt32(npcTemplate);
                b.WriteVariableInt32(shopId);
            }
            sheet.InventoryWithMoney().Write(b);
            b.WriteVariableInt32(stock.Count);
            for (var i = 0; i < stock.Count; i++)
            {
                b.WriteVariableInt32(i + 1);
                b.WriteVariableInt32(stock[i]);
                b.WriteVariableInt32(999);
            }
            var priced = stock.Concat(listed).Concat(sheet.Backpack.Select(i => i.TemplateId)).Distinct()
                .Select(t => _balance.Items.GetValueOrDefault(t)).Where(i => i is not null).ToList();
            b.WriteVariableInt32(priced.Count);
            foreach (var item in priced)
            {
                b.WriteVariableInt32(item!.Id);
                b.WriteVariableInt32(stock.Contains(item.Id) ? _shops.BuyPrice(item) : 0);
                b.WriteVariableInt32(_shops.SellPrice(item));
            }
        });
    }
}
