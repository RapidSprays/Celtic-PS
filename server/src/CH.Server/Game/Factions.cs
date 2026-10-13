using System.Text.Json;

namespace CH.Server.Game;

public sealed record FactionLevel(int Value, int Points, string Title);

/// <summary>A FactionTemplate from the client's balance data.</summary>
public sealed record FactionInfo(int Id, string Name, IReadOnlyList<FactionLevel> Levels, int Min, int Max)
{
    /// <summary>The client's FactionTemplate.GetLevel, exactly: conversations test this level (FACTION_GREATER_THAN).</summary>
    public FactionLevel? LevelAt(int points)
    {
        if (points < 0)
            foreach (var l in Levels)
                if (l.Points <= 0 && points <= l.Points) return l;
        if (points >= 0)
            for (var i = Levels.Count - 1; i > 0; i--)
                if (Levels[i].Points >= 0 && points >= Levels[i].Points) return Levels[i];
        return null;
    }
}

/// <summary>
/// Where reputation comes from (server/content/factions.json). The official point sources are server-only, so these
/// are our own: the Liches' and Reavers' quests in zone 24 (each side's quests cost standing with the other), and the
/// Valley of the Ancients quests, Ancient bounties and kills for the Hiddenfolk.
/// </summary>
public sealed class FactionRules
{
    public sealed record Rule(int Faction, int Points, IReadOnlySet<int> Quests, string? Archetype, int? KillsInZone);

    public IReadOnlyList<int> Shown { get; }
    public IReadOnlyList<Rule> Rules { get; }

    private FactionRules(IReadOnlyList<int> shown, IReadOnlyList<Rule> rules)
    {
        Shown = shown;
        Rules = rules;
    }

    public static FactionRules Empty { get; } = new([], []);

    public static FactionRules Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Empty;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var rules = root.GetProperty("rules").EnumerateArray().Select(r => new Rule(r.GetProperty("faction").GetInt32(), r.GetProperty("points").GetInt32(),
            r.TryGetProperty("quests", out var q) ? q.EnumerateArray().Select(x => x.GetInt32()).ToHashSet() : new HashSet<int>(),
            r.TryGetProperty("archetype", out var a) ? a.GetString() : null,
            r.TryGetProperty("killsInZone", out var z) ? z.GetInt32() : null)).ToList();
        return new FactionRules(root.GetProperty("shown").EnumerateArray().Select(x => x.GetInt32()).ToList(), rules);
    }

    public IEnumerable<(int Faction, int Points)> ForQuest(QuestInfo quest) =>
        Rules.Where(r => r.Quests.Contains(quest.Id) || (r.Archetype is not null && r.Archetype == quest.Archetype)).Select(r => (r.Faction, r.Points));

    public IEnumerable<(int Faction, int Points)> ForKill(int zoneId) =>
        Rules.Where(r => r.KillsInZone == zoneId).Select(r => (r.Faction, r.Points));
}
