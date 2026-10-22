using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's quest item rewards (server/content/quest-rewards.json). The game data has no item rewards (they're
/// server-only), so quests otherwise get the approximated chance of a piece of gear; a quest listed here gives exactly its
/// items instead (Azhar the Fugitive's daily: a Contraption Piece, his weekly Occulus Rift: a Contraption Core; the class
/// armour quests: Onyx/Obsidian, Elara's Beastbone, Lazhar's Alchemical/Occult).
/// </summary>
public sealed class QuestRewards
{
    private readonly Dictionary<int, List<(int Item, int Quantity, CharacterClass? Class)>> _items = new();

    public static QuestRewards None { get; } = new();

    public static QuestRewards Load(string? path)
    {
        var r = new QuestRewards();
        if (path is null || !File.Exists(path)) return r;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var q in doc.RootElement.GetProperty("quests").EnumerateArray())
        {
            if (!q.TryGetProperty("id", out var id)) continue;
            var list = new List<(int, int, CharacterClass?)>();
            if (q.TryGetProperty("items", out var items))
                foreach (var i in items.EnumerateArray())
                    if (i.TryGetProperty("id", out var item))
                        list.Add((item.GetInt32(), i.TryGetProperty("quantity", out var n) && n.ValueKind == JsonValueKind.Number ? Math.Max(1, n.GetInt32()) : 1,
                            i.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String && Enum.TryParse<CharacterClass>(c.GetString(), true, out var cls) ? cls : null));
            r._items[id.GetInt32()] = list;
        }
        return r;
    }

    /// <summary>
    /// The owner's items for a quest, for this class (an item with a "class" goes only to that class: the armour quests
    /// give each class its own piece); null → not listed (the gear named after the quest, else the approximated chance).
    /// </summary>
    /// <summary>Every item some quest gives as its reward (Jaryn's and Torbin's Falling Stars, Severina's Focus…).</summary>
    public IReadOnlySet<int> RewardItems => _rewardItems ??= _items.Values.SelectMany(l => l.Select(i => i.Item)).ToHashSet();

    private HashSet<int>? _rewardItems;

    /// <summary>The quest's listed items with their class (null = every class); null → the quest isn't listed.</summary>
    public IReadOnlyList<(int Item, int Quantity, CharacterClass? Class)>? Listed(int questId) => _items.TryGetValue(questId, out var list) ? list : null;

    public IReadOnlyList<(int Item, int Quantity)>? For(int questId, CharacterClass characterClass) =>
        _items.TryGetValue(questId, out var list) ? list.Where(i => i.Class is null || i.Class == characterClass).Select(i => (i.Item, i.Quantity)).ToList() : null;
}
