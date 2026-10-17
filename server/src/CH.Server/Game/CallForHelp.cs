using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's call-for-help ranges (server/content/call-for-help.json): how far a monster's "call enemies within m"
/// script reaches. Every script's own range is multiplied by <c>rangeMultiplier</c>, capped at <c>maxRange</c>; a monster
/// listed with its own <c>range</c> uses that instead (0 = it never calls for help).
/// </summary>
public sealed class CallForHelpRanges
{
    private readonly Dictionary<int, float> _own = new();
    private float _multiplier = 1f;
    private float? _max;

    public static CallForHelpRanges None { get; } = new();

    public static CallForHelpRanges Load(string? path)
    {
        var c = new CallForHelpRanges();
        if (path is null || !File.Exists(path)) return c;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.TryGetProperty("rangeMultiplier", out var m) && m.ValueKind == JsonValueKind.Number) c._multiplier = Math.Max(0, m.GetSingle());
        if (root.TryGetProperty("maxRange", out var max) && max.ValueKind == JsonValueKind.Number) c._max = Math.Max(0, max.GetSingle());
        if (root.TryGetProperty("monsters", out var monsters))
            foreach (var e in monsters.EnumerateArray())
                if (e.TryGetProperty("id", out var id) && e.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Number)
                    c._own[id.GetInt32()] = Math.Max(0, r.GetSingle());
        return c;
    }

    /// <summary>The range a monster's call for help reaches, from its script's own range.</summary>
    public float Range(int templateId, float scriptRange)
    {
        if (_own.TryGetValue(templateId, out var own)) return own;
        var range = scriptRange * _multiplier;
        return _max is { } max ? Math.Min(range, max) : range;
    }
}
