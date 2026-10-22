using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's boss abilities (custom-monsters.json "abilities"), run by GameServer.Bosses. Each has a name the client
/// shows (the client mod copies its <see cref="Look"/> skill and that skill's status effect under
/// <see cref="SkillId"/>/<see cref="EffectId"/>), and a kind:
/// <list type="bullet">
/// <item>weaponBuff: adds <see cref="Damage"/> of <see cref="DamageType"/> to every auto-attack for <see cref="Duration"/> s,
/// recast <see cref="RecastAfter"/> s after it ends (Denya's Venom Coat).</item>
/// <item>selfHot: heals <see cref="Heal"/> every <see cref="Every"/> s while it lasts, recast when it ends (Morrigan's Embrace).</item>
/// <item>selfHeal: a <see cref="CastTime"/> s cast healing <see cref="Heal"/>, once as health falls below each of
/// <see cref="AtHealth"/> (percent); a hit from one of <see cref="InterruptedBy"/> stops it (Dark Touch, Shield Bash).</item>
/// <item>aoeStun: every <see cref="Cooldown"/> s, everyone within <see cref="Radius"/> m takes <see cref="Damage"/> and a
/// <see cref="StunSeconds"/> s stun, unless they evade it with <see cref="EvadedBy"/> (Priestess's Mezmorize).</item>
/// </list>
/// </summary>
public sealed record BossAbility(string Kind, string Name, int SkillId, int Look, int EffectId)
{
    public int DamageType { get; init; } = 5;
    public int Damage { get; init; }
    public double Duration { get; init; } = 60;
    public double RecastAfter { get; init; }
    public int Heal { get; init; }
    public double Every { get; init; } = 5;
    public double CastTime { get; init; } = 2;
    public IReadOnlyList<double> AtHealth { get; init; } = [];
    public IReadOnlySet<int> InterruptedBy { get; init; } = new HashSet<int>();
    public double Cooldown { get; init; } = 45;
    public float Radius { get; init; } = 8;
    public double StunSeconds { get; init; } = 4;
    public int EvadedBy { get; init; } = -1;

    private static readonly Dictionary<string, int> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pierce"] = 0, ["slash"] = 1, ["crush"] = 2, ["heat"] = 3, ["fire"] = 3, ["cold"] = 4, ["ice"] = 4, ["magic"] = 5,
        ["poison"] = 6, ["divine"] = 7, ["chaos"] = 8, ["true"] = 9,
    };

    private static readonly Dictionary<string, int> Avoidances = new(StringComparer.OrdinalIgnoreCase)
    {
        ["physical"] = 0, ["spell"] = 1, ["movement"] = 2, ["wounding"] = 3, ["weakening"] = 4, ["mental"] = 5,
    };

    public static IReadOnlyList<BossAbility> Parse(JsonElement monster, Func<string, int?> skillByName)
    {
        if (!monster.TryGetProperty("abilities", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        double D(JsonElement e, string n, double d) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        int I(JsonElement e, string n, int d) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return list.EnumerateArray().Select(e => new BossAbility(S(e, "kind"), S(e, "name"), I(e, "skillId", 0), I(e, "look", 0), I(e, "effectId", 0))
        {
            DamageType = Types.GetValueOrDefault(S(e, "damageType"), 5),
            Damage = I(e, "damage", 0),
            Duration = D(e, "duration", 60),
            RecastAfter = D(e, "recastAfter", 0),
            Heal = I(e, "heal", 0),
            Every = Math.Max(0.5, D(e, "every", 5)),
            CastTime = Math.Max(0, D(e, "castTime", 2)),
            AtHealth = e.TryGetProperty("atHealth", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetDouble()).OrderByDescending(x => x).ToList() : [],
            InterruptedBy = e.TryGetProperty("interruptedBy", out var ib) && ib.ValueKind == JsonValueKind.Array
                ? ib.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : skillByName(x.GetString() ?? "")).OfType<int>().ToHashSet()
                : new HashSet<int>(),
            Cooldown = Math.Max(1, D(e, "cooldown", 45)),
            Radius = (float)D(e, "radius", 8),
            StunSeconds = Math.Max(0, D(e, "stunSeconds", 4)),
            EvadedBy = Avoidances.GetValueOrDefault(S(e, "evadedBy"), -1),
        }).Where(x => x.Kind.Length > 0).ToList();
    }
}

/// <summary>A boss's live ability state: buff and heal timers, cooldowns, which health thresholds it has healed at, a cast.</summary>
public sealed class BossState(IReadOnlyList<BossAbility> abilities)
{
    public IReadOnlyList<BossAbility> Abilities { get; } = abilities;
    public Dictionary<BossAbility, double> ActiveUntil { get; } = new();
    public Dictionary<BossAbility, double> ReadyAt { get; } = new();
    public Dictionary<BossAbility, double> NextTickAt { get; } = new();
    public HashSet<(BossAbility, double)> HealedAt { get; } = new();
    public (BossAbility Ability, double LandsAt)? Casting { get; set; }

    /// <summary>Bonus auto-attack damage from an active weaponBuff (Venom Coat).</summary>
    public IEnumerable<(int Type, int Amount)> HitBonuses(double now) =>
        Abilities.Where(a => a.Kind == "weaponBuff" && ActiveUntil.GetValueOrDefault(a) > now).Select(a => (a.DamageType, a.Damage));

    /// <summary>Back home: everything starts over.</summary>
    public void Reset()
    {
        ActiveUntil.Clear();
        ReadyAt.Clear();
        NextTickAt.Clear();
        HealedAt.Clear();
        Casting = null;
    }
}
