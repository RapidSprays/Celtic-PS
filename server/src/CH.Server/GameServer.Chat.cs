using System.Globalization;
using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Chat and GM commands. Commands are typed in normal (local) chat with a "." prefix: the client sends "/" commands
/// encrypted with a client key, which the server deliberately doesn't embed (docs/decisions.md). Every account is a GM
/// on this private server.
/// </summary>
public sealed partial class GameServer
{
    private static readonly string Help = string.Join("\n",
        "GM commands:",
        ".where - your zone and position",
        ".spawn <mob id or name> [level] - hostile mob here (saved)",
        ".npc <mob id or name> [conv <id>] - friendly NPC here, facing you (saved)",
        ".near [radius] - spawns and mobs around you",
        ".delete <spawn id> | .delete nearest - remove a saved spawn",
        ".find <name> - search mob templates",
        ".heal | .level <n> | .item <template id> [qty] | .learn <skill id>",
        ".respawn - respawn every mob in the zone");

    private void OnChat(Session s, ChatMessage chat)
    {
        var text = chat.Text.Trim();
        if (text.StartsWith('.'))
        {
            try
            {
                RunCommand(s, text[1..].Trim());
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                Tell(s, $"Command failed: {ex.Message}");
            }
            return;
        }

        // The client sends its channel prefixes ("s ", "c ", "g ") as part of the text.
        var channel = chat.Channel;
        if (channel is ChatChannel.Zone or ChatChannel.Clan or ChatChannel.Group && text.Length > 2 && text[1] == ' ') text = text[2..];
        if (channel is not (ChatChannel.Local or ChatChannel.Zone)) channel = ChatChannel.Local;
        if (!_zones.TryGetValue(s.Position.Zone, out var zone)) return;
        Broadcast(zone, Opcode.GeneralChat, new ChatMessage(channel, s.Name, text).WriteToClient);
    }

    /// <summary>A private system line for one player (SimpleMessageForThePlayer, 13026).</summary>
    private void Tell(Session s, string text)
    {
        foreach (var line in text.Split('\n')) Send(s, Opcode.SimpleMessageForThePlayer, b => b.Write(line));
    }

    private void RunCommand(Session s, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            Tell(s, Help);
            return;
        }
        var args = parts[1..];
        var zone = ZoneFor(s.Position.Zone);
        var now = NetTime.Now;
        var (zoneId, x, y, z, dir) = s.Position;
        _log.LogInformation("GM {Name}: .{Command}", s.Name, command);

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                Tell(s, Help);
                break;

            case "where":
                var zoneName = _balance.Zones.TryGetValue(zoneId, out var zi) ? zi.Name : "?";
                Tell(s, $"Zone {zoneId} ({zoneName}) at x={x:F1} y={y:F1} z={z:F1}, facing {dir:F0}");
                break;

            case "find":
                var matches = FindMobs(string.Join(' ', args)).Take(10).ToList();
                Tell(s, matches.Count == 0 ? "No mob templates match." : string.Join("\n", matches.Select(m => $"{m.Id}: {m.Name} ({m.Race}, {m.Power} stars)")));
                break;

            case "spawn":
            case "npc":
            {
                var npc = parts[0].Equals("npc", StringComparison.OrdinalIgnoreCase);
                var (template, rest) = ResolveMob(args);
                if (template is null)
                {
                    Tell(s, $"Usage: .{parts[0]} <mob id or name>. Try .find first.");
                    break;
                }
                var level = npc ? 0 : rest.Length > 0 && int.TryParse(rest[0], out var l) ? Math.Clamp(l, 1, Progression.MaxLevel) : 1;
                var conv = -1;
                if (npc)
                {
                    var convIndex = Array.FindIndex(rest, r => r.Equals("conv", StringComparison.OrdinalIgnoreCase));
                    conv = convIndex >= 0 && convIndex + 1 < rest.Length ? int.Parse(rest[convIndex + 1], CultureInfo.InvariantCulture)
                        : _balance.ConversationFor(template)?.Id ?? -1;
                }
                // NPCs face the player who placed them; monsters face the same way as the player.
                var facing = npc ? (dir + 180) % 360 : dir;
                var record = _store.AddSpawn(new SpawnRecord(0, zoneId, template.Id, x, y, z, facing, npc ? 100 : 0, level, conv,
                    RespawnSeconds: 45, RoamRadius: npc ? 0 : 5, s.Name));
                var mob = MobFromSpawn(zone, record, now)!;
                zone.Mobs[mob.ServerId] = mob;
                Broadcast(zone, Opcode.ZoneMonsterAppeared, ToWire(mob, now).Write);
                var convText = conv >= 0 ? $", conversation {conv} ({_balance.Conversations.FirstOrDefault(c => c.Id == conv)?.Filename})" : "";
                Tell(s, $"Spawn #{record.Id}: {template.Name}{(npc ? "" : $" level {level}")}{convText}. Step away to see it.");
                break;
            }

            case "near":
            {
                var radius = args.Length > 0 ? float.Parse(args[0], CultureInfo.InvariantCulture) : 25f;
                var near = zone.Mobs.Values
                    .Select(m => (Mob: m, Pos: m.PositionAt(now)))
                    .Select(m => (m.Mob, Dist: MathF.Sqrt((m.Pos.X - x) * (m.Pos.X - x) + (m.Pos.Z - z) * (m.Pos.Z - z))))
                    .Where(m => m.Dist <= radius)
                    .OrderBy(m => m.Dist)
                    .Take(12)
                    .Select(m => $"{(m.Mob.SpawnRecordId is { } id ? $"spawn #{id}" : "auto")}: {m.Mob.Template.Name}" +
                                 $"{(m.Mob.IsNpc ? " (NPC)" : $" L{m.Mob.Level} {m.Mob.State}")} {m.Dist:F0}m");
                var lines = near.ToList();
                Tell(s, lines.Count == 0 ? $"Nothing within {radius:F0}m." : string.Join("\n", lines));
                break;
            }

