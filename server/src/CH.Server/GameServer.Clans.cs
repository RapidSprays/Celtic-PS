using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Clans (ClanMessage 17003, CH_ClanNetworkHandler; docs/social-notes.md): create with a Clan Banner, invite, promote,
/// demote/kick/leave, message, permissions, disband, clan chat, and the clan bank (20001-20006). The client sends
/// any member's attempt; who may do what is decided here (<b>our own</b> rules: Nobles and up invite; you only promote
/// or demote those below you; the Leader can hand over leadership by promoting a General). The roster is pushed to
/// every online member after any change, and the clan name follows on nameplates.
/// </summary>
public sealed partial class GameServer
{
    private const int Recruit = 0, Member = 1, Noble = 2, General = 3, Leader = 4;
    private const int ClanBanner = 6, ClanNameMax = 30, ClanMessageMax = 300, ClanInviteRank = Noble;
    private const int ClanBankStartSlots = 20, ClanBankSlotOffer = 10, ClanBankSlotGold = 100_000, ClanBankSlotPlatinum = 50;
    private const int ClanAppearanceSlot = 74;

    /// <summary>Default permissions, rank 0-4 × (view bank, inspect, donate, withdraw, buy slots, set permissions, see logs).</summary>
    private const string DefaultClanPermissions = "1010000" + "1110000" + "1111001" + "1111101" + "1111111";

    /// <summary>Open clan invites: invitee → (clan, inviter, lapse time).</summary>
    private readonly Dictionary<long, (long ClanId, long InviterId, double Until)> _clanInvites = new();

    private static bool Permission(ClanRecord clan, int rank, int index) =>
        rank == Leader || (rank is >= 0 and < Leader && clan.Permissions.Length >= 35 && clan.Permissions[rank * 7 + index] == '1');

    private ClanBlock ClanBlockFor(long clanId)
    {
        if (_store.GetClan(clanId) is not { } clan) return ClanBlock.None;
        var members = _store.ListClanMembers(clanId).Select(m => (SocialEntry(m.CharacterId), m.Rank)).Where(m => m.Item1 is not null)
            .Select(m => (m.Item1!, m.Rank)).ToList();
        return new ClanBlock((int)clan.Id, clan.Name, members, clan.Message, clan.Permissions.Select(ch => ch == '1').ToList());
    }

    /// <summary>ClanMessage CLAN_LIST (sub 1): the roster, or −1 for no clan.</summary>
    private void SendClanList(Session s)
    {
        var block = _store.GetMembership(s.Character!.Id) is { } m ? ClanBlockFor(m.ClanId) : ClanBlock.None;
        Send(s, Opcode.ClanMessage, b =>
        {
            b.WriteVariableInt32(1);
            block.Write(b);
        });
    }

    private void SendClanToMembers(long clanId, long? except = null)
    {
        foreach (var (id, _) in _store.ListClanMembers(clanId))
            if (id != except && OnlineById(id) is { } p) SendClanList(p);
    }

    /// <summary>A character joined or left a clan: their sheet, nameplate and roster follow.</summary>
    private void ClanChanged(long characterId)
    {
        if (OnlineById(characterId) is not { } p) return;
        p.ClanId = _store.GetMembership(characterId)?.ClanId;
        RefreshSheet(p);
        SendClanList(p);
        RefreshZoneHate(p.Position.Zone);
        if (!p.InZone || !_zones.TryGetValue(p.Position.Zone, out var zone)) return;
        var name = p.Sheet!.ClanName;
        SendToOthers(zone, p, Opcode.PlayerAppearanceUpdate, b =>
        {
            b.WriteVariableUInt32((uint)p.CharacterId);
            b.WriteVariableInt32(1);
            b.WriteVariableInt32(ClanAppearanceSlot);
            b.Write(name);
        });
    }

