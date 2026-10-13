using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;

namespace CH.Server;

/// <summary>
/// Personal bank (BankingMessage 18006/18007; docs/economy-notes.md). Opened by a banker's BANK conversation action.
/// Items move between the backpack (slot −1) and the bank (slot −2); the bank holds no gold.
/// </summary>
public sealed partial class GameServer
{
    private void OnBankMessage(Session s, NetIncomingMessage msg)
    {
        string? problem = null;
        if (msg.ReadByte() == 1)
        {
            var toBank = msg.ReadByte() == 1;
            var itemId = msg.ReadVariableInt32();
            msg.ReadVariableInt32(); // template: the server trusts its own record
            var quantity = msg.ReadVariableInt32();
            problem = BankTransfer(s, toBank, itemId, quantity);
        }
        if (problem is not null) Tell(s, problem);
        var items = _store.ListItems(s.Character!.Id);
        Send(s, Opcode.BankingMessageReply, b =>
        {
            b.Write((byte)1);
            WriteItems(b, items.Where(i => i.Slot == ItemRecord.Backpack));
            WriteItems(b, items.Where(i => i.Slot == ItemRecord.Bank));
        });
        RefreshSheet(s);
    }

    private static void WriteItems(NetBuffer b, IEnumerable<ItemRecord> items)
    {
        var list = items.ToList();
        b.WriteVariableInt32(list.Count);
        foreach (var i in list) CharacterSheet.ToWire(i).Write(b);
    }

    private string? BankTransfer(Session s, bool toBank, long itemId, int quantity)
    {
        var from = toBank ? ItemRecord.Backpack : ItemRecord.Bank;
        var to = toBank ? ItemRecord.Bank : ItemRecord.Backpack;
        var items = _store.ListItems(s.Character!.Id);
        var item = items.FirstOrDefault(i => i.Id == itemId && i.Slot == from);
        if (item is null) return toBank ? "That item isn't in your backpack." : "That item isn't in your bank.";
        var stackable = _balance.Items.GetValueOrDefault(item.TemplateId)?.Stackable ?? false;
        var target = stackable ? items.FirstOrDefault(i => i.Slot == to && i.TemplateId == item.TemplateId) : null;
        if (toBank && target is null && items.Count(i => i.Slot == ItemRecord.Bank) >= s.Sheet!.BankLimit) return "Your bank is full.";
        quantity = Math.Clamp(quantity, 1, item.Quantity);
        _store.Atomically(() =>
        {
            if (target is not null)
            {
                _store.SetItemQuantity(target.Id, target.Quantity + quantity);
                _store.SetItemQuantity(item.Id, item.Quantity - quantity);
            }
            else if (quantity >= item.Quantity) _store.SetItemSlot(item.Id, to);
            else
            {
                _store.SetItemQuantity(item.Id, item.Quantity - quantity);
                _store.AddItem(s.Character.Id, item.TemplateId, quantity, to);
            }
        });
        return null;
    }
}
