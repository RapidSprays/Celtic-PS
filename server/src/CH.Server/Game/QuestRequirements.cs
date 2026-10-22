using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's quest levels and item requirements (server/content/quest-requirements.json). The data's levels are
/// approximated; a quest listed here opens at its own <c>minLevel</c> (its <c>level</c>, for the reward XP, defaults to
/// that) and, with <c>requiresAnyItem</c>, only for a character who owns one of those items anywhere (backpack, equipped,
/// bank): the Warden armour needs a Warden weapon (or the Meteoric weapon it becomes). With <c>notWhileActive</c> it isn't
/// offered while one of those quests is in progress.
/// </summary>
public sealed class QuestRequirements
{
    private readonly Dictionary<int, (int? MinLevel, int? Level)> _levels = new();
    private readonly Dictionary<int, HashSet<int>> _items = new();
    private readonly Dictionary<int, HashSet<int>> _notWhileActive = new();
    private readonly Dictionary<int, int> _givers = new();
    private readonly Dictionary<int, double> _xpPercent = new();
    private readonly Dictionary<int, int> _xp = new();
    private readonly HashSet<int> _alwaysComplete = new();

    public static QuestRequirements None { get; } = new();

    /// <summary>Loads the owner's file and the generated conversation-order file (quest-exclusions.json); both are optional.</summary>
    public static QuestRequirements Load(params string?[] paths)
    {
        var r = new QuestRequirements();
        foreach (var path in paths) r.Read(path);
        return r;
    }

    private void Read(string? path)
    {
        var r = this;
        if (path is null || !File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var q in doc.RootElement.GetProperty("quests").EnumerateArray())
        {
            if (!q.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetInt32();
            int? Num(string name) => q.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Max(1, v.GetInt32()) : null;
            var min = Num("minLevel");
            var level = Num("level") ?? min;
            if (min is not null || level is not null) r._levels[id] = (min, level);
            if (q.TryGetProperty("requiresAnyItem", out var items) && items.ValueKind == JsonValueKind.Array)
                r._items[id] = items.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) ? i : e)
                    .Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToHashSet();
            if (q.TryGetProperty("alwaysComplete", out var ac) && ac.ValueKind == JsonValueKind.True) r._alwaysComplete.Add(id);
            if (q.TryGetProperty("xp", out var flat) && flat.ValueKind == JsonValueKind.Number) r._xp[id] = Math.Max(0, flat.GetInt32());
            if (q.TryGetProperty("xpPercentOfLevel", out var xp) && xp.ValueKind == JsonValueKind.Number) r._xpPercent[id] = Math.Max(0, xp.GetDouble());
            if (q.TryGetProperty("giver", out var g) && g.ValueKind == JsonValueKind.Number) r._givers[id] = g.GetInt32();
            if (q.TryGetProperty("notWhileActive", out var nw) && nw.ValueKind == JsonValueKind.Array)
                (r._notWhileActive.TryGetValue(id, out var set) ? set : r._notWhileActive[id] = [])
                    .UnionWith(nw.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Array ? e[0] : e).Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()));
        }
    }

    /// <summary>
    /// Quests that must not be in progress while this one is offered or held: the NPC's conversation checks "this quest
    /// in progress" before their turn-ins, so holding both hides the other's hand-in (quest-exclusions.json).
    /// </summary>
    public IReadOnlySet<int> NotWhileActive(int questId) => _notWhileActive.TryGetValue(questId, out var set) ? set : new HashSet<int>();

    /// <summary>The quest with the owner's levels.</summary>
    public QuestInfo Apply(QuestInfo q)
    {
        if (_levels.TryGetValue(q.Id, out var l)) q = q with { MinLevel = l.MinLevel ?? q.MinLevel, Level = l.Level ?? q.Level };
        if (q.Giver is null && _givers.TryGetValue(q.Id, out var giver)) q = q with { Giver = giver };
        return q;
    }

    /// <summary>"xpPercentOfLevel": the quest's XP as a percent of the player's current level (null = the quest's own XP).</summary>
    /// <summary>
    /// "alwaysComplete": every character counts as having done the quest (it's never offered), so whatever it unlocks is
    /// open from the start: Vaultkeeper Renauld's legacy lux shop without his "shady" quest.
    /// </summary>
    public IReadOnlySet<int> AlwaysComplete => _alwaysComplete;

    /// <summary>"xp": the quest's XP, the same at every level (null = not set).</summary>
    public int? Xp(int questId) => _xp.TryGetValue(questId, out var x) ? x : null;

    public double? XpPercent(int questId) => _xpPercent.TryGetValue(questId, out var p) ? p : null;

    /// <summary>Whether a character owning <paramref name="owned"/> may take the quest.</summary>
    public bool ItemsMet(int questId, Func<ISet<int>> owned) => !_items.TryGetValue(questId, out var need) || need.Overlaps(owned());
}
