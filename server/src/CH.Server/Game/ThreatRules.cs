using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's threat (aggro) numbers (server/content/threat.json): threat per point of damage, a multiplier per class,
/// how far ahead someone must get to pull a monster off its target, and per-skill flat threat (the Grimoire's skill-aggro
/// table otherwise) or a multiplier on it.
/// </summary>
public sealed class ThreatRules
{
    public double DamageMultiplier { get; private set; } = 1.5;
    public double SwitchMargin { get; private set; } = 1.1;
    private readonly Dictionary<int, double> _class = new();
    private readonly Dictionary<int, double> _flat = new();
    private readonly Dictionary<int, double> _skillMultiplier = new();

    public static ThreatRules Defaults { get; } = new();

    public static ThreatRules Load(string? path)
    {
        var t = new ThreatRules();
        if (path is null || !File.Exists(path)) return t;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.TryGetProperty("damageMultiplier", out var d) && d.ValueKind == JsonValueKind.Number) t.DamageMultiplier = Math.Max(0, d.GetDouble());
        if (root.TryGetProperty("switchMargin", out var m) && m.ValueKind == JsonValueKind.Number) t.SwitchMargin = Math.Max(1, m.GetDouble());
        if (root.TryGetProperty("classMultipliers", out var classes))
            foreach (var c in classes.EnumerateObject())
                if (Enum.TryParse<CharacterClass>(c.Name, true, out var cls) && c.Value.ValueKind == JsonValueKind.Number) t._class[(int)cls] = Math.Max(0, c.Value.GetDouble());
        if (root.TryGetProperty("skills", out var skills))
            foreach (var s in skills.EnumerateArray())
            {
                if (!s.TryGetProperty("id", out var id)) continue;
                if (s.TryGetProperty("flatThreat", out var f) && f.ValueKind == JsonValueKind.Number) t._flat[id.GetInt32()] = Math.Max(0, f.GetDouble());
                if (s.TryGetProperty("multiplier", out var x) && x.ValueKind == JsonValueKind.Number) t._skillMultiplier[id.GetInt32()] = Math.Max(0, x.GetDouble());
            }
        return t;
    }

    /// <summary>The class's threat multiplier (1 when not listed).</summary>
    public double ClassMultiplier(int characterClass) => _class.GetValueOrDefault(characterClass, 1);

    /// <summary>A skill's flat threat per cast: the owner's number, else the data's, times the owner's multiplier.</summary>
    public double SkillFlat(int skillId, double dataThreat) =>
        (_flat.TryGetValue(skillId, out var own) ? own : dataThreat) * _skillMultiplier.GetValueOrDefault(skillId, 1);

    /// <summary>The multiplier on threat from a skill's damage (and Taunt/Warcry's damage-as-threat).</summary>
    public double SkillDamageMultiplier(int skillId) => _skillMultiplier.GetValueOrDefault(skillId, 1);
}
