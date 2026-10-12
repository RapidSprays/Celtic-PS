namespace CH.Server.Game;

/// <summary>A cluster of monsters placed around one of the zone's player spawn points (leystones).</summary>
public sealed record Camp(int Anchor, float OffsetX, float OffsetZ, float Radius, int Count, int[] Templates, int MinLevel, int MaxLevel, float RoamRadius = 6f);

/// <summary>
/// Auto-population of monsters. The client ships no spawn positions or mob levels, so these camps are <b>approximated</b>:
/// which mobs live in a zone comes from the zone's quest chain (kill objectives in text.db) and MobSets; levels follow
/// quest order and star power; positions are offsets from the zone's real leystones. Placement is deterministic per zone
/// so mobs stand in the same places after a restart. Named NPCs are placed by hand with GM commands instead.
/// </summary>
public static class ZonePopulation
{
    public const uint FirstAutoServerId = 30000;

    public static readonly IReadOnlyDictionary<int, Camp[]> Camps = new Dictionary<int, Camp[]>
    {
        // Lir's Reach (zone 2). Anchors are PlayerSpawnPoints ids; Highshore Village (2003) stays safe.
        [2] =
        [
            new(2003, 45, 30, 12, 5, [70003, 70004], 1, 2),               // scrawny wolves outside the village
            new(2002, 25, 35, 12, 5, [70000], 2, 3),                      // direhounds by the guard tower
            new(2002, 40, 10, 8, 2, [70002], 4, 4),                       // beastwalkers
            new(2004, 30, 25, 15, 6, [70108, 70118], 3, 5),               // coast and grey wolves, western road
            new(2004, 45, 40, 4, 1, [70020], 6, 6, RoamRadius: 3),         // Alpha Snarl
            new(2006, 30, -25, 14, 6, [70129, 70130], 5, 7),              // grave robbers at the temple
            new(2006, 20, -40, 6, 2, [70128], 6, 6),                      // tomb burglars
            new(2006, 40, -45, 4, 1, [70132], 8, 8, RoamRadius: 3),        // pillager
            new(2005, 30, 30, 15, 7, [70009, 70122, 70167], 6, 9),        // Flintblade camp, southern pass
            new(2005, 45, 45, 4, 1, [70124], 10, 10, RoamRadius: 3),       // cutthroat
            new(2007, -35, 25, 15, 6, [70030, 70126], 8, 11),             // Overlook outlaws, eastern dock
            new(2007, -50, 40, 4, 1, [70031], 12, 12, RoamRadius: 3),      // outlaw brute
            new(2008, -30, -30, 15, 6, [70110, 70120], 6, 8),             // prowlers, northern peninsula
            new(2008, 30, 30, 12, 5, [70015, 70016, 70133], 10, 13),      // Skullcrusher picts
            new(2008, 40, 20, 6, 2, [70171], 10, 10),                     // warhounds
            new(2008, 50, -40, 15, 4, [70087, 70116], 13, 16),            // Jotun
            new(2008, 70, -55, 3, 1, [70022], 20, 20, RoamRadius: 0),      // Jotun Warbringer (boss)
        ],
    };

    /// <summary>Builds the monsters for a zone. Skips templates the client data doesn't have.</summary>
    public static List<Mob> Build(int zoneId, Balance balance, double now)
    {
        var mobs = new List<Mob>();
        if (!Camps.TryGetValue(zoneId, out var camps) || !balance.Zones.TryGetValue(zoneId, out var zone)) return mobs;
        var rng = new Random(zoneId * 7919);
        var serverId = FirstAutoServerId + (uint)zoneId * 1000;
        foreach (var camp in camps)
        {
            var anchor = zone.PlayerSpawns.FirstOrDefault(p => p.Id == camp.Anchor);
            if (anchor == default) continue;
            for (var i = 0; i < camp.Count; i++)
            {
                var templateId = camp.Templates[rng.Next(camp.Templates.Length)];
                var level = rng.Next(camp.MinLevel, camp.MaxLevel + 1);
                var angle = rng.NextDouble() * Math.PI * 2;
                var dist = Math.Sqrt(rng.NextDouble()) * camp.Radius;
                var x = anchor.X + camp.OffsetX + (float)(Math.Cos(angle) * dist);
                var z = anchor.Z + camp.OffsetZ + (float)(Math.Sin(angle) * dist);
                if (zone.Max.X - zone.Min.X > 20 && zone.Max.Z - zone.Min.Z > 20)
                {
                    x = Math.Clamp(x, zone.Min.X + 5, zone.Max.X - 5);
                    z = Math.Clamp(z, zone.Min.Z + 5, zone.Max.Z - 5);
                }
                if (!balance.Mobs.TryGetValue(templateId, out var template)) continue;
                var mob = new Mob
                {
                    ServerId = serverId++,
                    Template = template,
                    Level = level,
                    HomeX = x,
                    HomeY = anchor.Y,
                    HomeZ = z,
                    Direction = (float)(rng.NextDouble() * 360),
                    Opinion = 0,
                    AggroRange = MobStats.AggroRange(template.Power),
                    RoamRadius = camp.RoamRadius,
                };
                mob.Reset(now);
                mobs.Add(mob);
            }
        }
        return mobs;
    }
}
