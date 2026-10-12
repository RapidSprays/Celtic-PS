using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>Starting kit, inventory and item use (docs/protocol-notes.md, inventory messages).</summary>
public sealed partial class GameServer
{
    private const float PotionRechargeSeconds = 10f;
    private const int SkillBooksInKit = 2;

    private CharacterSheet LoadSheet(CharacterRecord c) =>
        new(c, _store.GetProgress(c.Id), _store.ListItems(c.Id), _store.ListSkills(c.Id), _balance);

    private CharacterSheet Sheet(Session s) => LoadSheet(s.Character!);

    /// <summary>Gives the class kit once: equipped starter gear, potions, a couple of class skill books, coins and skills.</summary>
    private void GrantKitIfNeeded(CharacterRecord c)
    {
        if (_store.GetProgress(c.Id).KitGranted) return;
        var kit = ClassKit.For(c.Class);
        var items = kit.Equipment.Select(e => (e.TemplateId, 1, (int)e.Slot)).ToList();
        items.Add((ClassKit.MinorHealthPotion, ClassKit.StartingPotions, ItemRecord.Backpack));
        foreach (var book in ClassSkillBooks((CharacterClass)c.Class).Where(b => !kit.Skills.Contains(_balance.SkillTaughtBy(b)!.Id)).Take(SkillBooksInKit))
            items.Add((book.Id, 1, ItemRecord.Backpack));
        if (_store.GrantKit(c.Id, items, kit.Skills, ClassKit.StartingCoins, hud: ""))
            _log.LogInformation("Granted {Class} starter kit to {Name}", kit.Class, c.Name);
    }

    private IEnumerable<ItemInfo> ClassSkillBooks(CharacterClass cls) =>
        _balance.Items.Values
            .Where(i => i.Id is > 1000 and < 1057 && i.SkillLink.StartsWith("Learn", StringComparison.Ordinal) && i.Classes.Contains(cls.ToString()))
            .Where(i => _balance.SkillTaughtBy(i) is not null)
            .OrderBy(i => i.Id);

    private void OnEquipItem(Session s, EquipItemRequest req)
    {
        var sheet = Sheet(s);
        var item = sheet.Items.FirstOrDefault(i => i.Id == req.InventoryId);
        string? problem = null;
        if (item is null) problem = "You don't have that item.";
        else if (req.Equip)
        {
            var info = _balance.Items.GetValueOrDefault(item.TemplateId);
            if (item.Slot != ItemRecord.Backpack) problem = "That item is already equipped.";
            else if (info is null || info.Slot < 0) problem = "That item can't be equipped.";
            else if (!SlotFits(info.Slot, req.Slot)) problem = "That item doesn't go there.";
            else if (info.Classes.Count > 0 && !info.Classes.Contains(((CharacterClass)s.Character!.Class).ToString())) problem = "Your class can't use that item.";
        }
        else if (item.Slot == ItemRecord.Backpack) problem = "That item isn't equipped.";

        if (problem is not null)
        {
            Send(s, Opcode.EquipItemReply, b => InventoryReplies.WriteEquipment(b, CommandStatus.Rejected, problem, [], [], alwaysIncludeLists: false));
            return;
        }

        if (req.Equip)
        {
            // Whatever is in the target slot goes back to the backpack.
            foreach (var occupant in sheet.Equipped.Where(i => i.Slot == req.Slot)) _store.SetItemSlot(occupant.Id, ItemRecord.Backpack);
            _store.SetItemSlot(item!.Id, req.Slot);
        }
        else
        {
            _store.SetItemSlot(item!.Id, ItemRecord.Backpack);
        }

        var updated = RefreshSheet(s);
        Send(s, Opcode.EquipItemReply, b => InventoryReplies.WriteEquipment(b, CommandStatus.Accepted, "", updated.EquipmentSlots(), updated.BackpackSlots(), alwaysIncludeLists: false));
        // EquipItemReply doesn't refresh speed, range, damage or armour on the client.
        Send(s, Opcode.StatsUpdate, updated.Stats().Write);
    }

    /// <summary>Rings and bangles can go in any of their slots; everything else in its own.</summary>
    private static bool SlotFits(EquipSlot itemSlot, int requested) => itemSlot switch
    {
        EquipSlot.RingR1 or EquipSlot.RingR2 or EquipSlot.RingL1 or EquipSlot.RingL2 => requested is >= 8 and <= 11,
        EquipSlot.BangleR or EquipSlot.BangleL => requested is 12 or 13,
        _ => requested == (int)itemSlot,
    };