            case "delete":
            {
                Mob? target;
                if (args.Length > 0 && args[0].Equals("nearest", StringComparison.OrdinalIgnoreCase))
                {
                    target = zone.Mobs.Values.Where(m => m.SpawnRecordId is not null)
                        .OrderBy(m => { var p = m.PositionAt(now); return (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z); })
                        .FirstOrDefault();
                }
                else
                {
                    var id = args.Length > 0 ? long.Parse(args[0].TrimStart('#'), CultureInfo.InvariantCulture) : -1;
                    target = zone.Mobs.Values.FirstOrDefault(m => m.SpawnRecordId == id);
                }
                if (target?.SpawnRecordId is not { } spawnId)
                {
                    Tell(s, "No saved spawn found. Auto-placed monsters can't be deleted; .near lists spawn ids.");
                    break;
                }
                _store.DeleteSpawn(spawnId);
                zone.Mobs.Remove(target.ServerId);
                foreach (var p in zone.Players.Where(p => ReferenceEquals(p.AttackTarget, target))) p.AttackTarget = null;
                Broadcast(zone, Opcode.ZoneMonsterDisappeared, b => b.WriteVariableUInt32(target.ServerId));
                Tell(s, $"Deleted spawn #{spawnId} ({target.Template.Name}).");
                break;
            }

            case "heal":
                s.Health = s.MaxHealth;
                s.Energy = s.MaxEnergy;
                Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.Health, s.Health), (StatKey.Energy, s.Energy)]).Write);
                Tell(s, "Healed.");
                break;

            case "level":
            {
                var level = Math.Clamp(int.Parse(args[0], CultureInfo.InvariantCulture), 1, Progression.MaxLevel);
                var progress = _store.GetProgress(s.Character!.Id);
                _store.SaveProgress(s.Character.Id, level, 0, progress.Coins);
                var sheet = RefreshSheet(s);
                s.Health = s.MaxHealth;
                s.Energy = s.MaxEnergy;
                Send(s, Opcode.LevelUp, new LevelUp(level).Write);
                Send(s, Opcode.StatsUpdate, sheet.Stats().Write);
                Tell(s, $"You are now level {level}.");
                break;
            }

            case "item":
            {
                var templateId = int.Parse(args[0], CultureInfo.InvariantCulture);
                var qty = args.Length > 1 ? Math.Clamp(int.Parse(args[1], CultureInfo.InvariantCulture), 1, 999) : 1;
                if (!_balance.Items.TryGetValue(templateId, out var info))
                {
                    Tell(s, $"No item template {templateId}.");
                    break;
                }
                _store.AddItem(s.Character!.Id, templateId, qty);
                var sheet = RefreshSheet(s);
                Send(s, Opcode.InventoryUpdate, sheet.InventoryWithMoney().Write);
                Tell(s, $"Added {qty} x {info.Name}.");
                break;
            }

            case "learn":
            {
                var skillId = int.Parse(args[0], CultureInfo.InvariantCulture);
                if (!_balance.Skills.TryGetValue(skillId, out var skill))
                {
                    Tell(s, $"No skill {skillId}.");
                    break;
                }
                _store.LearnSkill(s.Character!.Id, skillId);
                var sheet = RefreshSheet(s);
                Send(s, Opcode.SkillListUpdate, new SkillListUpdate(0, sheet.SkillEntries()).Write);
                Tell(s, $"Learned {skill.Name}.");
                break;
            }

            case "respawn":
                foreach (var mob in zone.Mobs.Values.ToList())
                {
                    if (mob.State != MobState.Dead) Broadcast(zone, Opcode.ZoneMonsterDisappeared, b => b.WriteVariableUInt32(mob.ServerId));
                    mob.Reset(now);
                }
                foreach (var p in zone.Players) p.AttackTarget = null;
                var all = zone.Mobs.Values.Select(m => ToWire(m, now)).ToList();
                Broadcast(zone, Opcode.BatchZoneMonsterUpdate, b => MobSpawn.WriteBatch(b, all));
                Tell(s, $"Respawned {all.Count} mobs.");
                break;

            default:
                Tell(s, $"Unknown command .{parts[0]}. Type .help");
                break;
        }
    }

    private IEnumerable<MobInfo> FindMobs(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return _balance.Mobs.Values
            .Where(m => m.Type == 0 && m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name.Length)
            .ThenBy(m => m.Id);
    }

    /// <summary>
    /// Resolves "70003 3", "Private Duncan" or "scrawny coast wolf 2" to a template plus the remaining options. The name is
    /// every word before the first number or "conv".
    /// </summary>
    private (MobInfo? Template, string[] Options) ResolveMob(string[] args)
    {
        if (args.Length == 0) return (null, args);
        if (int.TryParse(args[0], out var id)) return (_balance.Mobs.GetValueOrDefault(id), args[1..]);
        var nameLength = Array.FindIndex(args, a => int.TryParse(a, out _) || a.Equals("conv", StringComparison.OrdinalIgnoreCase));
        if (nameLength < 0) nameLength = args.Length;
        var name = string.Join(' ', args[..nameLength]);
        static string Bare(string n) => n.Contains(']') ? n[(n.IndexOf(']') + 1)..].Trim() : n;
        var template = _balance.Mobs.Values.Where(m => m.Type == 0)
                           .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || Bare(m.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
                       ?? FindMobs(name).FirstOrDefault();
        return (template, args[nameLength..]);
    }
}
