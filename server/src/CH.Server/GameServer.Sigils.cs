using CH.Protocol;
using CH.Server.Game;
using Lidgren.Network;

namespace CH.Server;

/// <summary>Sigils of Health and Energy, and the permanent regen icons for regen gear (docs/decisions.md).</summary>
public sealed partial class GameServer
{
    /// <summary>
    /// Sigils are applied to the character when they're received ("this item will not appear in your inventory") and
    /// counted: SigilsCount (23005) answers the stats page. Crates (34781/34782) open as chests with five sigils each.
    /// </summary>
    private bool TryGrantSigils(long characterId, int templateId, int quantity)
    {
        if (TryGrantExpansion(characterId, templateId, quantity) || TryGrantCurrencyItem(characterId, templateId, quantity)) return true;
        if (templateId is not (Sigils.Health or Sigils.Energy) || quantity <= 0) return false;
        var (health, energy) = templateId == Sigils.Health ? (quantity, 0) : (0, quantity);
        _store.AddSigils(characterId, health, energy);
        if (Online.FirstOrDefault(p => p.Character?.Id == characterId) is { } s)
        {
            s.SigilsOfHealth += health;
            s.SigilsOfEnergy += energy;
            SendSigils(s);
            Tell(s, $"{quantity} {(health > 0 ? "Sigil" : "Sigil")}{(quantity == 1 ? "" : "s")} of {(health > 0 ? "Health" : "Energy")} applied: " +
                $"{(health > 0 ? s.SigilsOfHealth : s.SigilsOfEnergy)} in total, +{Sigils.RegenPerSigil} {(health > 0 ? "health" : "energy")} per tick out of combat each.");
        }
        return true;
    }

    /// <summary>SigilsCount (NetworkManager.processSigilCountsMessage): varint energy, varint health.</summary>
    private void SendSigils(Session s) => Send(s, Opcode.SigilsCount, b =>
    {
        b.WriteVariableInt32(s.SigilsOfEnergy);
        b.WriteVariableInt32(s.SigilsOfHealth);
    });

    /// <summary>
    /// The client's permanent "Health/Energy Regen Boost Amulet" effects (62/63), at the level nearest the gear's total
    /// regen, so regen gear shows as a buff, the permanent "Attack Speed Boost" (61) for haste gear, plus each completed set's aura. Display only: the tick and the character sheet
    /// apply the real amounts.
    /// </summary>
    /// <summary>"Fingals Shield": protection from Mordris's Fingal's Bane (skill 2215, its burn 2147).</summary>
    private const int FingalsShieldEffect = 2148, FingalsBaneSkill = 2215, FingalsBaneEffect = 2147;

    /// <summary>
    /// The Dragonlord Aura (2100-2104) grants Fingal's Shield; the owner's rule (2026-10-16) gives it with the Exalted
    /// Aura (5348) too.
    /// </summary>
    private static bool HasFingalsShield(Session s) => s.Sheet?.AuraEffects.Any(a => a is >= 2100 and <= 2104 or 5348) == true;

    /// <summary>The client's permanent regen effects (health, energy) for gear worn in a slot.</summary>
    private static (int Health, int Energy) RegenIcons(EquipSlot slot) => slot switch
    {
        EquipSlot.RingR1 or EquipSlot.RingR2 or EquipSlot.RingL1 or EquipSlot.RingL2 => (117, 118),
        EquipSlot.BangleR or EquipSlot.BangleL => (119, 120),
        EquipSlot.Offhand => (69, 70),
        EquipSlot.Head => (115, 116),
        EquipSlot.Mount => (158, 159),
        _ => (Sigils.GearHealthRegenEffect, Sigils.GearEnergyRegenEffect),
    };

    private void UpdateGearRegenEffects(Session s)
    {
        // One health and one energy icon per kind of item, as on live: the client's "Health/Energy Regen Boost Amulet",
        // "… Ring", "… Bracelet", "… Offhand" (quivers, books), "… Helm" and "… Mount" effects, each at the level nearest that
        // kind's total. Anything else (charms, armour, weapons, pets) shows on the amulet icons.
        var wanted = new List<(int Effect, int Amount)>();
        foreach (var kind in (s.Sheet?.GearRegenBySlot ?? []).GroupBy(r => RegenIcons(r.Slot)))
        {
            if (kind.Sum(r => r.Health) is > 0 and var health) wanted.Add((kind.Key.Health, health));
            if (kind.Sum(r => r.Energy) is > 0 and var energy) wanted.Add((kind.Key.Energy, energy));
        }
        // Haste gear (Ring of Haste, Spectral Ring of the Reaper, haste quivers): the best one's percent, as the sheet applies it.
        if (s.Sheet?.GearHastePercent is > 0 and var haste) wanted.Add((Sigils.GearHasteEffect, haste));
        var levels = wanted.Select(w => (w.Effect, Level: _balance.StatusEffects.TryGetValue(w.Effect, out var info) && info.Levels.Count > 0
            ? Enumerable.Range(0, info.Levels.Count).MinBy(i => Math.Abs(info.Levels[i].Amount - w.Amount)) : 0)).ToList();
        // Set bonuses: the set's own aura effect ("Exalted Aura"), so it shows under the health bar with its description.
        levels.AddRange((s.Sheet?.AuraEffects ?? []).Distinct().Select(effect => (effect, 0)));
        if (HasFingalsShield(s)) levels.Add((FingalsShieldEffect, 0));
        var current = s.Effects.Where(e => e.DisplayOnly).Select(e => (e.Info.Id, e.LevelIndex)).OrderBy(x => x).ToList();
        if (current.SequenceEqual(levels.OrderBy(x => x))) return;
        s.Effects.RemoveAll(e => e.DisplayOnly);
        foreach (var (effect, level) in levels)
            if (_balance.StatusEffects.TryGetValue(effect, out var info))
                s.Effects.Add(new ActiveEffect { Info = info, LevelIndex = level, ExpiresAt = NetTime.Now + PermanentSeconds, NextTickAt = double.MaxValue, DisplayOnly = true });
    }
}