    private void OnClanMessage(Session s, NetIncomingMessage msg)
    {
        var c = s.Character!;
        var membership = _store.GetMembership(c.Id);
        var clan = membership is { } mm ? _store.GetClan(mm.ClanId) : null;
        var rank = membership?.Rank ?? -1;
        switch (msg.ReadVariableInt32())
        {
            case 0:
                CreateClan(s, msg.ReadString());
                break;
            case 2:
                InviteToClan(s, clan, rank, msg.ReadVariableInt32());
                break;
            case 3:
            {
                var clanId = msg.ReadVariableInt32();
                var reply = msg.ReadVariableInt32();
                msg.ReadVariableInt32();
                AnswerClanInvite(s, clanId, reply);
                break;
            }
            case var op when op is 4 or 5:
            {
                var target = msg.ReadVariableInt32();
                msg.ReadVariableInt32(); // the rank the client last saw
                if (clan is null) break;
                if (op == 4) Promote(s, clan, rank, target);
                else Demote(s, clan, rank, target);
                break;
            }
            case 10:
                if (clan is null || rank != Leader) break;
                var members = _store.ListClanMembers(clan.Id);
                _store.DeleteClan(clan.Id);
                foreach (var (id, _) in members) ClanChanged(id);
                _log.LogInformation("{Name} disbanded clan {Clan}", s.Name, clan.Name);
                break;
            case 11:
            {
                var message = msg.ReadString();
                if (clan is null || rank < General) break;
                _store.SetClanMessage(clan.Id, message.Length > ClanMessageMax ? message[..ClanMessageMax] : message);
                SendClanToMembers(clan.Id);
                break;
            }
            case 12:
            {
                var bits = string.Concat(Enumerable.Range(0, ClanBlock.Ranks * ClanBlock.PermissionsPerRank).Select(_ => msg.ReadBoolean() ? '1' : '0'));
                if (clan is null || !Permission(clan, rank, 5)) break;
                _store.SetClanPermissions(clan.Id, bits[..(Leader * 7)] + "1111111"); // the Leader can always do everything
                SendClanToMembers(clan.Id);
                break;
            }
        }
    }

    private void CreateClan(Session s, string name)
    {
        var c = s.Character!;
        name = name.Trim();
        var banner = Sheet(s).Backpack.FirstOrDefault(i => i.TemplateId == ClanBanner);
        var problem = _store.GetMembership(c.Id) is not null ? "You're already in a clan."
            : name.Length is 0 or > ClanNameMax || name.Any(ch => char.IsControl(ch) || ch is '[' or ']' or '#' or '~' or '^') ? "That clan name isn't allowed."
            : _store.ClanNameTaken(name) ? "That clan name is taken."
            : banner is null ? "You need a Clan Banner to found a clan."
            : null;
        if (problem is not null)
        {
            Tell(s, problem);
            return;
        }
        _store.SetItemQuantity(banner!.Id, banner.Quantity - 1);
        _store.CreateClan(name, DefaultClanPermissions, ClanBankStartSlots, c.Id, Leader);
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        ClanChanged(c.Id);
        Tell(s, $"You founded the clan {name}.");
        _log.LogInformation("{Name} founded clan {Clan}", s.Name, name);
    }

    private void InviteToClan(Session s, ClanRecord? clan, int rank, int targetId)
    {
        var target = OnlineByCharacter(targetId);
        var problem = clan is null ? "You're not in a clan."
            : rank < ClanInviteRank ? "Only Nobles and above can invite."
            : target is null || IsBlockedBy(s, target) ? "That player is not online."
            : _store.GetMembership(target.Character!.Id) is not null ? $"{target.Name} is already in a clan."
            : null;
        if (problem is not null)
        {
            Tell(s, problem);
            return;
        }
        _clanInvites[target!.Character!.Id] = (clan!.Id, s.Character!.Id, NetTime.Now + 30);
        Send(target, Opcode.ClanMessage, b =>
        {
            b.WriteVariableInt32(8);
            b.WriteVariableInt32((int)clan.Id);
            b.Write(clan.Name);
            b.WriteVariableInt32(s.CharacterId);
            b.Write(s.Name);
        });
        Tell(s, $"You invited {target.Name} to {clan.Name}.");
    }

    private void AnswerClanInvite(Session s, int clanId, int reply)
    {
        var c = s.Character!;
        if (!_clanInvites.Remove(c.Id, out var invite) || invite.ClanId != clanId || _store.GetClan(clanId) is not { } clan) return;
        var inviter = OnlineById(invite.InviterId);
        if (reply != FriendAccept || _store.GetMembership(c.Id) is not null)
        {
            if (inviter is not null) Tell(inviter, $"{s.Name} declined to join {clan.Name}.");
            return;
        }
        _store.SetClanMember(c.Id, clan.Id, Recruit);
        ClanChanged(c.Id);
        SendClanToMembers(clan.Id, except: c.Id);
        foreach (var (id, _) in _store.ListClanMembers(clan.Id))
            if (OnlineById(id) is { } p) Tell(p, $"{s.Name} joined {clan.Name}.");
    }

    private void ExpireClanInvites(double now)
    {
        foreach (var (invitee, invite) in _clanInvites.Where(i => i.Value.Until <= now).ToList())
        {
            _clanInvites.Remove(invitee);
            if (OnlineById(invite.InviterId) is { } inviter) Tell(inviter, $"{_store.GetCharacter(invitee)?.Name ?? "They"} didn't answer the clan invitation.");
        }
    }

