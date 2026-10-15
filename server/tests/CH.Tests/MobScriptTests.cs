using CH.Server.Game;

namespace CH.Tests;

/// <summary>Monster scripts (Master's Grimoire's scripts.txt), as GameServer.Scripts runs them.</summary>
public sealed class MobScriptTests
{
    private static MobScript Script(int id, double[][] when, double[][]? unless = null, int priority = 1) =>
        new(id, when, unless ?? [], [[102, 2134]], "", priority);

    private static ScriptView View(MobScriptState state, double now, double health = 1, Func<int, bool>? status = null, int aggroed = 1) =>
        new(now, health, state, status ?? (_ => false), _ => true, _ => aggroed, (_, _) => 0);

    [Fact]
    public void Stonecrusher_body_slams_at_once_and_then_every_five_seconds()
    {
        var bodySlam = Script(1057, [[3, 5]]); // "5 seconds since last activation"
        var state = new MobScriptState();
        state.Start(100);
        Assert.True(MobScriptRules.ShouldRun(bodySlam, View(state, 100))); // never run yet
        state.LastRunAt[1057] = 100;
        Assert.False(MobScriptRules.ShouldRun(bodySlam, View(state, 104)));
        Assert.True(MobScriptRules.ShouldRun(bodySlam, View(state, 105)));
    }

    [Fact]
    public void Every_trigger_must_hold_and_any_failure_condition_stops_it()
    {
        // Pyrus enrages once, 900 seconds into the fight: "4^900" and failure "201^0" (used more than 0 times).
        var enrage = Script(99, [[4, 900]], [[201, 0]]);
        var state = new MobScriptState();
        state.Start(0);
        Assert.False(MobScriptRules.ShouldRun(enrage, View(state, 899)));
        Assert.True(MobScriptRules.ShouldRun(enrage, View(state, 900)));
        state.Runs[99] = 1;
        Assert.False(MobScriptRules.ShouldRun(enrage, View(state, 1200)));

        // Under half health and 600 seconds since last activation.
        var heatUp = Script(1014, [[1, 0.5], [3, 600]]);
        Assert.False(MobScriptRules.ShouldRun(heatUp, View(state, 10, health: 0.6)));
        Assert.True(MobScriptRules.ShouldRun(heatUp, View(state, 10, health: 0.4)));
    }

    [Fact]
    public void Triggers_follow_other_scripts_statuses_hits_and_ai_changes()
    {
        var state = new MobScriptState();
        state.Start(0);
        // "6^1624^0.5": half a second since script 1624 last ran (never ran: no).
        var follow = Script(1625, [[5, 1624, 0], [6, 1624, 0.5]]);
        Assert.False(MobScriptRules.ShouldRun(follow, View(state, 10)));
        state.Runs[1624] = 1;
        state.LastRunAt[1624] = 9;
        Assert.True(MobScriptRules.ShouldRun(follow, View(state, 10)));

        // A status on the monster ("12^id") and its absence ("13^id").
        Assert.True(MobScriptRules.Trigger(follow, [12, 20003], View(state, 10, status: id => id == 20003)));
        Assert.False(MobScriptRules.Trigger(follow, [13, 20003], View(state, 10, status: id => id == 20003)));

        // "9^1^11^-1": any hit from Fire Bolt (skill 11) since the last check.
        Assert.False(MobScriptRules.Trigger(follow, [9, 1, 11, -1], View(state, 10)));
        state.HitsTaken.Add((11, 300));
        Assert.True(MobScriptRules.Trigger(follow, [9, 1, 11, -1], View(state, 10)));
        Assert.False(MobScriptRules.Trigger(follow, [9, 1, 11, 500], View(state, 10)));

        // "11^30": 30 seconds since the AI changed (or the fight started, before any change).
        Assert.True(MobScriptRules.Trigger(follow, [11, 30], View(state, 30)));
        state.AiChangedAt = 20;
        Assert.False(MobScriptRules.Trigger(follow, [11, 30], View(state, 30)));
    }

    [Fact]
    public void Scripts_load_by_monster_highest_priority_first()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mob-scripts-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"scripts": {"1055": {"when": [[3, 300]], "unless": [], "do": [[106, 2137, 2, 20]], "say": "", "priority": 1},
                         "1057": {"when": [[3, 5]], "unless": [], "do": [[102, 2134]], "say": "<MN> slams!", "priority": 3}},
             "mobs": {"71874": [[-1, 1055], [-1, 1057]]}}
            """);
        try
        {
            var book = MobScriptBook.Load(path);
            var scripts = book.For(71874);
            Assert.Equal([1057, 1055], scripts.Select(s => s.Script.Id));
            Assert.Equal([102.0, 2134.0], scripts[0].Script.Do[0]);
            Assert.Equal("<MN> slams!", scripts[0].Script.Say);
            Assert.Empty(book.For(1));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
