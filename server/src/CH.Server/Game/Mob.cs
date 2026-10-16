namespace CH.Server.Game;

public enum MobState
{
    Idle,
    Chasing,
    Fighting,
    Returning,
    Dead,
}

/// <summary>
/// A live mob or NPC in a zone. Position is tracked as the current movement leg (from → to over [start, eta]) in the
/// server's NetTime clock, matching what mobPatrolUpdate tells the client.
/// </summary>
public sealed class Mob
{
    public const float RoamSpeed = 1.5f;
    public const float ChaseSpeed = 4.5f;
    public const float LeashDistance = 22.5f; // owner's rule (2026-10-15): half the old 45 m

    /// <summary>The id the client knows the mob by. A spawn point that rerolls its monster gets a new one (GameServer.World).</summary>
    public required uint ServerId { get; set; }
    public required MobInfo Template { get; set; }
    public required int Level { get; set; }
    public required float HomeX { get; init; }
    public required float HomeY { get; init; }
    public required float HomeZ { get; init; }
    public float Direction { get; set; }
    public required int Opinion { get; init; }
    public int ConversationId { get; init; } = -1;
    public float AggroRange { get; set; }
    public int RespawnSeconds { get; init; } = 45;
    public float RoamRadius { get; init; }
    public long? SpawnRecordId { get; init; }

    public bool IsNpc => Opinion >= 50;

    /// <summary>Fishing spots are gathering mobs (MobTemplate Type 1, opinion −15): fished with a rod, never killed in combat.</summary>
    public bool IsFishingSpot => Template.Type == 1;
    public bool RareSpot { get; init; }
    public int? FixedMaxHealth { get; set; }

    /// <summary>When each of its skills can be cast again (skill id → server time).</summary>
    public Dictionary<int, double> SkillReadyAt { get; } = new();

    /// <summary>Its scripts' bookkeeping for the current fight (GameServer.Scripts).</summary>
    public MobScriptState Scripts { get; } = new();

    /// <summary>Official attack, defence and damage (null → the MobStats formulas).</summary>
    public MobCombat? Combat { get; set; }

    /// <summary>The client's "Chaos Aura" status (2136): "Its attacks deal additional Chaos Damage, but it is vulnerable to Divine Damage".</summary>
    public const int ChaosAuraEffect = 2136;

    /// <summary>
    /// Monsters of The Morrigan carry Chaos Aura. Nothing in the data applies it, so it goes by its description: every
    /// monster whose official attacks deal Chaos damage (Goretusk, Bladewing, Redbane, Aggragoth, Mordris…).
    /// Only these take Divine Strike.
    /// </summary>
    public bool HasChaosAura => Combat is { } c && c.Damage.Any(d => d.Type == 8);
    public int MaxHealth => FixedMaxHealth ?? MobStats.MaxHealth(Level, Template.Power);
    public int Health { get; set; }

    public MobState State { get; set; } = MobState.Idle;
    public object? Target { get; set; }
    public bool AttackAnnounced { get; set; }
    public double NextAttackAt { get; set; }

    /// <summary>When an elite or boss may next use its special attack (GameServer.Combat).</summary>
    public double NextThinkAt { get; set; }
    public double RemoveAt { get; set; }
    public double RespawnAt { get; set; }
    public bool Removed { get; set; }
    public Dictionary<object, int> Damage { get; } = new();

    /// <summary>
    /// Threat per player (Master's Grimoire's aggro): damage × 1.5 × level factor, plus each skill's own aggro value,
    /// heals and buffs on someone it's fighting included. It fights whoever is highest (GameServer.World).
    /// </summary>
    public Dictionary<object, double> Threat { get; } = new();

    /// <summary>Who tagged the mob (first to hit it): they and their party get the kill (GameServer.Combat locks).</summary>
    public object? Owner { get; set; }
    public List<ActiveEffect> Effects { get; } = new();

    public float FromX { get; private set; }
    public float FromZ { get; private set; }
    public float ToX { get; private set; }
    public float ToZ { get; private set; }
    public double LegStart { get; private set; }
    public double LegEta { get; private set; }

    public bool IsMoving(double now) => now < LegEta && (FromX != ToX || FromZ != ToZ);

