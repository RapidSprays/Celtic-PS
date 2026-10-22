using System.Text.Json;
using System.Text.RegularExpressions;

namespace CH.Server.Game;

/// <summary>
/// The owner's colours for combat chat lines (server/content/chat-colours.json): critical hits ("lands a critical hit"),
/// critical skills and damage-over-time ticks, critical heals, and "Divine Strike Succeeded". The client's chat is
/// TextMeshPro, so a line is wrapped in &lt;color=#RRGGBB&gt;; an empty colour leaves the line as it is.
/// </summary>
public sealed class ChatColours
{
    public string CritStrike { get; private init; } = "#FF5050";
    public string CritSkill { get; private init; } = "#FFB020";
    public string CritHeal { get; private init; } = "#50FF80";
    public string DivineStrike { get; private init; } = "#FFF080";
    public string DivineGlow { get; private init; } = "#FFF080";

    public static ChatColours Defaults { get; } = new();

    private static readonly Regex Hex = new("^#?([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$");

    public static ChatColours Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Defaults;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var r = doc.RootElement;
        string Get(string name, string fallback) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : fallback;
        return new ChatColours
        {
            CritStrike = Get("critStrike", Defaults.CritStrike), CritSkill = Get("critSkill", Defaults.CritSkill),
            CritHeal = Get("critHeal", Defaults.CritHeal), DivineStrike = Get("divineStrike", Defaults.DivineStrike),
            DivineGlow = Get("divineGlow", Defaults.DivineGlow),
        };
    }

    /// <summary>The line in <paramref name="colour"/> (#RRGGBB); unchanged when the colour is empty or not a colour.</summary>
    public static string Paint(string text, string colour) =>
        Hex.Match(colour.Trim()) is { Success: true } m ? $"<color=#{m.Groups[1].Value}>{text}</color>" : text;
}
