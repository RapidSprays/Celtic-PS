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
    /// regen, so regen gear shows as a buff. Display only: the tick applies the exact amounts.
    /// </summary>
    private void UpdateGearRegenEffects(Session s)
    {
        var gear = s.Sheet?.GearRegenPerTick ?? default;
        var wanted = new List<(int Effect, int Amount)>();
        if (gear.Health + gear.HealthOutOfCombat > 0) wanted.Add((Sigils.GearHealthRegenEffect, gear.Health + gear.HealthOutOfCombat));
        if (gear.Energy + gear.EnergyOutOfCombat > 0) wanted.Add((Sigils.GearEnergyRegenEffect, gear.Energy + gear.EnergyOutOfCombat));
        var levels = wanted.Select(w => (w.Effect, Level: _balance.StatusEffects.TryGetValue(w.Effect, out var info) && info.Levels.Count > 0
            ? Enumerable.Range(0, info.Levels.Count).MinBy(i => Math.Abs(info.Levels[i].Amount - w.Amount)) : 0)).ToList();
        var current = s.Effects.Where(e => e.DisplayOnly).Select(e => (e.Info.Id, e.LevelIndex)).OrderBy(x => x).ToList();
        if (current.SequenceEqual(levels.OrderBy(x => x))) return;
        s.Effects.RemoveAll(e => e.DisplayOnly);
        foreach (var (effect, level) in levels)
            if (_balance.StatusEffects.TryGetValue(effect, out var info))
                s.Effects.Add(new ActiveEffect { Info = info, LevelIndex = level, ExpiresAt = NetTime.Now + PermanentSeconds, NextTickAt = double.MaxValue, DisplayOnly = true });
    }
}
