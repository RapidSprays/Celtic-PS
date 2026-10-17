using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Openable chests (Game/Chests.cs). Mystery Chests use the client's chest-opening screen, driven by InGamePopup (21006):
/// OPEN, NEXT for every reward but the last, FINAL for the last, all sent together. Its "Next Chest" button comes back
/// as option (42, 1). Other chests just report what they gave in chat.
/// </summary>
public sealed partial class GameServer
{
    private const int PopupOpen = 40, PopupNext = 41, PopupFinal = 42;
    private const double ChestDropChance = 0.015;

    // Built once at start-up (GameServer constructor): it scans every item name against every chest description, which
    // takes seconds and must never run on the game thread.
    private readonly ChestCatalog _chestCatalog;

    private ChestCatalog ChestCatalog => _chestCatalog;

    /// <summary>Called from OnUseItem: true if the item was a chest (handled here).</summary>
    private bool TryOpenChest(Session s, ItemRecord item)
    {
        if (ChestCatalog.For(item.TemplateId) is not { } chest) return false;
        OpenChest(s, item, chest);
        return true;
    }

    private void OpenChest(Session s, ItemRecord item, ChestDefinition chest)
    {
        var c = s.Character!;
        var roll = ChestCatalog.Roll(chest, Random.Shared);
        _store.SetItemQuantity(item.Id, item.Quantity - 1);
        foreach (var (templateId, qty) in roll.Items) GrantItem(c.Id, templateId, qty);
        if (roll.Coins > 0 || roll.Platinum > 0) _store.AddCurrency(c.Id, roll.Coins, roll.Platinum);
        s.LastChestTemplate = chest.ItemId;

        var sheet = RefreshSheet(s);
        Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Accepted, "", 0f, sheet.InventoryWithMoney()));
        if (roll.Platinum > 0) Send(s, Opcode.CurrencyInfo, b => { b.Write((byte)1); b.WriteVariableInt32(sheet.Progress.Platinum); });

        var lines = RewardLines(roll);
        if (chest.Kind == ChestKind.Mystery && lines.Count > 0)
        {
            SendChestScreen(s, PopupOpen, "popup_magbox_open_prefab", []);
            for (var i = 0; i < lines.Count - 1; i++) SendChestScreen(s, PopupNext, "popup_magbox_next_prefab", [lines[i]]);
            SendChestScreen(s, PopupFinal, "popup_magbox_final_prefab", [lines[^1]]);
        }
        Tell(s, lines.Count == 0 ? $"The {chest.Name} was empty." : $"{chest.Name}: {string.Join(", ", lines)}");
        _log.LogInformation("{Name} opened {Chest}: {Rewards}", s.Name, chest.Name, string.Join(", ", lines));
    }

    /// <summary>InGamePopup OPEN (21006, ProcessInGamePopup NetworkManager.cs:6486): type 1, id, byte, 1, prefab, strings.</summary>
    private void SendChestScreen(Session s, int popupId, string prefab, IReadOnlyList<string> text) =>
        Send(s, Opcode.InGamePopup, b =>
        {
            b.WriteVariableInt32(1); // PMT_OpenPopup
            b.WriteVariableInt32(popupId);
            b.Write((byte)0);
            b.WriteVariableInt32(1); // must be 1 or the client drops the popup
            b.Write(prefab);
            b.WriteVariableInt32(text.Count);
            foreach (var t in text) b.Write(t);
        });

    /// <summary>InGamePopup from the client: varint 2 (option selected), popup id, option. (42, 1) is "Next Chest".</summary>
    private void OnPopupOption(Session s, int type, int popupId, int option)
    {
        if (type != 2 || popupId != PopupFinal || option != 1 || s.LastChestTemplate is not { } template) return;
        var next = Sheet(s).Backpack.FirstOrDefault(i => i.TemplateId == template);
        if (next is null || ChestCatalog.For(template) is not { } chest)
        {
            Tell(s, "You have no more of those chests.");
            return;
        }
        OpenChest(s, next, chest);
    }

    private List<string> RewardLines(ChestRoll roll)
    {
        var lines = roll.Items.Select(x => $"{(x.Quantity > 1 ? $"{x.Quantity} x " : "")}{_balance.Items.GetValueOrDefault(x.TemplateId)?.Name ?? $"item {x.TemplateId}"}").ToList();
        if (roll.Platinum > 0) lines.Add($"{roll.Platinum:N0} Platinum");
        if (roll.Coins > 0) lines.Add($"{roll.Coins:N0} Gold");
        return lines;
    }

    /// <summary>Adds items to the backpack: stackable ones join an existing stack, others get one entry each.</summary>
    private void GrantItem(long characterId, int templateId, int quantity)
    {
        if (TryGrantSigils(characterId, templateId, quantity)) return; // applied, never in the backpack
        var info = _balance.Items.GetValueOrDefault(templateId);
        if (info is { Stackable: true })
        {
            var stack = _store.ListItems(characterId).FirstOrDefault(i => i.Slot == ItemRecord.Backpack && i.TemplateId == templateId);
            if (stack is not null) _store.SetItemQuantity(stack.Id, stack.Quantity + quantity);
            else _store.AddItem(characterId, templateId, quantity);
            return;
        }
        for (var i = 0; i < Math.Min(quantity, 50); i++) _store.AddItem(characterId, templateId, 1);
    }

    /// <summary>A random event Mystery Chest (Beltane, Samhain, Icebound…), placeholders left out.</summary>
    private int? RandomEventChest()
    {
        var chests = ChestCatalog.Chests.Values.Where(c => c.Kind == ChestKind.Mystery && !_balance.Items[c.ItemId].IsPlaceholder).ToList();
        return chests.Count == 0 ? null : chests[Random.Shared.Next(chests.Count)].ItemId;
    }

    /// <summary>A small chance for a kill to drop a random Mystery Chest or random-item box.</summary>
    private int? RollChestDrop()
    {
        if (Random.Shared.NextDouble() >= ChestDropChance) return null;
        var droppable = ChestCatalog.Chests.Values.Where(c => c.Kind is ChestKind.Mystery or ChestKind.OneOf && !_balance.Items[c.ItemId].IsPlaceholder).ToList();
        return droppable.Count == 0 ? null : droppable[Random.Shared.Next(droppable.Count)].ItemId;
    }
}
