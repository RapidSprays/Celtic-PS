using CH.Persistence;
using CH.Protocol;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// The auction house (AuctionHouse 23003, AuctionHouseNetworkHandler; docs/economy-notes.md). The client's numbers
/// come from its auction_house_params text asset and the auctioneer's own words: 12/24/48 hour listings with a 1/2/4%
/// deposit (at least 50 gold, returned unless the seller cancels), bids at least 5% up, a 5% sale fee, 5 free listing
/// slots plus bought Auction House Slots up to 30. Storage, search, expiry and delivery are ours: the listed item is
/// taken from the seller, a bidder's gold is held until they're outbid, and items and gold go straight into the
/// backpack (there is no collect message), online or not.
/// </summary>
public sealed partial class GameServer
{
    private const int AuctionMaxSlots = 30, AuctionMinDeposit = 50, AuctionMaxBid = 999_999_999;
    private const double AuctionMinRaise = 1.05, AuctionSaleShare = 0.95;
    private static readonly (int Hours, double Deposit)[] AuctionDurations = [(12, 0.01), (24, 0.02), (48, 0.04)];

    // AHServerMessageType
    private const int AhCreated = 0, AhCancelledBidder = 1, AhCancelledSeller = 2, AhExpired = 4, AhBidPlaced = 5, AhWonBuyout = 6,
        AhWonCompletion = 7, AhCompleted = 8, AhBoughtOut = 9, AhQuery = 10, AhMyListings = 11, AhMyBids = 12, AhFailedCancel = 13,
        AhFailedBid = 14, AhFailedListing = 15, AhFailedQuery = 16, AhOutBid = 17;

    private double _nextAuctionCheck;

    private static long UnixNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void OnAuctionHouse(Session s, NetIncomingMessage msg)
    {
        var c = s.Character!;
        var op = msg.ReadVariableInt32();
        switch (op)
        {
            case 0:
                CancelAuction(s, msg.ReadVariableInt32());
                break;
            case 1:
            {
                var template = msg.ReadVariableInt32();
                var inventoryId = msg.ReadVariableInt32();
                var quantity = msg.ReadVariableInt32();
                msg.ReadVariableInt32(); // remaining charges: the server's own record counts
                var duration = msg.ReadVariableInt32();
                msg.ReadVariableInt32(); // character id
                var startBid = msg.ReadVariableInt32();
                var buyout = msg.ReadVariableInt32();
                CreateAuction(s, template, inventoryId, quantity, duration, startBid, buyout);
                break;
            }
            case 2:
            {
                var id = msg.ReadVariableInt32();
                msg.ReadVariableInt32();
                Bid(s, id, msg.ReadVariableInt32());
                break;
            }
            case 3:
            {
                var id = msg.ReadVariableInt32();
                Buyout(s, id);
                break;
            }
            case 4 or 6:
            {
                var filter = msg.ReadVariableInt32();
                var query = msg.ReadString();
                var sort = msg.ReadVariableInt32();
                var dir = msg.ReadVariableInt32();
                var minLevel = msg.ReadVariableInt32();
                var maxLevel = msg.ReadVariableInt32();
                var page = msg.ReadVariableInt32();
                var perPage = msg.ReadVariableInt32();
                var found = SearchAuctions(op == 6 ? _store.ListAuctions().Where(a => a.BidderId == c.Id) : _store.ListAuctions(), filter, query, minLevel, maxLevel);
                SendListings(s, op == 6 ? AhMyBids : AhQuery, Sorted(found, sort, dir).ToList(), page, perPage);
                break;
            }
            case 5:
            {
                msg.ReadVariableInt32();
                var page = msg.ReadVariableInt32();
                var perPage = msg.ReadVariableInt32();
                SendListings(s, AhMyListings, _store.ListAuctions().Where(a => a.SellerId == c.Id).ToList(), page, perPage);
                break;
            }
        }
    }

    private int AuctionSlots(long characterId) => Math.Min(AuctionMaxSlots, AuctionFreeSlots + _store.GetAuctionSlots(characterId));

