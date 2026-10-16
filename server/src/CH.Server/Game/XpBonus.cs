using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// Owner's extra kill XP (server/content/xp-bonus.json): a multiplier for monsters above the player's level (beyond the
/// highest listed difference, the highest one) and one per star rating. Without the file every multiplier is 1.
/// </summary>
public sealed class XpBonus
{
    private readonly SortedDictionary<int, double> _levelsAbove;
    private readonly Dictionary<int, double> _stars;

    private XpBonus(SortedDictionary<int, double> levelsAbove, Dictionary<int, double> stars) => (_levelsAbove, _stars) = (levelsAbove, stars);

    public static XpBonus None { get; } = new([], []);

    /// <summary>Base XP × the bonus for a monster <paramref name="mobLevel"/> with <paramref name="stars"/> stars.</summary>
    public double Multiplier(int playerLevel, int mobLevel, int stars)
    {
        var above = mobLevel - playerLevel;
        var level = above <= 0 || _levelsAbove.Count == 0 ? 1
            : _levelsAbove.TryGetValue(above, out var exact) ? exact
            : _levelsAbove.Where(l => l.Key <= above).Select(l => l.Value).DefaultIfEmpty(1).Last();
        var star = _stars.TryGetValue(Math.Max(1, stars), out var s) ? s : 1;
        return level * star;
    }

    public static XpBonus Load(string? path)
    {
        if (path is null || !File.Exists(path)) return None;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        static Dictionary<int, double> Table(JsonElement root, string name) => root.TryGetProperty(name, out var t)
            ? t.EnumerateObject().Where(e => int.TryParse(e.Name, out _)).ToDictionary(e => int.Parse(e.Name), e => e.Value.GetDouble())
            : [];
        return new XpBonus(new SortedDictionary<int, double>(Table(doc.RootElement, "levelsAbove")), Table(doc.RootElement, "stars"));
    }
}