    private void Promote(Session s, ClanRecord clan, int rank, int targetId)
    {
        var members = _store.ListClanMembers(clan.Id).ToDictionary(m => m.CharacterId, m => m.Rank);
        if (!members.TryGetValue(targetId, out var current) || targetId == s.Character!.Id) return;
        if (rank == Leader && current == General)
        {
            // Handing over the clan: the old Leader steps down to General.
            _store.SetClanMember(targetId, clan.Id, Leader);
            _store.SetClanMember(s.Character.Id, clan.Id, General);
        }
        else if (current + 1 < rank) _store.SetClanMember(targetId, clan.Id, current + 1);
        else
        {
            Tell(s, "You can only promote members below your own rank.");
            return;
        }
        SendClanToMembers(clan.Id);
    }

    /// <summary>Demoting a Recruit removes them; demoting yourself as a Recruit is leaving.</summary>
    private void Demote(Session s, ClanRecord clan, int rank, int targetId)
    {
        var self = targetId == s.Character!.Id;
        var members = _store.ListClanMembers(clan.Id).ToDictionary(m => m.CharacterId, m => m.Rank);
        if (!members.TryGetValue(targetId, out var current)) return;
        if (!self && current >= rank)
        {
            Tell(s, "You can only demote members below your own rank.");
            return;
        }
        if (self && current == Leader)
        {
            Tell(s, members.Count == 1 ? "Disband the clan to leave it." : "Promote a General to Leader first.");
            return;
        }
        if (current > Recruit) _store.SetClanMember(targetId, clan.Id, current - 1);
        else
        {
            _store.RemoveClanMember(targetId);
            ClanChanged(targetId);
            if (!self && OnlineById(targetId) is { } kicked) Tell(kicked, $"You were removed from {clan.Name}.");
        }
        SendClanToMembers(clan.Id);
    }

    private void ClanChat(Session s, string text)
    {
        if (_store.GetMembership(s.Character!.Id) is not { } m)
        {
            Tell(s, "You are not in a clan.");
            return;
        }
        foreach (var (id, _) in _store.ListClanMembers(m.ClanId))
            if (OnlineById(id) is { } p && !IsBlockedBy(s, p)) Send(p, Opcode.GeneralChat, new ChatMessage(ChatChannel.Clan, s.Name, text).WriteToClient);
    }

    // ---- clan bank (opened by a CLAN_BANK conversation action) -----------------------------------------------------

    private void OnClanBank(Session s, Opcode opcode, NetIncomingMessage msg)
    {
        var c = s.Character!;
        var membership = _store.GetMembership(c.Id);
        var clan = membership is { } m ? _store.GetClan(m.ClanId) : null;
        var rank = membership?.Rank ?? -1;
        switch (opcode)
        {
            case Opcode.ClanBankingMessage:
            {
                string? problem = clan is null ? "You're not in a clan." : !Permission(clan, rank, 0) ? "Your rank can't use the clan bank." : null;
                if (msg.ReadByte() == 1 && problem is null)
                {
                    var toBank = msg.ReadByte() == 1;
                    var id = msg.ReadVariableInt32();
                    msg.ReadVariableInt32();
                    var quantity = msg.ReadVariableInt32();
                    problem = toBank ? Donate(s, clan!, rank, id, quantity) : Withdraw(s, clan!, rank, id, quantity);
                }
                SendClanBank(s, problem);
                break;
            }
            case Opcode.ClanBankingLogMessage:
                Send(s, Opcode.ClanBankingLogMessageReply, b =>
                {
                    if (clan is null || !Permission(clan, rank, 6))
                    {
                        b.WriteVariableInt32(0);
                        b.Write("Your rank can't see the clan bank log.");
                        return;
                    }
                    var log = _store.ListClanBankLog(clan.Id);
                    b.WriteVariableInt32(1);
                    b.WriteVariableInt32(log.Count);
                    foreach (var e in log)
                    {
                        b.Write(e.Name);
                        b.Write((byte)(e.Donated ? 1 : 0));
                        b.WriteVariableInt32(e.TemplateId);
                        b.WriteVariableInt32(e.Quantity);
                        b.Write(e.At);
                    }
                });
                break;
            case Opcode.ClanBankingBuySlotMessage:
            {
                msg.ReadVariableInt32(); // offer id: there is one offer
                var platinum = msg.ReadVariableInt32() == 1;
                var progress = _store.GetProgress(c.Id);
                var problem = clan is null || !Permission(clan, rank, 4) ? "Your rank can't buy clan bank slots."
                    : platinum ? progress.Platinum < ClanBankSlotPlatinum ? "You don't have enough platinum." : null
                    : progress.Coins < ClanBankSlotGold ? "You don't have enough gold." : null;
                if (problem is null)
                {
                    _store.AddCurrency(c.Id, platinum ? 0 : -ClanBankSlotGold, platinum ? -ClanBankSlotPlatinum : 0);
                    _store.AddClanBankSlots(clan!.Id, ClanBankSlotOffer);
                    clan = _store.GetClan(clan.Id);
                }
                var after = _store.GetProgress(c.Id);
                Send(s, Opcode.ClanBankingBuySlotMessageReply, b =>
                {
                    b.WriteVariableInt32(problem is null ? 1 : 0);
                    if (problem is not null) b.Write(problem);
                    b.WriteVariableInt32(after.Platinum);
                    b.WriteVariableInt32(after.Coins);
                    WriteClanBankSlots(b, clan?.BankSlots ?? ClanBankStartSlots);
                });
                RefreshSheet(s);
                break;
            }
        }
    }

