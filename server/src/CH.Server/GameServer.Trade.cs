using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Player-to-player trade (TradeMessage 17013, CH_TradeNetworkHandler; docs/economy-notes.md). Offers live on the
/// server; every change resets both "ready" ticks; the swap happens when both confirm, in one database transaction,
/// after checking again that every offered item and coin is still there.
/// </summary>
public sealed partial class GameServer
{
    private const float TradeRange = 30f;

    private enum TradeOp
    {
        Request = 0,          // C→S {otherId, byte isReply}
        RequestFromServer = 1, // S→C {requesterId, byte silent}
        Cancel = 2,           // C→S {otherId}
        Cancelled = 3,        // S→C {otherId}
        Offer = 4,            // C→S {byte isMoney; item: byte toTrade, templateId, invId, qty | money: coins}
        Screen = 5,           // S→C {byte status [string]; 3 × InventoryWithMoney}
        Ready = 6,            // C→S {byte}
        OtherReady = 7,       // S→C {byte}
        Complete = 8,         // S→C {3 × InventoryWithMoney}
        Confirm = 9,          // C→S {otherId}
        Open = 10,            // S→C {otherId}
    }

    private sealed class TradeSide
    {
        public Dictionary<long, int> Items { get; } = new(); // item row id → quantity offered
        public int Coins { get; set; }
        public bool Ready { get; set; }
        public bool Confirmed { get; set; }
    }

    private sealed class Trade
    {
        public required Session A { get; init; }
        public required Session B { get; init; }
        public TradeSide SideA { get; } = new();
        public TradeSide SideB { get; } = new();

        public Session Other(Session s) => ReferenceEquals(s, A) ? B : A;
        public TradeSide Side(Session s) => ReferenceEquals(s, A) ? SideA : SideB;
    }

    private void OnTradeMessage(Session s, NetIncomingMessage msg)
    {
        switch ((TradeOp)msg.ReadVariableInt32())
        {
            case TradeOp.Request:
                var otherId = msg.ReadVariableInt32();
                var isReply = msg.ReadByte() == 1;
                if (isReply) AcceptTrade(s, otherId);
                else RequestTrade(s, otherId);
                break;
            case TradeOp.Cancel:
                if (s.Trade is { } trade) CancelTrade(trade, s);
                break;
            case TradeOp.Offer:
                ChangeOffer(s, msg);
                break;
            case TradeOp.Ready:
                SetTradeReady(s, msg.ReadByte() == 1);
                break;
            case TradeOp.Confirm:
                ConfirmTrade(s);
                break;
        }
    }

    private void RequestTrade(Session s, int otherId)
    {
        var other = OnlineByCharacter(otherId);
        string? problem = other is null ? "That player is not online."
            : ReferenceEquals(other, s) ? "You can't trade with yourself."
            : s.Trade is not null || other.Trade is not null ? "One of you is already trading."
            : !other.InZone || other.Position.Zone != s.Position.Zone
              || Dist2D(s.Position.X, s.Position.Z, other.Position.X, other.Position.Z) > TradeRange ? $"{other.Name} is too far away to trade."
            : null;
        if (problem is not null)
        {
            Tell(s, problem);
            return;
        }
        other!.TradeRequests.Add(s.CharacterId);
        Send(other, Opcode.TradeMessage, b =>
        {
            b.WriteVariableInt32((int)TradeOp.RequestFromServer);
            b.WriteVariableInt32(s.CharacterId);
            b.Write((byte)0);
        });
    }

    private void AcceptTrade(Session s, int requesterId)
    {
        if (!s.TradeRequests.Remove(requesterId) || OnlineByCharacter(requesterId) is not { } requester) return;
        if (s.Trade is not null || requester.Trade is not null || requester.Position.Zone != s.Position.Zone)
        {
            Tell(s, "That trade is no longer possible.");
            return;
        }
        var trade = new Trade { A = requester, B = s };
        requester.Trade = s.Trade = trade;
        foreach (var p in new[] { requester, s })
            Send(p, Opcode.TradeMessage, b =>
            {
                b.WriteVariableInt32((int)TradeOp.Open);
                b.WriteVariableInt32(trade.Other(p).CharacterId);
            });
        SendTradeScreens(trade, null, null);
    }

