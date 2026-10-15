using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// A monster script from Master's Grimoire (scripts.txt): when every trigger in <see cref="When"/> holds and no failure
/// condition in <see cref="Unless"/> does, the actions in <see cref="Do"/> run in order and <see cref="Say"/> is announced
/// (<c>&lt;MN&gt;</c> is the monster's name). Each entry is a code followed by its arguments, as in the data.
/// </summary>
public sealed record MobScript(int Id, IReadOnlyList<double[]> When, IReadOnlyList<double[]> Unless, IReadOnlyList<double[]> Do,
    string Say, int Priority);

/// <summary>
/// Which scripts each monster runs (mobscripts.txt), and while fighting with which AI (-1: any). Written by
/// scripts/import-grimoire.py as mob-scripts.json beside the balance JSON; GameServer.Scripts runs them.
/// </summary>
public sealed class MobScriptBook
{
    private readonly Dictionary<int, IReadOnlyList<(int AiType, MobScript Script)>> _mobs;

    private MobScriptBook(Dictionary<int, IReadOnlyList<(int, MobScript)>> mobs) => _mobs = mobs;

    public static MobScriptBook Empty { get; } = new([]);

    public int Count => _mobs.Count;

    public IReadOnlyList<(int AiType, MobScript Script)> For(int mobTemplate) => _mobs.GetValueOrDefault(mobTemplate) ?? [];

    public static MobScriptBook Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Empty;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        static List<double[]> Steps(JsonElement e, string name) => e.TryGetProperty(name, out var list)
            ? list.EnumerateArray().Select(x => x.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0).ToArray()).ToList()
            : [];
        var scripts = doc.RootElement.GetProperty("scripts").EnumerateObject().ToDictionary(s => int.Parse(s.Name), s => new MobScript(
            int.Parse(s.Name), Steps(s.Value, "when"), Steps(s.Value, "unless"), Steps(s.Value, "do"),
            s.Value.TryGetProperty("say", out var say) ? say.GetString() ?? "" : "",
            s.Value.TryGetProperty("priority", out var p) ? p.GetInt32() : 1));
        var mobs = new Dictionary<int, IReadOnlyList<(int, MobScript)>>();
        foreach (var m in doc.RootElement.GetProperty("mobs").EnumerateObject())
            mobs[int.Parse(m.Name)] = m.Value.EnumerateArray()
                .Where(l => scripts.ContainsKey(l[1].GetInt32()))
                .Select(l => (l[0].GetInt32(), scripts[l[1].GetInt32()]))
                .OrderByDescending(l => l.Item2.Priority).ToList();
        return new MobScriptBook(mobs);
    }
}

/// <summary>A monster's script bookkeeping for the current fight: cleared when it resets or gives up.</summary>
public sealed class MobScriptState
{
    public double BattleStartAt { get; set; }
    public double? AiChangedAt { get; set; }
    public double NextCheckAt { get; set; }
    /// <summary>The AI a script switched it to; null for its own.</summary>
    public MobAi? Ai { get; set; }
    public Dictionary<int, int> Runs { get; } = new();
    public Dictionary<int, double> LastRunAt { get; } = new();
    /// <summary>Hits it took since the scripts last checked: skill id (0 for auto-attacks) and damage.</summary>
    public List<(int SkillId, int Damage)> HitsTaken { get; } = new();

    public void Start(double now)
    {
        Clear();
        BattleStartAt = now;
        NextCheckAt = now;
    }

    public void Clear()
    {
        AiChangedAt = null;
        Ai = null;
        Runs.Clear();
        LastRunAt.Clear();
        HitsTaken.Clear();
    }
}

/// <summary>What a script's triggers can ask about a monster (GameServer.Scripts fills it in).</summary>
public sealed record ScriptView(
    double Now,
    double HealthFraction,
    MobScriptState State,
    Func<int, bool> HasStatus,
    Func<int, bool> SkillReady,
    Func<float, int> AggroedPlayersWithin,
    Func<int, float, int> MobsWithin);

/// <summary>
/// Master's Grimoire's script triggers and failure conditions (its "Trigger:" and "Failure Condition:" texts), checked
/// once a second while the monster fights.
/// </summary>
public static class MobScriptRules
{
    public static bool ShouldRun(MobScript script, ScriptView v) =>
        script.When.All(t => Trigger(script, t, v)) && !script.Unless.Any(f => Fails(script, f, v));

    private static double Arg(double[] step, int i) => i < step.Length ? step[i] : 0;

    private static int Runs(ScriptView v, double id) => v.State.Runs.GetValueOrDefault((int)id);

    private static double? SinceRun(ScriptView v, double id) =>
        v.State.LastRunAt.TryGetValue((int)id, out var at) ? v.Now - at : null;

    public static bool Trigger(MobScript script, double[] t, ScriptView v) => (int)Arg(t, 0) switch
    {
        1 => v.HealthFraction < Arg(t, 1),                                   // enemy health is under x
        2 => v.HealthFraction > Arg(t, 1),                                   // enemy health is above x
        3 => SinceRun(v, script.Id) is not { } since || since >= Arg(t, 1),  // x seconds since last activation (or never)
        4 => v.Now - v.State.BattleStartAt >= Arg(t, 1),                     // x seconds since battle start
        5 => Runs(v, Arg(t, 1)) > Arg(t, 2),                                 // script #id used more than n times
        6 => SinceRun(v, Arg(t, 1)) is { } since6 && since6 >= Arg(t, 2),    // x seconds since script #id last activated
        7 => Runs(v, Arg(t, 1)) < Arg(t, 2),                                 // script #id used less than n times
        8 => SinceRun(v, Arg(t, 1)) is { } since8 && since8 < Arg(t, 2),     // less than x since script #id last activated
        9 => v.State.HitsTaken.Any(h => (Arg(t, 2) < 0 || h.SkillId == (int)Arg(t, 2)) && h.Damage > Arg(t, 3)), // takes more than n from skill
        10 => v.State.HitsTaken.Any(h => (Arg(t, 2) < 0 || h.SkillId == (int)Arg(t, 2)) && h.Damage < Arg(t, 3)),
        11 => v.Now - (v.State.AiChangedAt ?? v.State.BattleStartAt) >= Arg(t, 1), // x seconds since AI type changed
        12 => v.HasStatus((int)Arg(t, 1)),
        13 => !v.HasStatus((int)Arg(t, 1)),
        14 => v.SkillReady((int)Arg(t, 1)),
        15 => v.AggroedPlayersWithin(float.MaxValue) > Arg(t, 1),           // aggro'd players more than n
        16 => v.AggroedPlayersWithin((float)Arg(t, 3)) > Arg(t, 1),         // aggro'd players within m more than n
        117 => v.MobsWithin((int)Arg(t, 1), (float)Arg(t, 3)) >= Arg(t, 2), // n of a monster within m
        _ => true, // codes the Grimoire doesn't describe (201 as a trigger) don't hold a script back
    };

    public static bool Fails(MobScript script, double[] f, ScriptView v) => (int)Arg(f, 0) switch
    {
        201 => Runs(v, script.Id) > Arg(f, 1),           // this script used more than n times
        5 => Runs(v, Arg(f, 1)) > Arg(f, 2),             // script #id used more than n times
        12 => v.HasStatus((int)Arg(f, 1)),               // a status effect is active
        _ => false,
    };
}