    public (float X, float Z) PositionAt(double now)
    {
        if (now >= LegEta || LegEta <= LegStart) return (ToX, ToZ);
        var t = (float)((now - LegStart) / (LegEta - LegStart));
        return (FromX + (ToX - FromX) * t, FromZ + (ToZ - FromZ) * t);
    }

    /// <summary>Starts a new leg from the current position to (x, z) at the given speed. Returns the ETA.</summary>
    public double MoveTo(double now, float x, float z, float speed)
    {
        var (cx, cz) = PositionAt(now);
        FromX = cx;
        FromZ = cz;
        ToX = x;
        ToZ = z;
        LegStart = now;
        var distance = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
        LegEta = now + Math.Max(0.05, distance / speed);
        if (distance > 0.01f) Direction = MathF.Atan2(x - cx, z - cz) * 180f / MathF.PI;
        return LegEta;
    }

    public void StopAt(double now)
    {
        var (cx, cz) = PositionAt(now);
        FromX = ToX = cx;
        FromZ = ToZ = cz;
        LegStart = LegEta = now;
    }

    /// <summary>
    /// What this spawn point can produce, with weights (an official spawn point's template mix). Each respawn picks one
    /// afresh (<see cref="Reroll"/>), so a wolf's spot may come back as a different wolf, or now and then a rare one.
    /// Empty → always the same monster.
    /// </summary>
    public IReadOnlyList<MobVariant> Variants { get; init; } = [];

    /// <summary>Picks this spawn's next monster from <see cref="Variants"/> by weight.</summary>
    public void Reroll(Random rng)
    {
        if (Variants.Count < 2) return;
        var roll = rng.NextDouble() * Variants.Sum(v => v.Weight);
        var pick = Variants[^1];
        foreach (var v in Variants)
        {
            roll -= v.Weight;
            if (roll < 0)
            {
                pick = v;
                break;
            }
        }
        Template = pick.Template;
        Level = pick.Level;
        FixedMaxHealth = pick.Health;
        Combat = pick.Combat;
        AggroRange = MobStats.AggroRange(pick.Template.Power);
    }

    public void Reset(double now)
    {
        Health = MaxHealth;
        State = MobState.Idle;
        Target = null;
        AttackAnnounced = false;
        Removed = false;
        Damage.Clear();
        Threat.Clear();
        Effects.Clear();
        Scripts.Clear();
        FromX = ToX = HomeX;
        FromZ = ToZ = HomeZ;
        LegStart = LegEta = now;
        NextThinkAt = now + Random.Shared.NextDouble() * 8;
    }

    public float DistanceFromHome(double now)
    {
        var (x, z) = PositionAt(now);
        return MathF.Sqrt((x - HomeX) * (x - HomeX) + (z - HomeZ) * (z - HomeZ));
    }
}

/// <summary>Mob numbers by level and star power (1-6). <b>Approximated</b>: the client ships no mob levels, HP or damage.</summary>
/// <summary>One monster a spawn point can produce, with its level, health (null → formula) and weight.</summary>
public sealed record MobVariant(MobInfo Template, int Level, int? Health, double Weight, MobCombat? Combat = null);

public static class MobStats
{
    public static int MaxHealth(int level, int power) => (int)(20 + 15 * Math.Max(1, level) * Math.Pow(Math.Max(1, power), 1.3));

    public static int HitDamage(int level, int power) => (int)((3 + 2 * Math.Max(1, level)) * (1 + 0.25 * (Math.Max(1, power) - 1)));

    public static int Xp(int level, int power) => 12 * Math.Max(1, level) * Math.Max(1, power);

    public static int Coins(int level, int power) => Random.Shared.Next(Math.Max(1, level), Math.Max(1, level) * 3 + 1) * Math.Max(1, power);

    public static float AggroRange(int power) => power >= 2 ? 8f : 0f;

    /// <summary>Attack for monsters without official numbers (approximated), weighed against the player's defence.</summary>
    public static int Attack(int level, int power) => (int)((10 + 5 * Math.Max(1, level)) * (1 + 0.25 * (Math.Max(1, power) - 1)));

    /// <summary>
    /// Share of a hit a resist lets through (Master's Grimoire's formula): 1 − R / (R + 6 + 3 × attacker level). High-level
    /// attackers punch through the same resist more easily.
    /// </summary>
    public static double ResistFactor(int resist, int attackerLevel) => resist <= 0 ? 1 : 1 - resist / (resist + 6.0 + 3 * Math.Max(1, attackerLevel));

