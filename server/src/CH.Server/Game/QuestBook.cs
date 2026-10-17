using System.Text.Json;

namespace CH.Server.Game;

/// <summary>Quest stage completion types (text.db quest_stage_templates.completion_type).</summary>
public enum StageType
{
    MobKill = 0,          // "mobTemplate,count"
    LootItem = 1,         // "itemTemplate,count"
    Position = 2,         // "zone,x,y,z,radius"
    Conversation = 3,     // completed by the client (STAGE_COMPLETE)
    CompletedQuest = 5,   // "questId"
    HandInItem = 6,       // "itemTemplate,count" (client STAGE_COMPLETE; the server takes the items)
    HandInItemList = 7,   // "item,count;item,count;..."
    CastSkill = 8,        // "skillId,count"
    CastSkillOn = 10,     // "skillId,count;mobTemplate"
    MobSetKill = 12,      // "mobSet,count"
}

public sealed record QuestStage(StageType Type, string Details, int Next, int Total)
{
    /// <summary>LootItem stages: monster templates named in the stage text, which drop the quest item.</summary>
    public IReadOnlyList<int> Sources { get; init; } = [];

    /// <summary>The leading "id,count" pair most stage types use.</summary>
    public (int Id, int Count) Target
    {
        get
        {
            var parts = Details.Split(';')[0].Split(',');
            return (parts.Length > 0 && int.TryParse(parts[0], out var id) ? id : -1,
                    parts.Length > 1 && int.TryParse(parts[1], out var n) ? Math.Max(1, n) : 1);
        }
    }

    /// <summary>HandInItemList entries.</summary>
    public IEnumerable<(int Item, int Count)> Items => Details.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split(',')).Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
        .Select(p => (int.Parse(p[0]), int.Parse(p[1])));
}

/// <summary>A quest from scripts/gen-world.py. Levels, prerequisites and rewards are <b>approximated</b>.</summary>
public sealed record QuestInfo(int Id, string Name, int? Zone, int Level, int MinLevel, IReadOnlyList<int> Prerequisites,
    bool Repeatable, bool Seasonal, int? Giver, IReadOnlyList<QuestStage> Stages)
{
    /// <summary>Stages no other stage leads to: active when the quest starts.</summary>
    public IEnumerable<int> InitialStages => Enumerable.Range(0, Stages.Count).Where(i => Stages.All(s => s.Next != i));

    /// <summary>Stages that lead to <paramref name="stage"/> (it activates when all of them are done).</summary>
    public IEnumerable<int> Predecessors(int stage) => Enumerable.Range(0, Stages.Count).Where(i => Stages[i].Next == stage);

    /// <summary>Reward XP: a share of the level's XP need. <b>Approximated.</b></summary>
    public int RewardXp => (int)(Progression.XpToNextLevel(Math.Max(1, Level)) * 0.35);

    public int RewardCoins => 10 + Level * 8;

    /// <summary>RepeatArchetype: "BountyBoard" and "AncientBountyBoard" quests are offered only by the bounty boards.</summary>
    public string Archetype { get; init; } = "NoRepeat";

    public bool IsBounty => Archetype is "BountyBoard" or "AncientBountyBoard";
}

/// <summary>An NPC from scripts/gen-world.py: the official spawn id (for the client's icons), template and conversation.</summary>
public sealed record NpcPlacement(int SpawnId, int Template, int Conversation, int Zone, float X, float Y, float Z, float Direction);

/// <summary>Quests and NPCs generated from the client data (world.json beside the balance JSON).</summary>
public sealed class QuestBook
{
    public IReadOnlyDictionary<int, QuestInfo> Quests { get; }
    public IReadOnlyList<NpcPlacement> Npcs { get; }

    private QuestBook(IReadOnlyDictionary<int, QuestInfo> quests, IReadOnlyList<NpcPlacement> npcs)
    {
        Quests = quests;
        Npcs = npcs;
    }

    public static QuestBook Empty { get; } = new(new Dictionary<int, QuestInfo>(), []);

