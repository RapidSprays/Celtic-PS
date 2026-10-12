using Lidgren.Network;

namespace CH.Protocol.Messages;

// Inventory and item messages. Line refs are to NetworkManager.cs.

/// <summary>EquipItem (15001), client → server (writers 4983/4998): equip (1) or unequip (0), template, inventory id, quantity, slot.</summary>
public sealed record EquipItemRequest(bool Equip, int TemplateId, int InventoryId, int Quantity, int Slot)
{
    public static EquipItemRequest Read(NetBuffer b) =>
        new(b.ReadByte() == 1, b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32());

    public void Write(NetBuffer b)
    {
        b.Write((byte)(Equip ? 1 : 0));
        b.WriteVariableInt32(TemplateId);
        b.WriteVariableInt32(InventoryId);
        b.WriteVariableInt32(Quantity);
        b.WriteVariableInt32(Slot);
    }
}

/// <summary>DeleteItem (15006), client → server (writer 5018): template, inventory id, quantity.</summary>
public sealed record DeleteItemRequest(int TemplateId, int InventoryId, int Quantity)
{
    public static DeleteItemRequest Read(NetBuffer b) => new(b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32());
}

/// <summary>UseItem (15012), client → server (writer 5070): inventory id, target type, target id.</summary>
public sealed record UseItemRequest(int InventoryId, TargetType TargetType, uint TargetId)
{
    public static UseItemRequest Read(NetBuffer b) => new(b.ReadVariableInt32(), (TargetType)b.ReadVariableInt32(), b.ReadVariableUInt32());

    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(InventoryId);
        b.WriteVariableInt32((int)TargetType);
        b.WriteVariableUInt32(TargetId);
    }
}

/// <summary>Writers for the replies that resend equipment (exactly 25 entries) and/or the backpack.</summary>
public static class InventoryReplies
{
    /// <summary>EquipItemReply (15002, reader 5111) and DeleteItemReply (15007, reader 5042): status [+ reason], 25 equipment entries, backpack list.</summary>
    public static void WriteEquipment(NetBuffer b, CommandStatus status, string reason, IReadOnlyList<ItemSlot> equipment, IReadOnlyList<ItemSlot> backpack, bool alwaysIncludeLists)
    {
        b.Write((byte)status);
        if (status != CommandStatus.Accepted)
        {
            b.Write(reason);
            if (!alwaysIncludeLists) return;
        }
        if (equipment.Count != CharacterSummary.EquipmentSlots) throw new ArgumentException("equipment must have 25 entries");
        foreach (var slot in equipment) slot.Write(b);
        b.WriteVariableInt32(backpack.Count);
        foreach (var item in backpack) item.Write(b);
    }

    /// <summary>UseItemReply (15013, reader 5079): status; rejected → reason; accepted → recharge seconds, coins, backpack.</summary>
    public static void WriteUseItem(NetBuffer b, CommandStatus status, string reason, float rechargeSeconds, InventoryWithMoney? inventory)
    {
        b.Write((byte)status);
        if (status != CommandStatus.Accepted)
        {
            b.Write(reason);
            return;
        }
        b.Write(rechargeSeconds);
        inventory!.Write(b);
    }

    /// <summary>BuySkillReply (16011, reader 2474): skill points, the book/skill that triggers the "learned" popup, the skill list.</summary>
    public static void WriteBuySkill(NetBuffer b, int skillPoints, int learnedTemplateId, IReadOnlyList<SkillEntry> skills)
    {
        b.WriteVariableInt32(skillPoints);
        b.WriteVariableInt32(learnedTemplateId);
        b.WriteVariableInt32(skills.Count);
        foreach (var s in skills)
        {
            b.WriteVariableInt32(s.SkillId);
            b.WriteVariableInt32(s.Level);
            b.WriteVariableInt32(s.MaxLevel);
            b.Write(s.TimeRemaining);
        }
    }
}

/// <summary>SkillBarUpdate (16002), client → server (reader 5507 is the client's writer): set id and its slots.</summary>
public sealed record SkillBarUpdate(int SetId, IReadOnlyList<IdValue> Slots)
{
    public static SkillBarUpdate Read(NetBuffer b)
    {
        var setId = b.ReadVariableInt32();
        var slots = new List<IdValue>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++) slots.Add(new IdValue(b.ReadVariableInt32(), b.ReadVariableInt32()));
        return new SkillBarUpdate(setId, slots);
    }
}
