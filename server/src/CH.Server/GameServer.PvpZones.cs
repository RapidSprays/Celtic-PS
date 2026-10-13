using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// PvP zones and PvP progress (docs/content-systems-notes.md). The client attacks another player only when they're
/// on its PlayerHateList (17011), so hostility is entirely the server's: in the Global Arena (zone 8, entered from
/// Farcrag Castle) everyone outside your party is an enemy; on the Clan Battleground (zone 14, entered with
/// <c>.battleground</c>) everyone outside your clan. Deaths there are real; the killer earns PvP experience toward the
/// next rank, and both records feed the rating. Rules and numbers are <b>our own</b>.
/// </summary>
public sealed partial class GameServer
{
    public const int ArenaZone = 8, BattlegroundZone = 14;

    private static bool IsPvpZone(int zoneId) => zoneId is ArenaZone or BattlegroundZone;

    /// <summary>Enemies by zone rule (never yourself, your party, or—on the battleground—your clan).</summary>
    private bool ZoneHostile(Session a, Session b)
    {
        if (ReferenceEquals(a, b) || !a.InZone || !b.InZone || a.Dead || b.Dead || a.Position.Zone != b.Position.Zone || !IsPvpZone(a.Position.Zone)) return false;
        if (a.Party is not null && ReferenceEquals(a.Party, b.Party)) return false;
        return a.Position.Zone != BattlegroundZone || a.ClanId is null || a.ClanId != b.ClanId;
    }

    /// <summary>A player this one may attack right now: their started duel opponent, or a zone enemy.</summary>
    private Session? Enemy(Session s, int targetId)
    {
        if (s.Duel is { Started: true } duel && duel.Other(s).CharacterId == targetId) return duel.Other(s);
        return OnlineByCharacter(targetId) is { } target && ZoneHostile(s, target) ? target : null;
    }

    private void RefreshHateList(Session s)
    {
        if (s.Character is null) return;
        var enemies = new List<int>();
        if (s.Duel is { Started: true } duel) enemies.Add(duel.Other(s).CharacterId);
        if (s.InZone && _zones.TryGetValue(s.Position.Zone, out var zone))
            enemies.AddRange(zone.Players.Where(p => ZoneHostile(s, p)).Select(p => p.CharacterId));
        var list = enemies.Distinct().ToList();
        Send(s, Opcode.PlayerHateList, b =>
        {
            b.WriteVariableInt32(list.Count);
            foreach (var id in list) b.WriteVariableUInt32((uint)id);
        });
    }

    /// <summary>Someone arrived, left, died, respawned or changed party or clan: hate lists in a PvP zone follow.</summary>
    private void RefreshZoneHate(int zoneId)
    {
        if (!IsPvpZone(zoneId) || !_zones.TryGetValue(zoneId, out var zone)) return;
        foreach (var p in zone.Players.ToList()) RefreshHateList(p);
    }

    /// <summary>A PvP-zone kill: experience and rank for the killer, the record for both, and word to the zone.</summary>
    private void PvpKill(Zone zone, Session killer, Session victim)
    {
        var (kc, vc) = (killer.Character!, victim.Character!);
        _store.AddStat(kc.Id, "pvp_kills", 1);
        _store.AddStat(vc.Id, "pvp_deaths", 1);
        var (rank, xp) = _store.GetPvp(kc.Id);
        var gained = PvpRanks.KillXp(killer.Sheet!.Level, victim.Sheet!.Level, victim.Sheet.PvpLevel);
        var (newRank, newXp) = PvpRanks.Add(rank, xp, gained);
        _store.SavePvp(kc.Id, newRank, newXp);
        UpdatePvpStanding(killer);
        UpdatePvpStanding(victim);
        foreach (var p in zone.Players.ToList()) Tell(p, $"{killer.Name} defeated {victim.Name}.");
        _log.LogInformation("PvP: {Killer} killed {Victim} in zone {Zone} (+{Xp} PvP xp)", killer.Name, victim.Name, zone.Id, gained);
    }

    /// <summary>Rank or rating changed: the player's own client announces it (AppearanceOnlyUpdate), the stats page bar
    /// follows (stat 31) and others' nameplates update.</summary>
    private void UpdatePvpStanding(Session s)
    {
        var before = (s.Sheet!.PvpLevel, s.Sheet.PvpRating);
        var sheet = RefreshSheet(s);
        Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.PvpExperience, sheet.PvpProgress)]).Write);
        if (before != (sheet.PvpLevel, sheet.PvpRating)) SendOwnAppearance(s); // also redraws them for others
    }

    /// <summary><c>.battleground</c>: clan members go to the Clan Battleground; from there it leads back to Farcrag Castle.</summary>
    private void BattlegroundCommand(Session s)
    {
        if (s.Position.Zone == BattlegroundZone)
        {
            ChangeZone(s, _travel.Entry(93));
            return;
        }
        if (s.ClanId is null)
        {
            Tell(s, "Only clan members can enter the Clan Battleground.");
            return;
        }
        if (s.Dead || s.Duel is not null)
        {
            Tell(s, "Not right now.");
            return;
        }
        Tell(s, "Entering the Clan Battleground: everyone outside your clan is an enemy. Type .battleground again to leave.");
        ChangeZone(s, _travel.Entry(BattlegroundZone));
    }
}
