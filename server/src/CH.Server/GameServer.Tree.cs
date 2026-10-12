using System.Globalization;
using CH.Persistence;
using CH.Protocol.Messages;
using CH.Server.Game;

namespace CH.Server;

/// <summary>
/// The passive tree (docs/passive-tree.md): <c>.tree</c> chat commands, and the layout and state messages the client
/// mod draws its panel from. Every player may use them.
/// </summary>
public sealed partial class GameServer
{
    private const string TreeHelp = ".tree - open the passive tree | .tree alloc <id> | .tree refund <id> | .tree reset | .tree list";

    /// <summary>Passives that count: taken, still in the tree and connected to the class start.</summary>
    private HashSet<int> TreeNodes(CharacterRecord c) => _tree.Connected(_store.ListTreeNodes(c.Id), c.Class);

    /// <summary>Gives back passives a re-tuned tree no longer has, or that no longer connect to the start.</summary>
    private void PruneTree(CharacterRecord c)
    {
        if (!_tree.IsLoaded) return;
        var stored = _store.ListTreeNodes(c.Id);
        var kept = _tree.Connected(stored, c.Class);
        foreach (var id in stored.Where(id => !kept.Contains(id))) _store.RemoveTreeNode(c.Id, id);
    }

    private void TreeCommand(Session s, string[] args)
    {
        if (!_tree.IsLoaded)
        {
            Tell(s, "The passive tree isn't available on this server.");
            return;
        }
        var c = s.Character!;
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        int NodeArg() => args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : -1;
        var owned = TreeNodes(c);
        switch (verb)
        {
            case "":
            case "open": // "open" comes from the mod's menu button: no chat line
                SendTree(s, open: true);
                if (verb == "")
                    Tell(s, $"Passives: {PointsFree(s, owned)} of {_tree.PointsFor(s.Sheet!.Level)} points free. " + TreeHelp);
                return;

            case "alloc":
            {
                var id = NodeArg();
                if (_tree.CannotAllocate(owned, c.Class, s.Sheet!.Level, id) is { } why)
                {
                    Tell(s, why);
                    SendTree(s, open: false);
                    return;
                }
                _store.AddTreeNode(c.Id, id);
                break;
            }

            case "refund":
            {
                var id = NodeArg();
                if (_tree.CannotRefund(owned, c.Class, id) is { } why)
                {
                    Tell(s, why);
                    SendTree(s, open: false);
                    return;
                }
                _store.RemoveTreeNode(c.Id, id);
                break;
            }

            case "reset":
                _store.ResetTree(c.Id);
                Tell(s, $"All {owned.Count} passives refunded.");
                break;

            case "list":
            {
                var lines = new List<string> { $"Passives: {PointsFree(s, owned)} of {_tree.PointsFor(s.Sheet!.Level)} points free." };
                lines.AddRange(owned.Order().Select(id => $"  {id} {_tree.Nodes[id].Name}"));
                var start = _tree.StartFor(c.Class);
                var open = _tree.Nodes.Values.Where(n => n.Kind != "start" && !owned.Contains(n.Id) && n.Links.Any(l => l == start || owned.Contains(l)))
                    .OrderBy(n => n.Id).Select(n => $"{n.Id} {n.Name} ({string.Join(", ", _tree.Describe(n))})");
                lines.Add("Can take next: " + string.Join("; ", open));
                Tell(s, string.Join("\n", lines));
                return;
            }

            default:
                Tell(s, TreeHelp);
                return;
        }

        RefreshSheet(s);
        SendProgress(s);
        SendLiveStats(s);
        SendTree(s, open: false);
    }

    private int PointsFree(Session s, IReadOnlySet<int> owned) => Math.Max(0, _tree.PointsFor(s.Sheet!.Level) - owned.Count);

    /// <summary>Layout (once per session per tree version), then the character's state.</summary>
    private void SendTree(Session s, bool open)
    {
        if (s.TreeLayoutVersion != _tree.Version)
        {
            var layout = new TreeLayout(_tree.Version, _tree.Nodes.Values.OrderBy(n => n.Id).Select(n => new TreeLayoutNode(
                n.Id, n.Kind switch { "start" => TreeNodeKind.Start, "notable" => TreeNodeKind.Notable, "keystone" => TreeNodeKind.Keystone, _ => TreeNodeKind.Small },
                n.Name, n.X, n.Y, n.Class, n.Links, _tree.Describe(n))).ToList());
            Send(s, PrivateOpcode.TreeLayout, layout.Write);
            s.TreeLayoutVersion = _tree.Version;
        }
        var c = s.Character!;
        var owned = TreeNodes(c);
        Send(s, PrivateOpcode.TreeState, new TreeState(open, _tree.Version, _tree.StartFor(c.Class) ?? 0, PointsFree(s, owned),
            _tree.PointsFor(s.Sheet!.Level), owned.Order().ToList(), TreeSummary(owned)).Write);
    }

    /// <summary>Everything the character's passives add up to, one line per stat, skill group and keystone.</summary>
    private List<string> TreeSummary(IEnumerable<int> owned)
    {
        var totals = _tree.Totals(owned);
        var lines = totals.Stats.Where(kv => kv.Value != 0).OrderBy(kv => PassiveTree.StatText.Keys.ToList().IndexOf(kv.Key))
            .Select(kv => string.Format(CultureInfo.InvariantCulture, PassiveTree.StatText[kv.Key], Math.Round(kv.Value, 1))).ToList();
        foreach (var group in totals.SkillMods.GroupBy(m => m.Tag))
        {
            var merged = new SkillMod(group.Key, group.Sum(m => m.DamagePct), group.Sum(m => m.CooldownPct), group.Sum(m => m.CostPct), group.Sum(m => m.Ranks));
            lines.AddRange(_tree.Describe(new TreeNode(0, "small", "", 0, 0, 0, [], [merged], null, [])));
        }
        foreach (var k in totals.Keystones) lines.Add(_tree.KeystoneText.GetValueOrDefault(k, k));
        return lines;
    }
}