    private void CancelTrade(Trade trade, Session? by)
    {
        foreach (var p in new[] { trade.A, trade.B })
        {
            p.Trade = null;
            if (ReferenceEquals(p, by) || p.Character is null) continue;
            Send(p, Opcode.TradeMessage, b =>
            {
                b.WriteVariableInt32((int)TradeOp.Cancelled);
                b.WriteVariableInt32(trade.Other(p).CharacterId);
            });
        }
    }

    private void ChangeOffer(Session s, NetIncomingMessage msg)
    {
        if (s.Trade is not { } trade) return;
        var side = trade.Side(s);
        string? problem = null;
        if (msg.ReadByte() == 1)
        {
            var coins = msg.ReadVariableInt32();
            side.Coins = Math.Clamp(coins, 0, _store.GetProgress(s.Character!.Id).Coins);
        }
        else
        {
            var toTrade = msg.ReadByte() == 1;
            msg.ReadVariableInt32(); // template id: the server trusts its own record
            var itemId = (long)msg.ReadVariableInt32();
            var quantity = msg.ReadVariableInt32();
            var item = _store.ListItems(s.Character!.Id).FirstOrDefault(i => i.Id == itemId && i.Slot == ItemRecord.Backpack);
            if (!toTrade) side.Items.Remove(itemId);
            else if (item is null) problem = "That item isn't in your backpack.";
            else if (_balance.Items.TryGetValue(item.TemplateId, out var info) && (!info.Tradeable || info.ImportantItem) || item.Bound)
                problem = "That item can't be traded.";
            else side.Items[itemId] = Math.Clamp(side.Items.GetValueOrDefault(itemId) + Math.Max(1, quantity), 1, item.Quantity);
        }
        trade.SideA.Ready = trade.SideB.Ready = false;
        trade.SideA.Confirmed = trade.SideB.Confirmed = false;
        SendTradeScreens(trade, s, problem);
    }

    private void SetTradeReady(Session s, bool ready)
    {
        if (s.Trade is not { } trade) return;
        trade.Side(s).Ready = ready;
        if (!ready) trade.SideA.Confirmed = trade.SideB.Confirmed = false;
        Send(trade.Other(s), Opcode.TradeMessage, b =>
        {
            b.WriteVariableInt32((int)TradeOp.OtherReady);
            b.Write((byte)(ready ? 1 : 0));
        });
        TryCompleteTrade(trade, s);
    }

    /// <summary>The two players' ready and confirm messages arrive in any order: complete once all four are in.</summary>
    private void ConfirmTrade(Session s)
    {
        if (s.Trade is not { } trade) return;
        trade.Side(s).Confirmed = true;
        TryCompleteTrade(trade, s);
    }

