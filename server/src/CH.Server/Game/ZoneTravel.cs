using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// One zone exit from the client's Zone&lt;N&gt;_zone_points table (scripts/extract-zone-points.py). The client shows
/// the travel prompt inside the cylinder and sends CharacterZoningRequest with <see cref="Id"/>; it checks
/// <see cref="MinLevel"/> and <see cref="Quest"/> itself before sending.
/// </summary>
public sealed record ZoneExit(int Id, int Zone, int MinLevel, int DestZone, float X, float Y, float Z, float Radius, int Quest);

/// <summary>Where a player appears: position and facing (degrees, Unity yaw).</summary>
public readonly record struct Arrival(int Zone, float X, float Y, float Z, float Direction);

/// <summary>
/// Zone exits and arrival points. The client data has no arrival positions (the live server kept them in server-only
/// spawn points), so arrival is <b>approximated</b>: just outside the destination's return portal, stepping towards
/// its nearest leystone, so the player doesn't land in the portal's trigger.
/// </summary>
public sealed class ZoneTravel
{
    /// <summary>
    /// Destinations with two return portals: which portal each exit pairs with. The client doesn't say; these pair
    /// Lir's Reach's two Farcrag Castle doors with the castle's two doors in the same order.
    /// </summary>
    private static readonly Dictionary<(int Zone, int Exit), int> PairOverrides = new()
    {
        [(2, 4)] = 9930, [(2, 26)] = 9931,
        [(93, 9930)] = 4, [(93, 9931)] = 26,
    };

    private const float ArrivalMargin = 3f;

    private readonly Balance _balance;
    private readonly Dictionary<int, List<ZoneExit>> _exits;

    private ZoneTravel(Balance balance, Dictionary<int, List<ZoneExit>> exits)
    {
        _balance = balance;
        _exits = exits;
        GateQuests = exits.Values.SelectMany(e => e).Where(e => e.Quest >= 0).Select(e => e.Quest).ToHashSet();
    }

    public static ZoneTravel Empty(Balance balance) => new(balance, new());

    /// <summary>Reads zone-points.json; a missing file means no walkable exits (teleports and .goto still work).</summary>
    public static ZoneTravel Load(string? path, Balance balance)
    {
        var exits = new Dictionary<int, List<ZoneExit>>();
        if (path is not null && File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var zone in doc.RootElement.GetProperty("zones").EnumerateObject())
            {
                var zoneId = int.Parse(zone.Name);
                exits[zoneId] = zone.Value.EnumerateArray().Select(e => new ZoneExit(e.GetProperty("id").GetInt32(), zoneId,
                    e.GetProperty("minLevel").GetInt32(), e.GetProperty("destZone").GetInt32(), e.GetProperty("x").GetSingle(),
                    e.GetProperty("y").GetSingle(), e.GetProperty("z").GetSingle(), e.GetProperty("radius").GetSingle(),
                    e.GetProperty("quest").GetInt32())).ToList();
            }
        }
        return new ZoneTravel(balance, exits);
    }

    public int ExitCount => _exits.Values.Sum(e => e.Count);

    /// <summary>
    /// Quests that gate an exit. Quests aren't implemented, so the server reports these as completed (the client
    /// refuses gated exits locally otherwise).
    /// </summary>
    public IReadOnlySet<int> GateQuests { get; }

    public IReadOnlyList<ZoneExit> ExitsOf(int zone) => _exits.TryGetValue(zone, out var e) ? e : [];

    public ZoneExit? Exit(int zone, int id) => ExitsOf(zone).FirstOrDefault(e => e.Id == id);

    /// <summary>Where walking through <paramref name="exit"/> puts the player.</summary>
    public Arrival ArrivalFor(ZoneExit exit)
    {
        var returns = ExitsOf(exit.DestZone).Where(e => e.DestZone == exit.Zone).ToList();
        var back = PairOverrides.TryGetValue((exit.Zone, exit.Id), out var pairedId)
            ? returns.FirstOrDefault(e => e.Id == pairedId)
            : returns.FirstOrDefault();
        return back is null ? Entry(exit.DestZone) : OutsidePortal(back);
    }

    /// <summary>Default arrival in a zone (.goto, one-way exits): its first leystone, else outside any exit, else the centre.</summary>
    public Arrival Entry(int zone)
    {
        if (_balance.Zones.TryGetValue(zone, out var info) && info.PlayerSpawns.Count > 0)
        {
            var p = info.PlayerSpawns[0];
            return new Arrival(zone, p.X, p.Y, p.Z, 0);
        }
        if (ExitsOf(zone).FirstOrDefault(e => e.Y > -500) is { } exit) return OutsidePortal(exit);
        return info is null ? new Arrival(zone, 0, 0, 0, 0)
            : new Arrival(zone, (info.Min.X + info.Max.X) / 2, 0, (info.Min.Z + info.Max.Z) / 2, 0);
    }

    /// <summary>A leystone's position, or null when the zone has no such spawn point.</summary>
    public SpawnPoint? Leystone(int zone, int spawnPointId) =>
        _balance.Zones.TryGetValue(zone, out var info) ? info.PlayerSpawns.FirstOrDefault(p => p.Id == spawnPointId) : null;

    public SpawnPoint? NearestLeystone(int zone, float x, float z) =>
        _balance.Zones.TryGetValue(zone, out var info)
            ? info.PlayerSpawns.OrderBy(p => (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z)).FirstOrDefault()
            : null;

    /// <summary>Just outside <paramref name="portal"/>, towards the zone's nearest leystone (or its centre), facing away from the portal.</summary>
    private Arrival OutsidePortal(ZoneExit portal)
    {
        float tx, ty, tz;
        if (NearestLeystone(portal.Zone, portal.X, portal.Z) is { } stone) (tx, ty, tz) = (stone.X, stone.Y, stone.Z);
        else if (_balance.Zones.TryGetValue(portal.Zone, out var info)) (tx, ty, tz) = ((info.Min.X + info.Max.X) / 2, portal.Y, (info.Min.Z + info.Max.Z) / 2);
        else (tx, ty, tz) = (portal.X, portal.Y, portal.Z + 1);
        var dx = tx - portal.X;
        var dz = tz - portal.Z;
        var dist = MathF.Sqrt(dx * dx + dz * dz);
        var push = portal.Radius + ArrivalMargin;
        if (dist < 0.01f) (dx, dz, dist) = (0, 1, 1);
        var facing = (MathF.Atan2(dx, dz) * 180 / MathF.PI + 360) % 360;
        // A leystone just past the portal is a known walkable spot: arrive there.
        if (dist <= push + 2) return new Arrival(portal.Zone, tx, ty, tz, facing);
        return new Arrival(portal.Zone, portal.X + dx / dist * push, portal.Y, portal.Z + dz / dist * push, facing);
    }
}
