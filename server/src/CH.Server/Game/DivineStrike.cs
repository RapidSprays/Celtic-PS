using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's Divine Strike damage (server/content/divine-strike.json): a random amount between min and max, for the set
/// weapons' proc (skill 2200, by its rank: Warden 0, Meteoric 1, Frozen 2, Dragonlord 3, Exalted Dragonlord 4) and for the
/// armour set auras' strike (by aura name). Ranks or auras not listed keep the game data's or set-bonuses.json's amount.
/// </summary>
public sealed class DivineStrikeDamage
{
    private readonly Dictionary<int, (int Min, int Max)> _weapon = new();
    private readonly Dictionary<string, (int Min, int Max)> _aura = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (int Min, int Max)> _offhand = new();
    private readonly Dictionary<int, int> _buffs = new();

    /// <summary>
    /// The Dragonlord and Exalted Dragonlord offhands' skills (Fury, Spite, Vengeance, Wrath, Boon) and their effects: "only
    /// affects targets vulnerable to Divine attacks", so they only work on monsters spawn-tuning.json marks divine.
    /// </summary>
    public static readonly HashSet<int> DivineOnlySkills = [2210, 2211, 2212, 2213, 2214, 7335, 7336, 7337, 7338, 7339];
    public static readonly HashSet<int> DivineOnlyEffects = [2142, 2144, 2146, 5319, 5321, 5323];

    public static DivineStrikeDamage None { get; } = new();

    public static DivineStrikeDamage Load(string? path)
    {
        var d = new DivineStrikeDamage();
        if (path is null || !File.Exists(path)) return d;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.TryGetProperty("weaponProc", out var weapons))
            foreach (var w in weapons.EnumerateArray())
                if (w.TryGetProperty("rank", out var rank)) d._weapon[rank.GetInt32()] = Range(w);
        if (doc.RootElement.TryGetProperty("offhandSkills", out var offhands))
            foreach (var o in offhands.EnumerateArray())
            {
                var range = Range(o);
                if (o.TryGetProperty("skill", out var skill) && skill.ValueKind == JsonValueKind.Number) d._offhand[skill.GetInt32()] = range;
                if (o.TryGetProperty("effect", out var effect) && effect.ValueKind == JsonValueKind.Number) d._offhand[effect.GetInt32()] = range;
            }
        if (doc.RootElement.TryGetProperty("offhandBuffs", out var buffs))
            foreach (var b in buffs.EnumerateArray())
                if (b.TryGetProperty("effect", out var effect) && b.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number)
                    d._buffs[effect.GetInt32()] = Math.Max(0, amount.GetInt32());
        if (doc.RootElement.TryGetProperty("setAura", out var auras))
            foreach (var a in auras.EnumerateArray())
                if (a.TryGetProperty("aura", out var name) && name.GetString() is { } n) d._aura[n] = Range(a);
        return d;
    }

    private static (int Min, int Max) Range(JsonElement e)
    {
        var min = e.TryGetProperty("min", out var lo) ? Math.Max(0, lo.GetInt32()) : 0;
        var max = e.TryGetProperty("max", out var hi) ? Math.Max(0, hi.GetInt32()) : min;
        return (Math.Min(min, max), Math.Max(min, max));
    }

    /// <summary>
    /// Sets the offhand buffs' amounts (Wrath's damage, Boon's chaos resist) on every rank of their status effects, so the
    /// buff applies exactly that much. Call after loading the balance data. Returns how many effects changed.
    /// </summary>
    public int ApplyBuffs(Balance balance)
    {
        var changed = 0;
        foreach (var (id, amount) in _buffs)
        {
            if (!balance.StatusEffects.TryGetValue(id, out var info)) continue;
            balance.ReplaceStatusEffect(info with { Levels = info.Levels.Select(l => l with { Amount = 0, BaseAmount = amount }).ToList() });
            changed++;
        }
        return changed;
    }

    /// <summary>An offhand skill's damage (Fury's hit, Spite's tick, Vengeance's strike back) by skill or effect id; null → the data's.</summary>
    public int? Offhand(int skillOrEffectId, Random rng) => _offhand.TryGetValue(skillOrEffectId, out var r) ? rng.Next(r.Min, r.Max + 1) : null;

    /// <summary>The weapon proc's damage at a rank; null → the game data's.</summary>
    public int? Weapon(int rank, Random rng) => _weapon.TryGetValue(rank, out var r) ? rng.Next(r.Min, r.Max + 1) : null;

    /// <summary>The aura strike's damage for the worn auras (the first one listed); null → set-bonuses.json's divine.</summary>
    public int? Aura(IEnumerable<string> auraNames, Random rng)
    {
        foreach (var name in auraNames)
            if (_aura.TryGetValue(name.Trim(), out var r)) return rng.Next(r.Min, r.Max + 1);
        return null;
    }
}