    private void TryCompleteTrade(Trade trade, Session s)
    {
        if (!trade.SideA.Ready || !trade.SideB.Ready || !trade.SideA.Confirmed || !trade.SideB.Confirmed) return;

        var (a, b) = (trade.A, trade.B);
        var itemsA = _store.ListItems(a.Character!.Id).ToDictionary(i => i.Id);
        var itemsB = _store.ListItems(b.Character!.Id).ToDictionary(i => i.Id);
        var coinsA = _store.GetProgress(a.Character.Id).Coins;
        var coinsB = _store.GetProgress(b.Character.Id).Coins;
        bool Valid(TradeSide side, Dictionary<long, ItemRecord> items, int coins) =>
            side.Coins <= coins && side.Items.All(o => items.TryGetValue(o.Key, out var i) && i.Slot == ItemRecord.Backpack && i.Quantity >= o.Value);
        if (!Valid(trade.SideA, itemsA, coinsA) || !Valid(trade.SideB, itemsB, coinsB))
        {
            trade.SideA.Ready = trade.SideB.Ready = trade.SideA.Confirmed = trade.SideB.Confirmed = false;
            SendTradeScreens(trade, s, "Something in the trade changed. Check the offers again.");
            return;
        }

        _store.Atomically(() =>
        {
            Move(trade.SideA, itemsA, b);
            Move(trade.SideB, itemsB, a);
            _store.AddCurrency(a.Character.Id, trade.SideB.Coins - trade.SideA.Coins, 0);
            _store.AddCurrency(b.Character.Id, trade.SideA.Coins - trade.SideB.Coins, 0);
        });
        _log.LogInformation("Trade {A} ⇄ {B}: {ItemsA} items + {CoinsA} coins for {ItemsB} items + {CoinsB} coins",
            a.Name, b.Name, trade.SideA.Items.Count, trade.SideA.Coins, trade.SideB.Items.Count, trade.SideB.Coins);
        a.Trade = b.Trade = null;
        foreach (var p in new[] { a, b })
        {
            var sheet = RefreshSheet(p);
            Send(p, Opcode.TradeMessage, w =>
            {
                w.WriteVariableInt32((int)TradeOp.Complete);
                sheet.InventoryWithMoney().Write(w);
                new InventoryWithMoney(0, []).Write(w);
                new InventoryWithMoney(0, []).Write(w);
            });
        }

        void Move(TradeSide side, Dictionary<long, ItemRecord> items, Session to)
        {
            foreach (var (itemId, quantity) in side.Items)
            {
                var item = items[itemId];
                if (quantity >= item.Quantity && !(_balance.Items.GetValueOrDefault(item.TemplateId)?.Stackable ?? false))
                    _store.GiveItem(item.Id, to.Character!.Id);
                else
                {
                    _store.SetItemQuantity(item.Id, item.Quantity - quantity);
                    GrantItem(to.Character!.Id, item.TemplateId, quantity);
                }
            }
        }
    }

    /// <summary>Trade screen (5) for both players: own backpack minus own offer, own offer, the other's offer.</summary>
    private void SendTradeScreens(Trade trade, Session? problemFor, string? problem)
    {
        foreach (var p in new[] { trade.A, trade.B })
        {
            var mine = Offer(p, trade.Side(p), out var remaining);
            var theirs = Offer(trade.Other(p), trade.Side(trade.Other(p)), out _);
            var error = ReferenceEquals(p, problemFor) ? problem : null;
            Send(p, Opcode.TradeMessage, b =>
            {
                b.WriteVariableInt32((int)TradeOp.Screen);
                b.Write((byte)(error is null ? 1 : 0));
                if (error is not null) b.Write(error);
                remaining.Write(b);
                mine.Write(b);
                theirs.Write(b);
            });
        }
    }

    /// <summary>A side's offer as an inventory, and what's left in that player's backpack.</summary>
    private InventoryWithMoney Offer(Session p, TradeSide side, out InventoryWithMoney remaining)
    {
        var items = _store.ListItems(p.Character!.Id).Where(i => i.Slot == ItemRecord.Backpack).ToList();
        var coins = _store.GetProgress(p.Character.Id).Coins;
        side.Coins = Math.Min(side.Coins, coins);
        foreach (var gone in side.Items.Keys.Where(id => items.All(i => i.Id != id)).ToList()) side.Items.Remove(gone);
        var offered = items.Where(i => side.Items.ContainsKey(i.Id))
            .Select(i => CharacterSheet.ToWire(i with { Quantity = Math.Min(i.Quantity, side.Items[i.Id]) })).ToList();
        var left = items.Select(i => i with { Quantity = i.Quantity - side.Items.GetValueOrDefault(i.Id) })
            .Where(i => i.Quantity > 0).Select(CharacterSheet.ToWire).ToList();
        remaining = new InventoryWithMoney(coins - side.Coins, left);
        return new InventoryWithMoney(side.Coins, offered);
    }
}
