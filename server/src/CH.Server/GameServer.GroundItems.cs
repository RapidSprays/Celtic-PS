using System.Text.Json;
using CH.Protocol;
using CH.Protocol.Messages;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Items lying on the ground: quest pickups and cooking crops (docs/gathering-notes.md; points from
/// scripts/gen-item-spawns.py). ItemSpawns (15009) lists them on zone entry, because the client clears them on every
/// zone unload. A pickup (PickupItem 13015, sent from within 1 m) answers with PickupItemReply (13016) and removes the
/// item for everyone (ItemDespawn 15011); it grows back after 60-120 s (ItemRespawn 15010). Positions are
/// <b>our own</b>; the client drops each item onto the ground below its point.
/// </summary>
public sealed partial class GameServer
{
    private const float PickupRange = 4f;

    private sealed record ItemPoint(int Id, float X, float Y, float Z, IReadOnlyList<(int Template, int Weight)> Items, int RespawnMin, int RespawnMax);

    private sealed class GroundItem
    {
        public required ItemPoint Point { get; init; }
        public int Template { get; set; }
        public double? RespawnAt { get; set; }
    }

    private readonly IReadOnlyDictionary<int, List<ItemPoint>> _itemPoints;

    private static IReadOnlyDictionary<int, List<ItemPoint>> LoadItemPoints(string? path)
    {
        var zones = new Dictionary<int, List<ItemPoint>>();
        if (path is null || !File.Exists(path)) return zones;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var z in doc.RootElement.GetProperty("zones").EnumerateObject())
        {
            var zoneId = int.Parse(z.Name);
            zones[zoneId] = z.Value.EnumerateArray().Select((p, i) => new ItemPoint(zoneId * 1000 + i, p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(),
                p.GetProperty("z").GetSingle(), p.GetProperty("items").EnumerateArray().Select(x => (x[0].GetInt32(), x[1].GetInt32())).ToList(),
                p.GetProperty("respawn")[0].GetInt32(), p.GetProperty("respawn")[1].GetInt32())).ToList();
        }
        return zones;
    }

    private int RollGroundItem(ItemPoint p)
    {
        var total = p.Items.Sum(i => i.Weight);
        var roll = Random.Shared.Next(Math.Max(1, total));
        foreach (var (template, weight) in p.Items)
        {
            if (roll < weight) return template;
            roll -= weight;
        }
        return p.Items[0].Template;
    }

    private Dictionary<int, GroundItem> GroundItemsFor(int zoneId) =>
        (_itemPoints.GetValueOrDefault(zoneId) ?? []).Where(p => _balance.Items.ContainsKey(p.Items[0].Template))
            .ToDictionary(p => p.Id, p => new GroundItem { Point = p, Template = RollGroundItem(p) });

    private static void WriteGroundItem(NetBuffer b, GroundItem g)
    {
        b.WriteVariableInt32(g.Point.Id);
        b.WriteVariableInt32(g.Template);
        b.Write(g.Point.X);
        b.Write(g.Point.Y);
        b.Write(g.Point.Z);
    }

    private void SendGroundItems(Zone zone, Session s)
    {
        var lying = zone.GroundItems.Values.Where(g => g.RespawnAt is null).ToList();
        if (lying.Count == 0) return;
        Send(s, Opcode.ItemSpawns, b =>
        {
            b.WriteVariableInt32(lying.Count);
            foreach (var g in lying) WriteGroundItem(b, g);
        });
    }

    private void OnPickupItem(Session s, int spawnId)
    {
        void Refuse(string why) => Send(s, Opcode.PickupItemReply, b => { b.Write((byte)0); b.Write(why); });
        if (!_zones.TryGetValue(s.Position.Zone, out var zone) || !zone.GroundItems.TryGetValue(spawnId, out var g) || g.RespawnAt is not null)
        {
            Refuse("Someone got there first.");
            return;
        }
        if (Dist2D(s.Position.X, s.Position.Z, g.Point.X, g.Point.Z) > PickupRange)
        {
            Refuse("That is too far away.");
            return;
        }
        var template = g.Template;
        g.RespawnAt = NetTime.Now + g.Point.RespawnMin + Random.Shared.NextDouble() * (g.Point.RespawnMax - g.Point.RespawnMin);
        GrantItem(s.Character!.Id, template, 1);
        var sheet = RefreshSheet(s);
        Send(s, Opcode.PickupItemReply, b =>
        {
            b.Write((byte)1);
            b.WriteVariableInt32(template);
            b.WriteVariableInt32(1);
            sheet.InventoryWithMoney().Write(b);
        });
        Broadcast(zone, Opcode.ItemDespawn, b => b.WriteVariableInt32(spawnId));
        QuestGathered(s, template, 1);
    }

    private void TickGroundItems(Zone zone, double now)
    {
        foreach (var g in zone.GroundItems.Values.Where(g => g.RespawnAt <= now).ToList())
        {
            g.RespawnAt = null;
            g.Template = RollGroundItem(g.Point);
            Broadcast(zone, Opcode.ItemRespawn, b => WriteGroundItem(b, g));
        }
    }
}