    private void CreateAuction(Session s, int template, int inventoryId, int quantity, int duration, int startBid, int buyout)
    {
        var c = s.Character!;
        var item = Sheet(s).Backpack.FirstOrDefault(i => i.Id == inventoryId && i.TemplateId == template);
        var info = _balance.Items.GetValueOrDefault(template);
        var mine = _store.ListAuctions().Count(a => a.SellerId == c.Id);
        var (hours, rate) = AuctionDurations[Math.Clamp(duration, 0, AuctionDurations.Length - 1)];
        var deposit = Math.Max(AuctionMinDeposit, (int)Math.Round(startBid * rate));
        var problem = item is null || quantity < 1 || quantity > item.Quantity ? "You don't have that item."
            : info is null || !info.Tradeable || info.ImportantItem || item.Bound ? "That item can't be sold."
            : mine >= AuctionSlots(c.Id) ? "All your auction slots are in use."
            : startBid < 1 || startBid > AuctionMaxBid || (buyout > 0 && buyout <= startBid) ? "That price isn't allowed."
            : _store.GetProgress(c.Id).Coins < deposit ? $"You need {deposit:N0} gold for the deposit."
            : null;
        if (problem is not null)
        {
            AuctionText(s, AhFailedListing, problem);
            return;
        }
        _store.SetItemQuantity(item!.Id, item.Quantity - quantity);
        _store.AddCurrency(c.Id, -deposit, 0);
        var id = _store.AddAuction(new AuctionRecord(0, c.Id, c.Name, template, quantity, item.Charges, startBid, buyout > 0 ? buyout : -1, 0, null, null,
            deposit, UnixNow + hours * 3600L));
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        AuctionText(s, AhCreated, $"Your {ItemLabel(template, quantity)} is up for auction for {hours} hours (deposit {deposit:N0} gold).");
        _log.LogInformation("{Name} listed auction {Id}: {Item} from {Bid} (buyout {Buyout})", s.Name, id, ItemLabel(template, quantity), startBid, buyout);
    }

    /// <summary>The lowest acceptable next bid (AuctionHouseNetworkHandler.GetMinimumNextBid), capped at the buyout.</summary>
    private static int MinimumBid(AuctionRecord a)
    {
        var min = a.BidderId is null ? a.StartBid : (int)Math.Min(AuctionMaxBid, Math.Round(a.Bid * AuctionMinRaise));
        if (a.BidderId is not null && min == a.Bid) min++;
        return a.Buyout > 0 ? Math.Min(min, a.Buyout) : min;
    }

    private void Bid(Session s, int auctionId, int amount)
    {
        var c = s.Character!;
        var a = _store.ListAuctions().FirstOrDefault(x => x.Id == auctionId);
        var problem = a is null ? "That auction has ended." : a.SellerId == c.Id ? "You can't bid on your own auction."
            : a.BidderId == c.Id ? "You already have the highest bid." : amount < MinimumBid(a) ? $"The lowest bid now is {MinimumBid(a):N0} gold."
            : _store.GetProgress(c.Id).Coins < amount ? "You don't have that much gold." : null;
        if (problem is not null)
        {
            AuctionText(s, AhFailedBid, problem);
            return;
        }
        if (a!.Buyout > 0 && amount >= a.Buyout)
        {
            Buyout(s, auctionId);
            return;
        }
        _store.AddCurrency(c.Id, -amount, 0);
        RefundBidder(a, AhOutBid, $"You've been outbid on {ItemLabel(a.TemplateId, a.Quantity)}; your {a.Bid:N0} gold is back.");
        _store.SetAuctionBid(a.Id, c.Id, amount);
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        AuctionText(s, AhBidPlaced, $"You bid {amount:N0} gold on {ItemLabel(a.TemplateId, a.Quantity)}.");
    }

    private void Buyout(Session s, int auctionId)
    {
        var c = s.Character!;
        var a = _store.ListAuctions().FirstOrDefault(x => x.Id == auctionId);
        var problem = a is null ? "That auction has ended." : a.Buyout <= 0 ? "That auction has no buyout price."
            : a.SellerId == c.Id ? "You can't buy your own auction." : _store.GetProgress(c.Id).Coins < a.Buyout ? "You don't have that much gold." : null;
        if (problem is not null)
        {
            AuctionText(s, AhFailedBid, problem);
            return;
        }
        _store.AddCurrency(c.Id, -a!.Buyout, 0);
        if (a.BidderId == c.Id) _store.AddCurrency(c.Id, a.Bid, 0); // their own bid, returned with the sale
        else RefundBidder(a, AhCancelledBidder, $"{ItemLabel(a.TemplateId, a.Quantity)} was bought out; your {a.Bid:N0} gold is back.");
        SettleAuction(a with { Bid = a.Buyout, BidderId = c.Id, BidderName = c.Name }, AhWonBuyout, AhBoughtOut);
    }

    private void CancelAuction(Session s, int auctionId)
    {
        var a = _store.ListAuctions().FirstOrDefault(x => x.Id == auctionId);
        if (a is null || a.SellerId != s.Character!.Id)
        {
            AuctionText(s, AhFailedCancel, "That isn't your auction.");
            return;
        }
        _store.DeleteAuction(a.Id);
        RefundBidder(a, AhCancelledBidder, $"The auction for {ItemLabel(a.TemplateId, a.Quantity)} was cancelled; your {a.Bid:N0} gold is back.");
        GrantItem(a.SellerId, a.TemplateId, a.Quantity);
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        AuctionText(s, AhCancelledSeller, $"You cancelled the auction for {ItemLabel(a.TemplateId, a.Quantity)}; the deposit is kept.");
    }

