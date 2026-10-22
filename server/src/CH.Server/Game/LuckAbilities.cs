using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// Treasure Hunter's double-gold and Scholar's double-XP chances (server/content/luck-abilities.json): the ability's
/// level × percentPerPoint, capped at maxPercent. The live rates are server-only; these are the owner's numbers.
/// </summary>
public sealed class LuckAbilities
{
    public const int TreasureHunter = 31, Scholar = 32;

    private readonly Dictionary<int, (double PerPoint, double Max)> _rates = new()
    {
        [TreasureHunter] = (0.01, 100),
        [Scholar] = (0.01, 100),
    };

    /// <summary>Chance per kill that each of the two goes up by one (owner's rule, 2026-10-17: 5%).</summary>
    public double KillGainChance { get; private set; } = 0.05;

    public static LuckAbilities Load(string? path)
    {
        var l = new LuckAbilities();
        if (path is null || !File.Exists(path)) return l;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.TryGetProperty("killGainChance", out var g) && g.ValueKind == JsonValueKind.Number) l.KillGainChance = Math.Clamp(g.GetDouble(), 0, 1);
        foreach (var (key, id) in new[] { ("treasureHunter", TreasureHunter), ("scholar", Scholar) })
            if (doc.RootElement.TryGetProperty(key, out var e))
            {
                var (per, max) = l._rates[id];
                if (e.TryGetProperty("percentPerPoint", out var p) && p.ValueKind == JsonValueKind.Number) per = Math.Max(0, p.GetDouble());
                if (e.TryGetProperty("maxPercent", out var m) && m.ValueKind == JsonValueKind.Number) max = Math.Clamp(m.GetDouble(), 0, 100);
                l._rates[id] = (per, max);
            }
        return l;
    }

    /// <summary>The chance in percent at this ability level.</summary>
    public double Percent(int abilityId, int level) =>
        _rates.TryGetValue(abilityId, out var r) ? Math.Min(r.Max, Math.Max(0, level) * r.PerPoint) : 0;

    /// <summary>Rolls whether this kill's reward doubles.</summary>
    public bool Doubles(int abilityId, int level, Random rng) => rng.NextDouble() * 100 < Percent(abilityId, level);
}
