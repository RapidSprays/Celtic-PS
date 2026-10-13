using CH.Protocol;
using CH.Server.Game;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Faction reputation (FactionsMessage 23002, FactionsNetworkHandler; docs/economy-notes.md). The client keeps the
/// points we send and works out levels itself (FACTION_GREATER_THAN conversation conditions, the Abilities page bars).
/// Point sources are our own (<see cref="FactionRules"/>).
/// </summary>
public sealed partial class GameServer
{
    private const int FactionsCurrent = 1, FactionsPointsChanged = 3, FactionsLevelChanged = 4;

    /// <summary>currentFactions: every shown faction, 0 when untouched (a missing entry fails most conversation tests).</summary>
    private void SendFactions(Session s)
    {
        var points = _store.GetFactions(s.Character!.Id);
        var shown = _factionRules.Shown.Union(points.Keys).Where(_balance.Factions.ContainsKey).Order().ToList();
        Send(s, Opcode.FactionsMessage, b =>
        {
            b.WriteVariableUInt32(FactionsCurrent);
            b.WriteVariableInt32(shown.Count);
            foreach (var f in shown)
            {
                b.WriteVariableInt32(f);
                b.WriteVariableInt32(points.GetValueOrDefault(f));
            }
        });
    }

    private void AdjustFaction(Session s, int factionId, int delta)
    {
        if (delta == 0 || !_balance.Factions.TryGetValue(factionId, out var faction)) return;
        var c = s.Character!;
        var before = _store.GetFactions(c.Id).GetValueOrDefault(factionId);
        var after = Math.Clamp((long)before + delta, faction.Min, faction.Max);
        if (after == before) return;
        _store.SetFaction(c.Id, factionId, (int)after);
        Send(s, Opcode.FactionsMessage, b =>
        {
            b.WriteVariableUInt32(FactionsPointsChanged);
            b.WriteVariableInt32(factionId);
            b.WriteVariableInt32((int)after);
            b.WriteVariableInt32((int)after - before);
        });
        var (oldLevel, newLevel) = (faction.LevelAt(before), faction.LevelAt((int)after));
        if (newLevel is null || newLevel.Value == oldLevel?.Value) return;
        Send(s, Opcode.FactionsMessage, b =>
        {
            b.WriteVariableUInt32(FactionsLevelChanged);
            b.WriteVariableInt32(factionId);
            b.WriteVariableInt32((int)after);
            b.Write((byte)(newLevel.Value > (oldLevel?.Value ?? 0) ? 1 : 0));
            b.Write(faction.Name);
            b.Write(newLevel.Title);
        });
        _log.LogInformation("{Name} is now {Title} with {Faction} ({Points})", s.Name, newLevel.Title, faction.Name, after);
    }

    private void FactionQuestReward(Session s, QuestInfo quest)
    {
        foreach (var (faction, points) in _factionRules.ForQuest(quest)) AdjustFaction(s, faction, points);
    }

    private void FactionKill(Session s)
    {
        foreach (var (faction, points) in _factionRules.ForKill(s.Position.Zone)) AdjustFaction(s, faction, points);
    }
}
