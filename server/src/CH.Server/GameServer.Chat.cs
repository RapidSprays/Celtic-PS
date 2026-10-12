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
        ".level <n> | .level +<n> | .level -<n> - set or change your level",
        ".xp <amount> - gain experience (levels up as it goes)",
        ".give <item id or name> [qty] - put items in your backpack (.item is the same)",
        ".finditem <name> - search item templates",
        ".chest [name] [qty] - a random legacy chest, or chests by name (e.g. .chest mystery chest 5)",
        ".gearset [level] - equip the best gear your class can wear at that level (old gear goes to your backpack)",
        ".wear <item name or id> - give and equip an item",
        ".mount [name] | .mount off - ride a mount (random if no name), or get off",
        ".heal | .learn <skill id>",
        ".respawn - respawn every mob in the zone",
        ".zones - list zones | .goto <zone id or name> [leystone] - travel there",
        ".leystones - discover every leystone (for map fast travel)");

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

            case "zones":
                Tell(s, string.Join("\n", TravelZones().Select(zz =>
                    $"{zz.Id}: {zz.Name}" + (zz.PlayerSpawns.Count > 0 ? $" - {string.Join(", ", zz.PlayerSpawns.Select(p => p.Name))}" : ""))));
                break;

            case "goto":
            {
                var (to, problem) = ResolveGoto(args);
                if (to is null)
                {
                    Tell(s, problem!);
                    break;
                }
                Tell(s, $"Travelling to {ZoneName(to.Value.Zone)}.");
                ChangeZone(s, to.Value);
                break;
            }

            case "leystones":
            {
                var before = s.Teleports.Count;
                foreach (var stone in TravelZones().SelectMany(zz => zz.PlayerSpawns)) DiscoverLeystone(s, stone);
                Tell(s, $"Discovered {s.Teleports.Count - before} leystones; open the map to fast travel.");
                break;
            }

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
                if (args.Length == 0)
                {
                    Tell(s, $"You are level {s.Sheet!.Level}. Usage: .level <n>, .level +<n> or .level -<n>");
                    break;
                }
                var current = s.Sheet!.Level;
                var arg = args[0];
                var value = int.Parse(arg.TrimStart('+'), CultureInfo.InvariantCulture);
                var level = Math.Clamp(arg.StartsWith('+') || arg.StartsWith('-') ? current + value : value, 1, Progression.MaxLevel);
                var progress = _store.GetProgress(s.Character!.Id);
                _store.SaveProgress(s.Character.Id, level, 0, progress.Coins);
                RefreshSheet(s);
                AnnounceLevel(s, level);
                Tell(s, $"Level {current} -> {level}.");
                break;
            }

            case "xp":
            {
                var amount = int.Parse(args[0], CultureInfo.InvariantCulture);
                var before = s.Sheet!.Level;
                var level = AddXp(s, amount, out var sheet);
                if (level != before) AnnounceLevel(s, level);
                else SendLiveStats(s);
                Tell(s, $"+{amount} xp: level {level}, {sheet.XpPercent:F0}% to next.");
                break;
            }

            case "finditem":
            {
                var query = string.Join(' ', args);
                var found = _balance.Items.Values
                    .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.Name.Length).ThenBy(i => i.Id).Take(12)
                    .Select(i => $"{i.Id}: {i.Name}{(i.Slot >= 0 ? $" ({i.Slot})" : "")}")
                    .ToList();
                Tell(s, found.Count == 0 ? "No items match." : string.Join("\n", found));
                break;
            }

            case "gearset":
            {
                var level = args.Length > 0 ? Math.Clamp(int.Parse(args[0], CultureInfo.InvariantCulture), 1, Progression.MaxLevel) : s.Sheet!.Level;
                var cls = (CharacterClass)s.Character!.Class;
                var plan = GearPlanner.Plan(_balance, cls, level, s.Character.Gender);
                foreach (var old in s.Sheet!.Equipped.Where(i => i.Slot < (int)EquipSlot.FashHead)) _store.SetItemSlot(old.Id, ItemRecord.Backpack);
                foreach (var (slot, info) in plan) _store.AddItem(s.Character.Id, info.Id, 1, slot);
                SendEquipment(s);
                Tell(s, $"Level {level} {cls} gear: " + string.Join(", ", plan.OrderBy(p => p.Key).Select(p => p.Value.Name)));
                break;
            }

            case "mount":
            {
                var current = s.Sheet!.Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Mount);
                if (args.Length > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    if (current is null) { Tell(s, "You aren't riding anything."); break; }
                    _store.SetItemSlot(current.Id, ItemRecord.Backpack);
                    SendEquipment(s);
                    Tell(s, "You dismount.");
                    break;
                }
                var query = string.Join(' ', args);
                var mounts = _balance.Items.Values.Where(i => i.Slot == EquipSlot.Mount && !i.IsPlaceholder).ToList();
                var mount = query.Length == 0 ? mounts[Random.Shared.Next(mounts.Count)]
                    : mounts.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Name.Length).ThenBy(i => i.Id).FirstOrDefault();
                if (mount is null) { Tell(s, $"No mount matches \"{query}\" ({mounts.Count} mounts exist)."); break; }
                if (current is not null) _store.SetItemSlot(current.Id, ItemRecord.Backpack);
                _store.AddItem(s.Character!.Id, mount.Id, 1, (int)EquipSlot.Mount);
                SendEquipment(s);
                Tell(s, $"You mount your {mount.Name} ({mount.Id}).");
                break;
            }

            case "wear":
            {
                var query = string.Join(' ', args);
                var item = int.TryParse(query, out var wearId) ? _balance.Items.GetValueOrDefault(wearId)
                    : _balance.Items.Values.Where(i => i.Slot >= EquipSlot.Weapon && !i.IsPlaceholder && i.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Id).FirstOrDefault()
                      ?? _balance.Items.Values.Where(i => i.Slot >= EquipSlot.Weapon && !i.IsPlaceholder && i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Name.Length).ThenBy(i => i.Id).FirstOrDefault();
                if (item is null || item.Slot < EquipSlot.Weapon)
                {
                    Tell(s, "Usage: .wear <item name or id> (equipment only). Try .finditem first.");
                    break;
                }
                // Same slot rules as equipping by hand: the occupant and blocked slots go to the backpack.
                var slot = (int)item.Slot;
                if (item.Slot is EquipSlot.RingR1 or EquipSlot.RingR2 or EquipSlot.RingL1 or EquipSlot.RingL2)
                    slot = Enumerable.Range(8, 4).FirstOrDefault(i => s.Sheet!.Equipped.All(e => e.Slot != i), 8);
                if (item.Slot is EquipSlot.BangleR or EquipSlot.BangleL)
                    slot = Enumerable.Range(12, 2).FirstOrDefault(i => s.Sheet!.Equipped.All(e => e.Slot != i), 12);
                foreach (var occupant in s.Sheet!.Equipped)
                {
                    var blocks = _balance.Items.GetValueOrDefault(occupant.TemplateId)?.BlockedSlots ?? [];
                    if (occupant.Slot == slot || item.BlockedSlots.Contains(occupant.Slot) || blocks.Contains(slot)) _store.SetItemSlot(occupant.Id, ItemRecord.Backpack);
                }
                _store.AddItem(s.Character!.Id, item.Id, 1, slot);
                SendEquipment(s);
                var note = RequirementProblem(s.Sheet!, item, s.Character) is { } p ? $" (GM override: {p})" : "";
                Tell(s, $"Equipped {item.Name} ({item.Id}).{note}");
                break;
            }

            case "chest":
            {
                var qtyGiven = args.Length > 0 && int.TryParse(args[^1], out _);
                var qty = qtyGiven ? Math.Clamp(int.Parse(args[^1], CultureInfo.InvariantCulture), 1, 100) : 1;
                var query = string.Join(' ', qtyGiven ? args[..^1] : args);
                var chests = ChestCatalog.Chests.Values.Where(c => !_balance.Items[c.ItemId].IsPlaceholder).ToList();
                var chest = query.Length == 0
                    ? chests[Random.Shared.Next(chests.Count)]
                    : chests.FirstOrDefault(c => c.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                      ?? chests.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name.Length).FirstOrDefault();
                if (chest is null)
                {
                    Tell(s, $"No chest matches \"{query}\". {chests.Count} legacy chests exist; try .chest mystery, .chest pirate, .chest bounty.");
                    break;
                }
                GrantItem(s.Character!.Id, chest.ItemId, qty);
                var sheet = RefreshSheet(s);
                Send(s, Opcode.InventoryUpdate, sheet.InventoryWithMoney().Write);
                Tell(s, $"Added {qty} x {chest.Name}. Use it from your backpack to open it.");
                break;
            }

            case "give":
            case "item":
            {
                // ".give 10040 5", ".give minor health potion 5", ".give Tempered Small Sword"
                var qtyGiven = args.Length > 1 && int.TryParse(args[^1], out var q) && !(args.Length == 1);
                var nameArgs = qtyGiven ? args[..^1] : args;
                var qty = qtyGiven ? Math.Clamp(int.Parse(args[^1], CultureInfo.InvariantCulture), 1, 999) : 1;
                ItemInfo? info;
                if (nameArgs.Length == 1 && int.TryParse(nameArgs[0], out var templateId)) info = _balance.Items.GetValueOrDefault(templateId);
                else
                {
                    // Several templates can share a name (three "Minor Health Potion"s): prefer one already carried, then the lowest id.
                    var name = string.Join(' ', nameArgs);
                    var carried = s.Sheet!.Items.Select(i => i.TemplateId).ToHashSet();
                    info = _balance.Items.Values.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                               .OrderByDescending(i => carried.Contains(i.Id)).ThenBy(i => i.Id).FirstOrDefault()
                           ?? _balance.Items.Values.Where(i => i.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                               .OrderByDescending(i => carried.Contains(i.Id)).ThenBy(i => i.Name.Length).ThenBy(i => i.Id).FirstOrDefault();
                }
                if (info is null || nameArgs.Length == 0)
                {
                    Tell(s, "Usage: .give <item id or name> [qty]. Try .finditem first.");
                    break;
                }
                // Stackable items join an existing stack; everything else is one entry per item.
                var stack = info.Stackable ? s.Sheet!.Backpack.FirstOrDefault(i => i.TemplateId == info.Id) : null;
                if (stack is not null) _store.SetItemQuantity(stack.Id, stack.Quantity + qty);
                else if (info.Stackable) _store.AddItem(s.Character!.Id, info.Id, qty);
                else for (var i = 0; i < Math.Min(qty, 20); i++) _store.AddItem(s.Character!.Id, info.Id, 1);
                var sheet = RefreshSheet(s);
                Send(s, Opcode.InventoryUpdate, sheet.InventoryWithMoney().Write);
                Tell(s, $"Added {qty} x {info.Name} ({info.Id}).");
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
                SendLiveStats(s);
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