    /// <summary>
    /// This book with the owner's extra NPCs added and placed NPCs adjusted (server/content/npc-extras.json: "npcs" and
    /// "adjust", the latter by mob template: direction, x, y, z).
    /// </summary>
    /// <summary>The quests with the owner's levels (quest-requirements.json).</summary>
    public QuestBook WithRequirements(QuestRequirements requirements) =>
        new(Quests.ToDictionary(q => q.Key, q => requirements.Apply(q.Value)), Npcs);

    public QuestBook WithExtraNpcs(string? path)
    {
        if (path is null || !File.Exists(path)) return this;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var extras = doc.RootElement.GetProperty("npcs").EnumerateArray().Select((n, i) => new NpcPlacement(
            ExtraSpawnIds + i, n.GetProperty("template").GetInt32(), n.GetProperty("conversation").GetInt32(), n.GetProperty("zone").GetInt32(),
            n.GetProperty("x").GetSingle(), n.GetProperty("y").GetSingle(), n.GetProperty("z").GetSingle(), n.GetProperty("direction").GetSingle()));
        var adjust = doc.RootElement.TryGetProperty("adjust", out var a)
            ? a.EnumerateArray().ToDictionary(x => x.GetProperty("template").GetInt32(), x => x.Clone())
            : new Dictionary<int, JsonElement>();
        float Or(JsonElement e, string name, float value) => e.TryGetProperty(name, out var v) ? v.GetSingle() : value;
        var moved = Npcs.Select(n => adjust.TryGetValue(n.Template, out var e)
            ? n with { X = Or(e, "x", n.X), Y = Or(e, "y", n.Y), Z = Or(e, "z", n.Z), Direction = Or(e, "direction", n.Direction) }
            : n);
        return new QuestBook(Quests, moved.Concat(extras).ToList());
    }

    /// <summary>Spawn ids for extra NPCs: well clear of the official ones.</summary>
    private const int ExtraSpawnIds = 9_000_000;

    public static QuestBook Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Empty;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var quests = new Dictionary<int, QuestInfo>();
        foreach (var q in doc.RootElement.GetProperty("quests").EnumerateObject())
        {
            var e = q.Value;
            int? Opt(string name) => e.GetProperty(name).ValueKind == JsonValueKind.Number ? e.GetProperty(name).GetInt32() : null;
            quests[int.Parse(q.Name)] = new QuestInfo(int.Parse(q.Name), e.GetProperty("name").GetString() ?? "", Opt("zone"),
                e.GetProperty("level").GetInt32(), e.GetProperty("minLevel").GetInt32(),
                e.GetProperty("prereqs").EnumerateArray().Select(p => p.GetInt32()).ToList(),
                e.GetProperty("repeatable").GetBoolean(), e.GetProperty("seasonal").GetBoolean(), Opt("giver"),
                e.GetProperty("stages").EnumerateArray().Select(s => new QuestStage((StageType)s.GetProperty("type").GetInt32(),
                    s.GetProperty("details").GetString() ?? "", s.GetProperty("next").GetInt32(), s.GetProperty("total").GetInt32())
                {
                    Sources = s.TryGetProperty("sources", out var src) ? src.EnumerateArray().Select(x => x.GetInt32()).ToList() : [],
                }).ToList())
            {
                Archetype = e.TryGetProperty("archetype", out var arch) ? arch.GetString() ?? "NoRepeat" : "NoRepeat",
            };
        }
        var npcs = doc.RootElement.GetProperty("npcs").EnumerateArray().Select(n => new NpcPlacement(
            n.GetProperty("spawnId").GetInt32(), n.GetProperty("template").GetInt32(), n.GetProperty("conversation").GetInt32(),
            n.GetProperty("zone").GetInt32(), n.GetProperty("x").GetSingle(), n.GetProperty("y").GetSingle(), n.GetProperty("z").GetSingle(),
            n.GetProperty("direction").GetSingle())).ToList();
        return new QuestBook(quests, npcs);
    }
}
