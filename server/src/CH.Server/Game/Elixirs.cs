using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// What elixirs really give (server/content/elixirs.json, from the live game, 2026-10-15). The client data only holds
/// each elixir's flat part (Super Elixir of Attack: 100) and, for the combination elixirs, just a tier; live adds a
/// percentage of the stat on top and multiplies XP and ability gains. Each elixir status effect lists what it gives and
/// which tier each of its levels is (normal, super, heroic). Elixirs don't stack with each other: for every stat the
/// strongest active elixir counts (a Super Combination and a Super Elixir of Attack give the attack once).
/// </summary>
public sealed class ElixirRules
{
    /// <summary>A flat bonus plus a percentage of the character's own stat (regen: of maximum health or energy).</summary>
    public sealed record Boost(double Flat, double Percent);

    public sealed record Grant(IReadOnlyList<string> Gives, IReadOnlyList<string> Tiers);

    private readonly Dictionary<string, Dictionary<string, JsonElement>> _tiers;
    private readonly Dictionary<int, Grant> _effects;

    private ElixirRules(Dictionary<string, Dictionary<string, JsonElement>> tiers, Dictionary<int, Grant> effects, int minAttackDelay) =>
        (_tiers, _effects, MinAttackDelay) = (tiers, effects, minAttackDelay);

    public static ElixirRules None { get; } = new([], [], 0);

    /// <summary>How long every elixir lasts (owner: 30 minutes); 0 keeps the game data's.</summary>
    public double DurationSeconds { get; private init; }

    /// <summary>Whether elixirs stay on through death (owner: yes).</summary>
    public bool KeepOnDeath { get; private init; }

    public IEnumerable<int> EffectIds => _effects.Keys;

    public bool IsElixir(int effectId) => _effects.ContainsKey(effectId);

    /// <summary>The rules in use (loaded when the server starts).</summary>
    public static ElixirRules Current { get; set; } = None;

    /// <summary>An elixir's haste never takes the weapon faster than this many milliseconds a swing.</summary>
    public int MinAttackDelay { get; }

    /// <summary>The tier and stats an active elixir effect gives, or false when it isn't one of the elixirs.</summary>
    public bool TryGet(int effectId, int levelIndex, out string tier, out IReadOnlyList<string> gives)
    {
        (tier, gives) = ("", []);
        if (!_effects.TryGetValue(effectId, out var grant) || grant.Tiers.Count == 0) return false;
        tier = grant.Tiers[Math.Clamp(levelIndex, 0, grant.Tiers.Count - 1)];
        gives = grant.Gives;
        return _tiers.ContainsKey(tier);
    }

    /// <summary>A tier's boost for attack, defence, armour, maxHealth, maxEnergy, healthRegen or energyRegen.</summary>
    public Boost BoostOf(string tier, string stat) =>
        _tiers.TryGetValue(tier, out var t) && t.TryGetValue(stat, out var v) && v.ValueKind == JsonValueKind.Object
            ? new Boost(v.TryGetProperty("flat", out var f) ? f.GetDouble() : 0, v.TryGetProperty("percent", out var p) ? p.GetDouble() : 0)
            : new Boost(0, 0);

    /// <summary>A tier's single number for haste and runSpeed (percent) or xp and abilityGain (multiplier).</summary>
    public double ValueOf(string tier, string stat, double none) =>
        _tiers.TryGetValue(tier, out var t) && t.TryGetValue(stat, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : none;

    public static ElixirRules Load(string? path)
    {
        if (path is null || !File.Exists(path)) return None;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var tiers = root.GetProperty("tiers").EnumerateObject()
            .ToDictionary(t => t.Name, t => t.Value.EnumerateObject().ToDictionary(s => s.Name, s => s.Value.Clone()));
        var effects = root.GetProperty("elixirs").EnumerateObject().Where(e => int.TryParse(e.Name, out _)).ToDictionary(
            e => int.Parse(e.Name),
            e => new Grant(e.Value.GetProperty("gives").EnumerateArray().Select(g => g.GetString() ?? "").ToList(),
                e.Value.GetProperty("tiers").EnumerateArray().Select(t => t.GetString() ?? "").ToList()));
        return new ElixirRules(tiers, effects, root.TryGetProperty("minAttackDelay", out var d) ? d.GetInt32() : 0)
        {
            DurationSeconds = root.TryGetProperty("durationSeconds", out var s) ? s.GetDouble() : 0,
            KeepOnDeath = root.TryGetProperty("keepOnDeath", out var k) && k.GetBoolean(),
        };
    }
}