    /// <summary>A sale: the winner gets the item, the seller the price less the fee, plus the deposit back.</summary>
    private void SettleAuction(AuctionRecord a, int winnerMessage, int sellerMessage)
    {
        _store.DeleteAuction(a.Id);
        var item = ItemLabel(a.TemplateId, a.Quantity);
        GrantItem(a.BidderId!.Value, a.TemplateId, a.Quantity);
        var proceeds = (int)(a.Bid * AuctionSaleShare);
        _store.AddCurrency(a.SellerId, proceeds + a.Deposit, 0);
        AuctionNotify(a.BidderId.Value, winnerMessage, $"You won {item} for {a.Bid:N0} gold.");
        AuctionNotify(a.SellerId, sellerMessage, $"{a.BidderName} bought {item} for {a.Bid:N0} gold: {proceeds:N0} gold after the fee, and your {a.Deposit:N0} gold deposit back.");
        _log.LogInformation("Auction {Id} sold: {Item} to {Buyer} for {Bid}", a.Id, item, a.BidderName, a.Bid);
    }

    private void RefundBidder(AuctionRecord a, int message, string text)
    {
        if (a.BidderId is not { } bidder) return;
        _store.AddCurrency(bidder, a.Bid, 0);
        AuctionNotify(bidder, message, text);
    }

    /// <summary>Ends auctions whose time is up: sold to the highest bidder, or back to the seller with the deposit.</summary>
    private void ExpireAuctions(double now)
    {
        if (now < _nextAuctionCheck) return;
        _nextAuctionCheck = now + 15;
        var unixNow = UnixNow;
        foreach (var a in _store.ListAuctions().Where(a => a.ExpiresAt <= unixNow))
        {
            if (a.BidderId is not null)
            {
                SettleAuction(a, AhWonCompletion, AhCompleted);
                continue;
            }
            _store.DeleteAuction(a.Id);
            GrantItem(a.SellerId, a.TemplateId, a.Quantity);
            _store.AddCurrency(a.SellerId, a.Deposit, 0);
            AuctionNotify(a.SellerId, AhExpired, $"Your auction for {ItemLabel(a.TemplateId, a.Quantity)} ended without a bid; the item and your {a.Deposit:N0} gold deposit are back.");
        }
    }

