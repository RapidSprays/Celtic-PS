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
    public const float LeashDistance = 45f;

    public required uint ServerId { get; init; }
    public required MobInfo Template { get; init; }
    public required int Level { get; init; }
    public required float HomeX { get; init; }
    public required float HomeY { get; init; }
    public required float HomeZ { get; init; }
    public float Direction { get; set; }
    public required int Opinion { get; init; }
    public int ConversationId { get; init; } = -1;
    public float AggroRange { get; init; }
    public int RespawnSeconds { get; init; } = 45;
    public float RoamRadius { get; init; }
    public long? SpawnRecordId { get; init; }

    public bool IsNpc => Opinion >= 50;
    public int MaxHealth => MobStats.MaxHealth(Level, Template.Power);
    public int Health { get; set; }

    public MobState State { get; set; } = MobState.Idle;
    public object? Target { get; set; }
    public bool AttackAnnounced { get; set; }
    public double NextAttackAt { get; set; }
    public double NextThinkAt { get; set; }
    public double RemoveAt { get; set; }
    public double RespawnAt { get; set; }
    public bool Removed { get; set; }
    public Dictionary<object, int> Damage { get; } = new();

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

    public void Reset(double now)
    {
        Health = MaxHealth;
        State = MobState.Idle;
        Target = null;
        AttackAnnounced = false;
        Removed = false;
        Damage.Clear();
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
public static class MobStats
{
    public static int MaxHealth(int level, int power) => (int)(20 + 15 * Math.Max(1, level) * Math.Pow(Math.Max(1, power), 1.3));

    public static int HitDamage(int level, int power) => (int)((3 + 2 * Math.Max(1, level)) * (1 + 0.25 * (Math.Max(1, power) - 1)));

    public static int Xp(int level, int power) => 12 * Math.Max(1, level) * Math.Max(1, power);

    public static int Coins(int level, int power) => Random.Shared.Next(Math.Max(1, level), Math.Max(1, level) * 3 + 1) * Math.Max(1, power);

    public static float AggroRange(int power) => power >= 2 ? 8f : 0f;

    public static double AttackInterval(MobInfo template) => (template.AttackSpeed > 0 ? template.AttackSpeed : 2500) / 1000.0;
}
