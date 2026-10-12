using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Zone exits, leystone discovery and fast travel (docs/protocol-notes.md §6). A zone change is CorrectPlayerPosition
/// with another zone id: the client unloads, loads the new scene and sends FinishedZoning, which
/// <see cref="EnterZone"/> answers with the zone snapshot. Nothing for the new zone is sent before that, because the
/// client wipes zone content while it loads.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>How close (metres, 2D) a player must come to a leystone to discover it. <b>Approximated.</b></summary>
    private const float DiscoverRadius = 12f;

    /// <summary>Slack on the exit's radius when checking the player really stands in it.</summary>
    private const float ExitTolerance = 4f;

    private void OnZoningRequest(Session s, ZoningRequest req)
    {
        var (zoneId, x, _, z, _) = s.Position;
        var exit = _travel.Exit(zoneId, req.ZonePointId);
        if (exit is null || s.Dead || !s.InZone)
        {
            _log.LogWarning("{Name}: zoning request for exit {Exit} in zone {Zone} refused", s.Name, req.ZonePointId, zoneId);
            return;
        }
        var near = Math.Min(Dist2D(req.X, req.Z, exit.X, exit.Z), Dist2D(x, z, exit.X, exit.Z));
        if (near > exit.Radius + ExitTolerance)
        {
            Tell(s, "You are too far from the way through.");
            return;
        }
        if (s.Sheet!.Level < exit.MinLevel)
        {
            Tell(s, $"You are too low level to travel to {ZoneName(exit.DestZone)}.");
            return;
        }
        ChangeZone(s, _travel.ArrivalFor(exit));
    }

    private void OnTeleportRequest(Session s, TeleportRequest req)
    {
        var stone = _travel.Leystone((int)req.Zone, (int)req.SpawnPointId);
        string? problem = stone is null ? "That leystone doesn't exist."
            : s.Dead ? "You can't travel while dead."
            : !stone.FreeTravel && !s.Teleports.Contains(stone.Id) ? "You haven't discovered that leystone yet."
            : null;
        if (problem is not null)
        {
            // The client opened its loading screen before asking, so always answer with a position.
            Tell(s, problem);
            var (zoneId, x, y, z, dir) = s.Position;
            Send(s, Opcode.CorrectPlayerPosition, new CorrectPlayerPosition((uint)zoneId, s.CharacterId, x, y, z, dir).Write);
            return;
        }
        ChangeZone(s, new Arrival((int)req.Zone, stone!.X, stone.Y, stone.Z, s.Position.Direction));
    }

    /// <summary>CharacterStuck (Options → Stuck): back to the zone's nearest leystone.</summary>
    private void OnStuck(Session s)
    {
        if (s.Dead) return;
        var (zoneId, x, _, z, dir) = s.Position;
        var arrival = _travel.NearestLeystone(zoneId, x, z) is { } stone
            ? new Arrival(zoneId, stone.X, stone.Y, stone.Z, dir)
            : _travel.Entry(zoneId);
        ChangeZone(s, arrival);
    }

    /// <summary>Moves a player: a snap inside the zone, or a full zone change that completes on FinishedZoning.</summary>
    private void ChangeZone(Session s, Arrival to)
    {
        if (s.Character is null) return;
        var from = s.Position.Zone;
        if (to.Zone != from && s.InZone) LeaveZone(s, changingZone: true);
        s.Position = (to.Zone, to.X, to.Y, to.Z, to.Direction);
        _store.SavePosition(s.Character.Id, to.Zone, to.X, to.Y, to.Z, to.Direction);
        Send(s, Opcode.CorrectPlayerPosition, new CorrectPlayerPosition((uint)to.Zone, s.CharacterId, to.X, to.Y, to.Z, to.Direction).Write);
        if (to.Zone != from)
            _log.LogInformation("{Name} travels from zone {From} to {To} ({Zone}) at ({X:F1}, {Z:F1})", s.Name, from, to.Zone, ZoneName(to.Zone), to.X, to.Z);
        else
            DiscoverLeystones(s);
    }

    /// <summary>Leystones are discovered by walking near them; the client only reacts to PlayerSpawnPointDiscovered.</summary>
    private void DiscoverLeystones(Session s)
    {
        var (zoneId, x, _, z, _) = s.Position;
        if (s.Character is null || !_balance.Zones.TryGetValue(zoneId, out var zone)) return;
        foreach (var stone in zone.PlayerSpawns)
        {
            if (s.Teleports.Contains(stone.Id) || Dist2D(x, z, stone.X, stone.Z) > DiscoverRadius) continue;
            DiscoverLeystone(s, stone);
        }
    }

    private void DiscoverLeystone(Session s, SpawnPoint stone)
    {
        if (!s.Teleports.Add(stone.Id)) return;
        _store.AddTeleport(s.Character!.Id, stone.Id);
        Send(s, Opcode.PlayerSpawnPointDiscovered, b => b.WriteVariableInt32(stone.Id));
        _log.LogInformation("{Name} discovered leystone {Id} ({Stone})", s.Name, stone.Id, stone.Name);
    }

    private string ZoneName(int zoneId) => _balance.Zones.TryGetValue(zoneId, out var z) ? z.Name : $"zone {zoneId}";

    private static float Dist2D(float x1, float z1, float x2, float z2) => MathF.Sqrt((x1 - x2) * (x1 - x2) + (z1 - z2) * (z1 - z2));

    /// <summary>Zones players can visit: the live ones plus the Global Arena (reached from Farcrag Castle).</summary>
    private IEnumerable<ZoneInfo> TravelZones() =>
        _balance.Zones.Values.Where(z => z.Accessibility == "L" || z.Id is 8 or 9).OrderBy(z => z.Id);

    /// <summary>.goto target: a zone id or name, then optionally a leystone name.</summary>
    private (Arrival? To, string? Problem) ResolveGoto(string[] args)
    {
        if (args.Length == 0) return (null, "Usage: .goto <zone id or name> [leystone]. .zones lists them.");
        var zones = TravelZones().ToList();
        ZoneInfo? zone = null;
        var used = 0;
        if (int.TryParse(args[0], out var id))
        {
            _balance.Zones.TryGetValue(id, out zone);
            used = 1;
        }
        else
        {
            // Longest zone-name prefix of the arguments: ".goto lir's reach eastern dock".
            for (var n = args.Length; n >= 1 && zone is null; n--)
            {
                var name = string.Join(' ', args[..n]);
                zone = zones.FirstOrDefault(z => z.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? zones.FirstOrDefault(z => z.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    ?? zones.FirstOrDefault(z => z.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                used = n;
            }
        }
        if (zone is null) return (null, $"No zone matches \"{string.Join(' ', args)}\". .zones lists them.");
        var rest = string.Join(' ', args[used..]);
        if (rest.Length == 0) return (_travel.Entry(zone.Id), null);
        var stone = zone.PlayerSpawns.FirstOrDefault(p => p.Name.Contains(rest, StringComparison.OrdinalIgnoreCase));
        return stone is null
            ? (null, $"{zone.Name} has no leystone matching \"{rest}\": {string.Join(", ", zone.PlayerSpawns.Select(p => p.Name))}")
            : (new Arrival(zone.Id, stone.X, stone.Y, stone.Z, 0), null);
    }
}