    public static double AttackInterval(MobInfo template) => (template.AttackSpeed > 0 ? template.AttackSpeed : 2500) / 1000.0;
}

/// <summary>A monster's official combat numbers: hit chance is attack against the target's defence; damage is per type.</summary>
public sealed record MobCombat(int Level, int Health, int Attack, int Defence, IReadOnlyList<(int Type, int Amount)> Damage)
{
    /// <summary>Official XP for a kill (before the level difference) and the gold range it drops.</summary>
    public int Xp { get; init; }
    public (int Min, int Max) Gold { get; init; }

    /// <summary>
    /// XP a player of <paramref name="playerLevel"/> gets for the kill (Master's Grimoire): full XP from a monster at or
    /// above their level, × 0.75 for each level they're above it.
    /// </summary>
    public int XpFor(int playerLevel) => playerLevel > Level ? (int)Math.Round(Xp * Math.Pow(0.75, playerLevel - Level)) : Xp;

    /// <summary>Resist per damage type: a flat amount (through <see cref="MobStats.ResistFactor"/>) and the share let through (0 = immune).</summary>
    public IReadOnlyDictionary<int, (int Flat, double Share)> Resists { get; init; } = new Dictionary<int, (int, double)>();

    /// <summary>Evasion against players' skills by avoidance type (0 physical, 1 spell, 2 movement, 3 wounding, 4 weakening, 5 mental).</summary>
    public IReadOnlyDictionary<int, int> Evade { get; init; } = new Dictionary<int, int>();

    /// <summary>
    /// The part of <paramref name="amount"/> of a damage type that gets through, against an attacker of
    /// <paramref name="attackerLevel"/>. <paramref name="change"/> is what lures on the monster add to the flat resist
    /// (negative), never taking it below 0, as Master's Grimoire's solver does.
    /// </summary>
    public double AfterResist(int type, double amount, int attackerLevel, int change = 0) =>
        Resists.TryGetValue(type, out var r) ? amount * MobStats.ResistFactor(Math.Max(0, r.Flat + change), attackerLevel) * r.Share : amount;
}

/// <summary>
/// Each monster's official level, health, attack, defence and damage, from Master's Grimoire (scripts/import-grimoire.py
/// writes mob-stats.json beside the balance JSON). Monsters without an entry keep the <see cref="MobStats"/> formulas.
/// </summary>
public sealed class OfficialMobStats
{
    private readonly Dictionary<int, MobCombat> _mobs;

    private OfficialMobStats(Dictionary<int, MobCombat> mobs) => _mobs = mobs;

    public static OfficialMobStats Empty { get; } = new([]);

    public int Count => _mobs.Count;

    public static OfficialMobStats Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Empty;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        int Int(System.Text.Json.JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
        return new OfficialMobStats(doc.RootElement.GetProperty("mobs").EnumerateObject().ToDictionary(
            e => int.Parse(e.Name),
            e => new MobCombat(Int(e.Value, "level"), Int(e.Value, "health"), Int(e.Value, "attack"), Int(e.Value, "defence"),
                e.Value.TryGetProperty("damage", out var d) ? d.EnumerateArray().Select(x => (x[0].GetInt32(), x[1].GetInt32())).ToList() : [])
            {
                Xp = Int(e.Value, "xp"),
                Gold = e.Value.TryGetProperty("gold", out var g) && g.GetArrayLength() == 2 ? (g[0].GetInt32(), g[1].GetInt32()) : (0, 0),
                Resists = e.Value.TryGetProperty("resists", out var r)
                    ? r.EnumerateArray().ToDictionary(x => x[0].GetInt32(), x => (x[1].GetInt32(), x[2].GetDouble()))
                    : new Dictionary<int, (int, double)>(),
                Evade = e.Value.TryGetProperty("evade", out var ev)
                    ? ev.EnumerateObject().ToDictionary(x => int.Parse(x.Name), x => x.Value.GetInt32())
                    : new Dictionary<int, int>(),
            }));
    }

    public bool TryGet(int template, out MobCombat combat) => _mobs.TryGetValue(template, out combat!);
}