    private void OnDeleteItem(Session s, DeleteItemRequest req)
    {
        var sheet = Sheet(s);
        var item = sheet.Items.FirstOrDefault(i => i.Id == req.InventoryId && i.TemplateId == req.TemplateId);
        var status = CommandStatus.Accepted;
        var reason = "";
        if (item is null)
        {
            status = CommandStatus.Rejected;
            reason = "You don't have that item.";
        }
        else
        {
            _store.SetItemQuantity(item.Id, item.Quantity - Math.Clamp(req.Quantity, 1, item.Quantity));
        }
        var updated = RefreshSheet(s);
        // DeleteItemReply carries the lists even when rejected (NetworkManager.cs:5042).
        Send(s, Opcode.DeleteItemReply, b => InventoryReplies.WriteEquipment(b, status, reason, updated.EquipmentSlots(), updated.BackpackSlots(), alwaysIncludeLists: true));
        if (item is { Slot: >= 0 }) Send(s, Opcode.StatsUpdate, updated.Stats().Write);
    }

    private void OnUseItem(Session s, UseItemRequest req)
    {
        var sheet = Sheet(s);
        var item = sheet.Backpack.FirstOrDefault(i => i.Id == req.InventoryId);
        var info = item is null ? null : _balance.Items.GetValueOrDefault(item.TemplateId);
        if (item is null || info is null || !info.Usable)
        {
            Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Rejected, "Nothing happens.", 0, null));
            return;
        }

        if (_balance.SkillTaughtBy(info) is { } skill)
        {
            if (info.Classes.Count > 0 && !info.Classes.Contains(((CharacterClass)s.Character!.Class).ToString()))
            {
                Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Rejected, "Your class can't learn that.", 0, null));
                return;
            }
            if (!_store.LearnSkill(s.Character!.Id, skill.Id))
            {
                Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Rejected, $"You already know {skill.Name}.", 0, null));
                return;
            }
            _store.SetItemQuantity(item.Id, item.Quantity - 1);
            var learned = RefreshSheet(s);
            Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Accepted, "", 0f, learned.InventoryWithMoney()));
            Send(s, Opcode.BuySkillReply, b => InventoryReplies.WriteBuySkill(b, 0, info.Id, learned.SkillEntries()));
            _log.LogInformation("{Name} learned {Skill} ({Id})", s.Character.Name, skill.Name, skill.Id);
            return;
        }

        if (info.SkillLink == "InstantHeal")
        {
            // Approximated: a minor potion restores 30% of max health.
            if (s.Dead)
            {
                Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Rejected, "You are dead.", 0, null));
                return;
            }
            var healed = Math.Min(s.MaxHealth, s.Health + Math.Max(10, s.MaxHealth * 30 / 100));
            s.Health = healed;
            _store.SaveVitals(s.Character!.Id, healed, s.Energy);
            _store.SetItemQuantity(item.Id, item.Quantity - 1);
            var after = RefreshSheet(s);
            Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Accepted, "", PotionRechargeSeconds, after.InventoryWithMoney()));
            Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.Health, healed)]).Write);
            return;
        }

        Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Rejected, "You can't use that here yet.", 0, null));
    }

    private void OnSkillBarUpdate(Session s, SkillBarUpdate update)
    {
        var sets = Sheet(s).Hud().ToList();
        var index = sets.FindIndex(h => h.SetId == update.SetId);
        var replacement = new HudSet(update.SetId, update.Slots);
        if (index >= 0) sets[index] = replacement;
        else sets.Add(replacement);
        _store.SaveHud(s.Character!.Id, CharacterSheet.SerializeHud(sets.OrderBy(h => h.SetId).ToList()));
    }

    private void OnFavourite(Session s, bool favourite, int inventoryId)
    {
        if (Sheet(s).Items.Any(i => i.Id == inventoryId)) _store.SetItemFavourite(inventoryId, favourite);
    }

    /// <summary>Backpack order isn't stored yet; answer with the current list so the client stays consistent.</summary>
    private void OnInventorySwap(Session s, byte inventoryType)
    {
        var backpack = Sheet(s).BackpackSlots();
        Send(s, Opcode.InventoryItemSwapReply, b =>
        {
            b.Write(inventoryType);
            b.WriteVariableInt32(backpack.Count);
            foreach (var item in backpack) item.Write(b);
        });
    }
}
