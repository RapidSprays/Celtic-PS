using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's damage-over-time variance and damage types (server/content/dot-damage.json): every tick of a player's DoT is
/// a random share of its normal amount, between minPercent and maxPercent (one default, or per DoT by status effect id),
/// then cut by the target's resist to its damage type. A DoT can be given a damage type the data lacks (Rupture, Stinging
/// Swarm), so resists apply to it.
/// </summary>
public sealed class DotDamage
{
    private static readonly string[] TypeNames = ["pierce", "slash", "crush", "heat", "cold", "magic", "poison", "divine", "chaos", "true"];

    private (double Min, double Max) _default = (100, 100);
    private readonly Dictionary<int, (double Min, double Max)> _own = new();
    private readonly Dictionary<int, int> _types = new();

    public static DotDamage None { get; } = new();

    public static DotDamage Load(string? path)
    {
        var d = new DotDamage();
        if (path is null || !File.Exists(path)) return d;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.TryGetProperty("default", out var def)) d._default = Range(def, d._default);
        if (root.TryGetProperty("dots", out var dots))
            foreach (var e in dots.EnumerateArray())
            {
                if (!e.TryGetProperty("effect", out var id)) continue;
                if (e.TryGetProperty("minPercent", out _) || e.TryGetProperty("maxPercent", out _))
                {
                    var r = Range(e, (-1, -1));
                    if (r.Min >= 0) d._own[id.GetInt32()] = r;
                }
                if (e.TryGetProperty("damageType", out var t) && t.ValueKind == JsonValueKind.String && Array.IndexOf(TypeNames, t.GetString()!.ToLowerInvariant()) is >= 0 and var type)
                    d._types[id.GetInt32()] = type;
            }
        return d;
    }

    private static (double Min, double Max) Range(JsonElement e, (double Min, double Max) fallback)
    {
        double? Num(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Max(0, v.GetDouble()) : null;
        var min = Num("minPercent");
        var max = Num("maxPercent");
        if (min is null && max is null) return fallback;
        var lo = min ?? max!.Value;
        var hi = max ?? lo;
        return (Math.Min(lo, hi), Math.Max(lo, hi));
    }

    /// <summary>This tick's share of the DoT's normal amount (1 = all of it).</summary>
    public double Factor(int effectId, Random rng)
    {
        var (min, max) = _own.TryGetValue(effectId, out var own) ? own : _default;
        return (min + rng.NextDouble() * (max - min)) / 100.0;
    }

    /// <summary>Gives the listed DoTs their damage type (so resists apply). Call after loading the balance data.</summary>
    public int ApplyTypes(Balance balance)
    {
        var changed = 0;
        foreach (var (id, type) in _types)
            if (balance.StatusEffects.TryGetValue(id, out var info))
            {
                balance.ReplaceStatusEffect(info with { DamageType = type });
                changed++;
            }
        return changed;
    }
}
