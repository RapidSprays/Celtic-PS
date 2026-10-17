using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's quest levels and item requirements (server/content/quest-requirements.json). The data's levels are
/// approximated; a quest listed here opens at its own <c>minLevel</c> (its <c>level</c>, for the reward XP, defaults to
/// that) and, with <c>requiresAnyItem</c>, only for a character who owns one of those items anywhere (backpack, equipped,
/// bank): the Warden armour needs a Warden weapon (or the Meteoric weapon it becomes).
/// </summary>
public sealed class QuestRequirements
{
    private readonly Dictionary<int, (int? MinLevel, int? Level)> _levels = new();
    private readonly Dictionary<int, HashSet<int>> _items = new();

    public static QuestRequirements None { get; } = new();

    public static QuestRequirements Load(string? path)
    {
        var r = new QuestRequirements();
        if (path is null || !File.Exists(path)) return r;
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
        }
        return r;
    }

    /// <summary>The quest with the owner's levels.</summary>
    public QuestInfo Apply(QuestInfo q) => _levels.TryGetValue(q.Id, out var l)
        ? q with { MinLevel = l.MinLevel ?? q.MinLevel, Level = l.Level ?? q.Level }
        : q;

    /// <summary>Whether a character owning <paramref name="owned"/> may take the quest.</summary>
    public bool ItemsMet(int questId, Func<ISet<int>> owned) => !_items.TryGetValue(questId, out var need) || need.Overlaps(owned());
}