    private string? Donate(Session s, ClanRecord clan, int rank, int itemId, int quantity)
    {
        var item = Sheet(s).Backpack.FirstOrDefault(i => i.Id == itemId);
        var info = item is null ? null : _balance.Items.GetValueOrDefault(item.TemplateId);
        if (!Permission(clan, rank, 2)) return "Your rank can't donate to the clan bank.";
        if (item is null || info is null) return "That item isn't in your backpack.";
        if (item.Bound || !info.Tradeable) return "That item can't go in the clan bank.";
        quantity = Math.Clamp(quantity, 1, item.Quantity);
        var bank = _store.ListClanBank(clan.Id);
        var stack = info.Stackable ? bank.FirstOrDefault(i => i.TemplateId == item.TemplateId) : null;
        if (stack is null && bank.Count >= clan.BankSlots) return "The clan bank is full.";
        _store.Atomically(() =>
        {
            _store.SetItemQuantity(item.Id, item.Quantity - quantity);
            if (stack is not null) _store.SetClanBankQuantity(stack.Id, stack.Quantity + quantity);
            else _store.AddClanBankItem(clan.Id, item.TemplateId, quantity, item.Charges);
            _store.LogClanBank(clan.Id, s.Name, true, item.TemplateId, quantity);
        });
        return null;
    }

    private string? Withdraw(Session s, ClanRecord clan, int rank, int bankId, int quantity)
    {
        if (!Permission(clan, rank, 3)) return "Your rank can't withdraw from the clan bank.";
        var item = _store.ListClanBank(clan.Id).FirstOrDefault(i => i.Id == bankId);
        if (item is null) return "That item isn't in the clan bank.";
        quantity = Math.Clamp(quantity, 1, item.Quantity);
        _store.Atomically(() =>
        {
            _store.SetClanBankQuantity(item.Id, item.Quantity - quantity);
            if (_balance.Items.GetValueOrDefault(item.TemplateId) is { Stackable: true }) GrantItem(s.Character!.Id, item.TemplateId, quantity);
            else _store.RestoreItem(s.Character!.Id, item.TemplateId, quantity, false, item.Charges);
            _store.LogClanBank(clan.Id, s.Name, false, item.TemplateId, quantity);
        });
        return null;
    }

    /// <summary>ClanBankingMessageReply (20002): backpack, clan bank, slot offers, permissions.</summary>
    private void SendClanBank(Session s, string? problem)
    {
        var membership = _store.GetMembership(s.Character!.Id);
        var clan = membership is { } m ? _store.GetClan(m.ClanId) : null;
        if (clan is null)
        {
            Send(s, Opcode.ClanBankingMessageReply, b => { b.Write((byte)0); b.WriteVariableInt32(0); b.Write(problem ?? "You're not in a clan."); });
            return;
        }
        var backpack = _store.ListItems(s.Character.Id).Where(i => i.Slot == ItemRecord.Backpack).ToList();
        var bank = _store.ListClanBank(clan.Id);
        Send(s, Opcode.ClanBankingMessageReply, b =>
        {
            b.Write((byte)1);
            b.WriteVariableInt32(1);
            WriteItems(b, backpack);
            b.WriteVariableInt32(bank.Count);
            foreach (var i in bank) new ItemSlot((int)i.Id, i.TemplateId, i.Quantity, false, i.Charges, false).Write(b);
            WriteClanBankSlots(b, clan.BankSlots);
            foreach (var bit in clan.Permissions) b.Write(bit == '1');
        });
        if (problem is not null) Tell(s, problem);
        RefreshSheet(s);
    }

    /// <summary>Slot settings: the clan's slots and one repeatable offer (10 slots for gold or platinum; <b>our own</b> prices).</summary>
    private static void WriteClanBankSlots(NetBuffer b, int slots)
    {
        b.WriteVariableInt32(slots);
        b.WriteVariableInt32(1);
        b.WriteVariableInt32(1);
        b.WriteVariableInt32(ClanBankSlotOffer);
        b.WriteVariableInt32(ClanBankSlotPlatinum);
        b.WriteVariableInt32(ClanBankSlotGold);
        b.WriteVariableInt32(99);
        b.Write("");
        b.WriteVariableInt32(0); // the second (limited-time) offer list: must be present, the permissions follow it
    }
}
