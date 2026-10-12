using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Pets, mount dismounting, the Mount Whistle, paid NPC services and token vendors (docs/pets-mounts-notes.md).
/// A pet shows (for everyone) while its owner has a "Well Fed" effect; unfed it gets "Hungry", which hides it and
/// greys out its skill. Feeding time only runs while the owner is online.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>A newly equipped pet arrives fed for this long. <b>Approximated.</b></summary>
    private const double NewPetFedSeconds = 1800;
    private const double HungryShownSeconds = 7 * 24 * 3600;
    private const double MaxFedSeconds = 24 * 3600;

    private ItemInfo? EquippedPet(Session s) =>
        s.Sheet?.Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Companion) is { } p ? _balance.Items.GetValueOrDefault(p.TemplateId) : null;

    private ActiveEffect? NewEffect(int effectId, int levelIndex, double expiresAt) =>
        _balance.StatusEffects.TryGetValue(effectId, out var info)
            ? new ActiveEffect { Info = info, LevelIndex = Math.Clamp(levelIndex, 0, Math.Max(0, info.Levels.Count - 1)), ExpiresAt = expiresAt, NextTickAt = double.MaxValue }
            : null;

    /// <summary>Permanent effects (riding) are shown for 30 days and renewed whenever they're re-applied.</summary>
    private const double PermanentSeconds = 30 * 24 * 3600;

    /// <summary>
    /// The "Mounted" buff while riding: run speed at the mount's own rate (its description), shown as the client's
    /// Mounted icon. Gone while Dismounted or without a mount.
    /// </summary>
    private void UpdateMountEffect(Session s)
    {
        var mount = s.Sheet?.Mount;
        var wanted = mount is not null && !Dismounted(s) ? Pets.MountedFor(mount, _balance) : ((int, int)?)null;
        var current = s.Effects.FirstOrDefault(e => Pets.MountEffects.Contains(e.Info.Id));
        if (current is not null && wanted is { } w && current.Info.Id == w.Item1 && current.LevelIndex == w.Item2) return;
        s.Effects.RemoveAll(e => Pets.MountEffects.Contains(e.Info.Id));
        if (wanted is { } add && NewEffect(add.Item1, add.Item2, NetTime.Now + PermanentSeconds) is { } effect) s.Effects.Add(effect);
    }

    /// <summary>Keeps exactly the right pet effect on the player: the pet's Well Fed, else its Hungry; none without a pet. Also the mount's.</summary>
    private void UpdatePetEffects(Session s, bool send = true)
    {
        var now = NetTime.Now;
        var pet = EquippedPet(s);
        var before = s.Effects.Where(e => Pets.AllPetEffects.Contains(e.Info.Id) || Pets.MountEffects.Contains(e.Info.Id))
            .Select(e => (e.Info.Id, e.LevelIndex)).OrderBy(i => i).ToList();
        UpdateMountEffect(s);
        if (pet is null)
        {
            s.Effects.RemoveAll(e => Pets.AllPetEffects.Contains(e.Info.Id));
            s.PetTemplate = null;
        }
        else
        {
            var (hungry, fed) = Pets.FamilyOf(pet);
            // Another family's effects go (a different pet was equipped).
            s.Effects.RemoveAll(e => Pets.AllPetEffects.Contains(e.Info.Id) && e.Info.Id != hungry && e.Info.Id != fed && e.Info.Id != Pets.NewFedEffect);
            if (s.PetTemplate != pet.Id)
            {
                if (!s.Effects.Any(e => Pets.IsFedEffect(e.Info.Id)) && NewEffect(fed, 0, now + NewPetFedSeconds) is { } welcome)
                    s.Effects.Add(welcome);
                s.PetTemplate = pet.Id;
            }
            if (s.Effects.Any(e => Pets.IsFedEffect(e.Info.Id))) s.Effects.RemoveAll(e => e.Info.Id == hungry);
            else if (s.Effects.All(e => e.Info.Id != hungry) && NewEffect(hungry, 0, now + HungryShownSeconds) is { } h) s.Effects.Add(h);
        }
        var after = s.Effects.Where(e => Pets.AllPetEffects.Contains(e.Info.Id) || Pets.MountEffects.Contains(e.Info.Id))
            .Select(e => (e.Info.Id, e.LevelIndex)).OrderBy(i => i).ToList();
        if (send && !before.SequenceEqual(after)) SendEffects(s);
        SendSkillToggles(s);
    }

    private bool PetFed(Session s) => s.Effects.Any(e => Pets.IsFedEffect(e.Info.Id));

    private bool Dismounted(Session s) => s.Effects.Any(e => e.Info.Id == Pets.DismountedEffect);

    /// <summary>
    /// CompanionSkillToggle / MountSkillToggle (<c>int32 skillId, bit enabled</c>): grey out the pet's skill while it's
    /// hungry and the mount's while dismounted. The client resets these on every skill list, so they follow each one.
    /// </summary>
    private void SendSkillToggles(Session s)
    {
        if (EquippedPet(s) is { EquipSkillId: > 0 } pet)
            Send(s, Opcode.CompanionSkillToggle, b => { b.Write(pet.EquipSkillId); b.Write(PetFed(s)); });
        if (s.Sheet?.Mount is { EquipSkillId: > 0 } mount)
            Send(s, Opcode.MountSkillToggle, b => { b.Write(mount.EquipSkillId); b.Write(!Dismounted(s)); });
    }

    /// <summary>Pet food: refreshes the pet's Well Fed time (added up, at most a day). Returns false if it isn't pet food.</summary>
    private bool TryFeedPet(Session s, ItemRecord item, ItemInfo food)
    {
        if (food.Subtype != "PetFood" && Pets.FedEffectOf(food) is null) return false;
        string? problem = null;
        var pet = EquippedPet(s);
        var fedEffect = Pets.FedEffectOf(food) ?? Pets.NewFedEffect;
        if (pet is null) problem = "You have no pet to feed.";
        else if (fedEffect != Pets.NewFedEffect && fedEffect != Pets.FamilyOf(pet).Fed) problem = "That food is for a different kind of pet.";
        else if (fedEffect != Pets.NewFedEffect && pet.StarLevel > food.SkillLinkLevel + 1) problem = "That food isn't hearty enough for your pet.";
        if (problem is not null)
        {
            Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Rejected, problem, 0, null));
            return true;
        }
        var now = NetTime.Now;
        var remaining = s.Effects.Where(e => Pets.IsFedEffect(e.Info.Id)).Select(e => e.ExpiresAt - now).DefaultIfEmpty(0).Max();
        s.Effects.RemoveAll(e => Pets.IsFedEffect(e.Info.Id));
        var level = fedEffect == Pets.NewFedEffect ? Math.Max(0, food.SkillLinkLevel - 1) : 0;
        if (NewEffect(fedEffect, level, now + Math.Min(MaxFedSeconds, Math.Max(0, remaining) + Pets.FedSeconds(food))) is { } fed) s.Effects.Add(fed);
        _store.SetItemQuantity(item.Id, item.Quantity - 1);
        var sheet = RefreshSheet(s);
        Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Accepted, "", ConsumableRechargeSeconds, sheet.InventoryWithMoney()));
        UpdatePetEffects(s, send: false);
        SendEffects(s);
        return true;
    }

    /// <summary>Dying on a mount leaves the player Dismounted for an hour (or until a whistle or the stablemaster).</summary>
    private void DismountOnDeath(Session s)
    {
        if (s.Sheet?.Mount is null || Dismounted(s)) return;
        if (NewEffect(Pets.DismountedEffect, 0, NetTime.Now + Pets.DismountedSeconds) is { } e) s.Effects.Add(e);
        UpdateMountEffect(s); // the Mounted speed buff goes with the mount
    }

    private bool Remount(Session s)
    {
        if (s.Effects.RemoveAll(e => e.Info.Id == Pets.DismountedEffect) == 0) return false;
        UpdateMountEffect(s);
        SendEffects(s);
        SendSkillToggles(s);
        return true;
    }

    /// <summary>Mount Whistle: only while dismounted; consumed on use.</summary>
    private bool TryWhistle(Session s, ItemRecord item, ItemInfo info)
    {
        if (info.SkillLinkId != Pets.WhistleSkill) return false;
        if (!Dismounted(s))
        {
            Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Rejected, "Your mount is already with you.", 0, null));
            return true;
        }
        _store.SetItemQuantity(item.Id, item.Quantity - 1);
        var sheet = RefreshSheet(s);
        Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Accepted, "", 0f, sheet.InventoryWithMoney()));
        Remount(s);
        return true;
    }

    /// <summary>
    /// CastSkillOnPlayer (13014, a conversation's CASTSKILL action): <c>vi npcServerId, vi skillId, vi gold</c>. The
    /// stablemaster's 8065 brings a dismounted mount back; other skills are paid buffs.
    /// </summary>
    private void OnCastSkillOnPlayer(Session s, int skillId, int price)
    {
        var coins = _store.GetProgress(s.Character!.Id).Coins;
        if (price < 0 || price > coins)
        {
            Tell(s, "You don't have enough gold.");
            return;
        }
        if (skillId == Pets.StableRemountSkill)
        {
            if (!Dismounted(s))
            {
                Tell(s, "Your mount is fine.");
                return;
            }
            Remount(s);
        }
        else if (_balance.Skills.TryGetValue(skillId, out var skill) && skill.StatusEffectId > 0)
        {
            ApplyEffect(s, skill, 0, NetTime.Now);
        }
        else
        {
            Tell(s, "Nothing happens.");
            return;
        }
        _store.AddCurrency(s.Character.Id, -price, 0);
        Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
    }

    // ---- feeding time across sessions -------------------------------------------------------------------------

    private void SavePetFeed(Session s)
    {
        if (s.Character is null) return;
        var now = NetTime.Now;
        var fed = s.Effects.FirstOrDefault(e => Pets.IsFedEffect(e.Info.Id));
        _store.SavePetFeed(s.Character.Id, fed?.Info.Id ?? 0, fed?.LevelIndex ?? 0, fed is null ? 0 : Math.Max(0, (int)(fed.ExpiresAt - now)));
    }

    private void RestorePetFeed(Session s)
    {
        var (effect, level, seconds) = _store.GetPetFeed(s.Character!.Id);
        s.PetTemplate = EquippedPet(s)?.Id;
        if (effect > 0 && seconds > 0 && NewEffect(effect, level, NetTime.Now + seconds) is { } e) s.Effects.Add(e);
        UpdatePetEffects(s, send: false);
    }

    // ---- token vendors (TokenVendorMessage 23006) ---------------------------------------------------------------

    private void OnTokenVendorMessage(Session s, NetIncomingMessage msg)
    {
        switch (msg.ReadVariableInt32())
        {
            case 1:
                SendTokenVendor(s, msg.ReadVariableInt32());
                break;
            case 3:
            {
                var vendorId = msg.ReadVariableInt32();
                var stockId = msg.ReadVariableInt32();
                var quantity = Math.Clamp(msg.ReadVariableInt32(), 1, 10);
                BuyFromTokenVendor(s, vendorId, stockId, quantity);
                break;
            }
        }
    }

    /// <summary>Stock reply: <c>varint 2, byte hasId, varint id, varint n × {stockId, template, qty, costId}, varint m × costs</c>.</summary>
    private void SendTokenVendor(Session s, int vendorId)
    {
        var stock = _tokenVendors.Vendors.TryGetValue(vendorId, out var v) ? v.Stock : [];
        Send(s, Opcode.TokenVendorMessage, b =>
        {
            b.WriteVariableInt32(2);
            b.Write((byte)1);
            b.WriteVariableInt32(vendorId);
            b.WriteVariableInt32(stock.Count);
            if (stock.Count == 0)
            {
                b.Write("This vendor has nothing to offer yet.");
                b.WriteVariableInt32(0);
                return;
            }
            foreach (var e in stock)
            {
                b.WriteVariableInt32(e.StockId);
                b.WriteVariableInt32(e.Template);
                b.WriteVariableInt32(1);
                b.WriteVariableInt32(e.StockId); // one cost entry per stock entry
            }
            b.WriteVariableInt32(stock.Count);
            foreach (var e in stock)
            {
                b.WriteVariableInt32(e.StockId);
                b.WriteVariableInt32(e.Cost.Count);
                foreach (var (template, qty) in e.Cost)
                {
                    b.WriteVariableInt32(template);
                    b.WriteVariableInt32(qty);
                }
            }
        });
    }

    /// <summary>Purchase: hand in the cost items (backpack only), receive the stock item. Reply <c>varint 4, byte ok, …</c>.</summary>
    private void BuyFromTokenVendor(Session s, int vendorId, int stockId, int quantity)
    {
        var entry = _tokenVendors.Vendors.TryGetValue(vendorId, out var v) ? v.Stock.FirstOrDefault(e => e.StockId == stockId) : null;
        var c = s.Character!;
        var backpack = _store.ListItems(c.Id).Where(i => i.Slot == ItemRecord.Backpack).ToList();
        string? problem = entry is null ? "That isn't offered here."
            : entry.Cost.Any(cost => backpack.Where(i => i.TemplateId == cost.Template).Sum(i => i.Quantity) < cost.Quantity * quantity)
                ? "You don't have what it costs. Items must be in your backpack, not equipped."
                : null;
        if (problem is not null)
        {
            Send(s, Opcode.TokenVendorMessage, b => { b.WriteVariableInt32(4); b.Write((byte)0); b.Write(problem); });
            return;
        }
        _store.Atomically(() =>
        {
            foreach (var (template, qty) in entry!.Cost) TakeItems(c.Id, template, qty * quantity);
            GrantItem(c.Id, entry.Template, quantity);
        });
        var sheet = RefreshSheet(s);
        var newItem = sheet.Backpack.Where(i => i.TemplateId == entry!.Template).MaxBy(i => i.Id);
        Send(s, Opcode.TokenVendorMessage, b =>
        {
            b.WriteVariableInt32(4);
            b.Write((byte)1);
            b.WriteVariableInt32((int)(newItem?.Id ?? 0));
            sheet.InventoryWithMoney().Write(b);
        });
        _log.LogInformation("{Name} got {Item} from token vendor {Vendor}", s.Name, _balance.Items.GetValueOrDefault(entry!.Template)?.Name, vendorId);
    }
}