    // ---- search ------------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, EquipSlot[]> SlotWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["+torso"] = [EquipSlot.Chest, EquipSlot.FashTorso], ["+feet"] = [EquipSlot.Feet, EquipSlot.FashFeet],
        ["+hands"] = [EquipSlot.Hands, EquipSlot.FashHands], ["+head"] = [EquipSlot.Head, EquipSlot.FashHead],
        ["+legs"] = [EquipSlot.Leg, EquipSlot.FashLegs], ["+mainhand"] = [EquipSlot.Weapon, EquipSlot.FashWeapon],
        ["+offhand"] = [EquipSlot.Offhand, EquipSlot.FashOffhand], ["+bangle"] = [EquipSlot.BangleL, EquipSlot.BangleR],
        ["+misc"] = [EquipSlot.Misc], ["+ring"] = [EquipSlot.RingL1, EquipSlot.RingL2, EquipSlot.RingR1, EquipSlot.RingR2], ["+neck"] = [EquipSlot.Neck],
    };

    /// <summary>InventoryFiltering.FilterType, as the client's own inventory filter tabs.</summary>
    private static bool InFilter(ItemInfo i, int filter) => filter switch
    {
        1 => i.Slot is EquipSlot.Head or EquipSlot.Chest or EquipSlot.Leg or EquipSlot.Feet or EquipSlot.Hands,
        2 => i.Slot is EquipSlot.Offhand or EquipSlot.Weapon,
        3 => i.Slot is >= EquipSlot.RingR1 and <= EquipSlot.Neck or EquipSlot.Misc,
        4 => i.Slot is >= EquipSlot.FashHead and <= EquipSlot.FashHands or EquipSlot.FashWeapon or EquipSlot.FashOffhand,
        5 => i.Slot is EquipSlot.Companion or EquipSlot.Mount or EquipSlot.Saddle || i.Subtype == "PetFood",
        6 => i.Subtype == "Token",
        7 => i.Subtype is "FishingRod" or "FishingItem",
        8 => i.Subtype == "Cooking",
        9 => i.Subtype == "Consumable",
        11 => i.Slot == EquipSlot.Unequipable && i.Subtype is not ("PetFood" or "Token" or "FishingRod" or "FishingItem" or "Cooking" or "Consumable"),
        _ => true,
    };

    /// <summary>The query is "{words} {+slot} {+Class}" (GUIAuctionHouseListings): name words, slot and class tokens.</summary>
    private IEnumerable<AuctionRecord> SearchAuctions(IEnumerable<AuctionRecord> listings, int filter, string query, int minLevel, int maxLevel)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var slots = tokens.Where(SlotWords.ContainsKey).SelectMany(t => SlotWords[t]).ToHashSet();
        var classes = tokens.Where(t => t.StartsWith('+') && !SlotWords.ContainsKey(t)).Select(t => t[1..]).ToList();
        var words = tokens.Where(t => !t.StartsWith('+')).ToList();
        foreach (var a in listings)
        {
            if (!_balance.Items.TryGetValue(a.TemplateId, out var i) || !InFilter(i, filter)) continue;
            if (words.Any(w => !i.Name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            if (slots.Count > 0 && !slots.Contains(i.Slot)) continue;
            if (classes.Count > 0 && i.Classes.Count > 0 && !classes.Any(cl => i.Classes.Contains(cl, StringComparer.OrdinalIgnoreCase))) continue;
            if ((minLevel >= 0 && i.LevelRequirement < minLevel) || (maxLevel >= 0 && i.LevelRequirement > maxLevel)) continue;
            yield return a;
        }
    }

    private IEnumerable<AuctionRecord> Sorted(IEnumerable<AuctionRecord> listings, int sort, int dir)
    {
        Func<AuctionRecord, IComparable> key = sort switch
        {
            1 => a => _balance.Items.GetValueOrDefault(a.TemplateId)?.Name ?? "",
            2 => a => a.Quantity,
            3 => a => _balance.Items.GetValueOrDefault(a.TemplateId)?.LevelRequirement ?? 0,
            4 => a => _balance.Items.GetValueOrDefault(a.TemplateId)?.Subtype ?? "",
            5 => a => a.ExpiresAt,
            6 => a => a.SellerName,
            7 => a => a.BidderId is null ? a.StartBid : a.Bid,
            8 => a => a.Buyout,
            _ => a => a.ExpiresAt,
        };
        return dir == 2 ? listings.OrderByDescending(key) : listings.OrderBy(key);
    }

    private void SendListings(Session s, int type, List<AuctionRecord> listings, int page, int perPage)
    {
        var c = s.Character!;
        var used = _store.ListAuctions().Count(a => a.SellerId == c.Id);
        var extra = Math.Min(AuctionMaxSlots - AuctionFreeSlots, _store.GetAuctionSlots(c.Id));
        if (listings.Count == 0 && type != AhMyListings)
        {
            Send(s, Opcode.AuctionHouse, b =>
            {
                b.WriteVariableInt32(AhFailedQuery);
                b.WriteVariableInt32(used);
                b.WriteVariableInt32(extra);
            });
            return;
        }
        perPage = Math.Clamp(perPage, 1, 50);
        var shown = listings.Skip(Math.Max(0, page - 1) * perPage).Take(perPage).ToList();
        var unixNow = UnixNow;
        Send(s, Opcode.AuctionHouse, b =>
        {
            b.WriteVariableInt32(type);
            b.WriteVariableInt32(listings.Count);
            b.WriteVariableInt32(shown.Count);
            foreach (var a in shown)
            {
                b.WriteVariableInt32((int)a.Id);
                b.WriteVariableInt32(a.TemplateId);
                b.WriteVariableInt32(a.Quantity);
                b.Write(TimeSpan.FromSeconds(Math.Max(0, a.ExpiresAt - unixNow)).ToString(@"d\.hh\:mm\:ss"));
                b.Write(a.SellerName);
                b.WriteVariableInt32(a.BidderId is null ? a.StartBid : a.Bid);
                b.Write(a.BidderName ?? "NONE");
                b.WriteVariableInt32(a.Buyout);
            }
            if (type == AhMyListings)
            {
                b.WriteVariableInt32(used);
                b.WriteVariableInt32(extra);
            }
        });
    }

    // ---- messages ----------------------------------------------------------------------------------------------

    private void AuctionText(Session s, int type, string text) => Send(s, Opcode.AuctionHouse, b =>
    {
        b.WriteVariableInt32(type);
        b.Write(text);
    });

    /// <summary>Tells the character if they're online, with their new backpack and gold.</summary>
    private void AuctionNotify(long characterId, int type, string text)
    {
        if (Online.FirstOrDefault(p => p.Character!.Id == characterId) is not { } s) return;
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        AuctionText(s, type, text);
    }

    private string ItemLabel(int template, int quantity)
    {
        var name = _balance.Items.GetValueOrDefault(template)?.Name ?? $"item {template}";
        return quantity > 1 ? $"{quantity} × {name}" : name;
    }
}
