using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

// The aggro meter (private message 31003, drawn by the client mod's AggroMeter on the left of the screen): the threat
// list of the monster you're fighting, twice a second while it changes. ".aggro off|on" hides or shows it.
public sealed partial class GameServer
{
    private const double AggroMeterInterval = 0.5;
    private const int AggroMeterRows = 8;

    private void TickAggroMeter(Zone zone, Session s, double now)
    {
        if (now < s.AggroMeterNextAt) return;
        s.AggroMeterNextAt = now + AggroMeterInterval;
        var mob = s.Dead || !s.AggroMeterOn ? null : AggroMeterMob(zone, s);
        if (mob is null)
        {
            if (s.AggroMeterLast is null) return;
            s.AggroMeterLast = null;
            Send(s, PrivateOpcode.AggroMeter, b => b.Write((byte)0));
            return;
        }
        var rows = mob.Threat.Where(t => t.Key is Session { InZone: true } p && zone.Players.Contains(p) && t.Value > 0)
            .OrderByDescending(t => t.Value).Take(AggroMeterRows)
            .Select(t => (Player: (Session)t.Key, Threat: (int)Math.Round(t.Value))).ToList();
        if (rows.Count == 0 && mob.Threat.Count == 0)
        {
            if (s.AggroMeterLast is null) return;
            s.AggroMeterLast = null;
            Send(s, PrivateOpcode.AggroMeter, b => b.Write((byte)0));
            return;
        }
        var target = mob.Target as Session;
        var key = $"{mob.ServerId}|{string.Join(",", rows.Select(r => $"{r.Player.CharacterId}:{r.Threat}:{ReferenceEquals(r.Player, target)}"))}";
        if (key == s.AggroMeterLast) return;
        s.AggroMeterLast = key;
        Send(s, PrivateOpcode.AggroMeter, b =>
        {
            b.Write((byte)1);
            b.Write(mob.Template.Name);
            b.WriteVariableInt32(rows.Count);
            foreach (var (player, threat) in rows)
            {
                b.Write(player.Name);
                b.WriteVariableInt32(threat);
                b.Write((byte)((ReferenceEquals(player, target) ? 1 : 0) | (ReferenceEquals(player, s) ? 2 : 0)));
            }
        });
    }

    /// <summary>The monster the meter shows: the one you're attacking, else the one fighting you, else any with you on its list.</summary>
    private static Mob? AggroMeterMob(Zone zone, Session s)
    {
        static bool Alive(Mob m) => m.State != MobState.Dead && !m.Removed && !m.IsNpc;
        if (s.AttackTarget is { } t && Alive(t) && t.Threat.Count > 0) return t;
        Mob? listed = null;
        foreach (var m in zone.Mobs.Values)
        {
            if (!Alive(m) || !m.Threat.ContainsKey(s)) continue;
            if (ReferenceEquals(m.Target, s)) return m;
            if (listed is null || m.Threat[s] > listed.Threat[s]) listed = m;
        }
        return listed;
    }

    /// <summary>
    /// The aggro meter's look from server/content/aggro-meter.json (sizes, position, colours), sent at log-in so the
    /// client mod draws it that way: edit and restart the server, no mod rebuild.
    /// </summary>
    private void SendAggroMeterStyle(Session s)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "content", "aggro-meter.json");
        if (!File.Exists(path)) return;
        List<(string, string)> pairs;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path), new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true });
            pairs = doc.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_'))
                .Select(p => (p.Name, p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText())).ToList();
        }
        catch (Exception e)
        {
            _log.LogWarning("aggro-meter.json unreadable: {Error}", e.Message);
            return;
        }
        Send(s, PrivateOpcode.AggroMeterStyle, b =>
        {
            b.WriteVariableInt32(pairs.Count);
            foreach (var (k, v) in pairs)
            {
                b.Write(k);
                b.Write(v);
            }
        });
    }

    private void AggroCommand(Session s, string[] args)
    {
        s.AggroMeterOn = args.Length == 0 ? !s.AggroMeterOn : !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
        s.AggroMeterNextAt = 0;
        Tell(s, s.AggroMeterOn ? "Aggro meter on." : "Aggro meter off.");
    }
}
