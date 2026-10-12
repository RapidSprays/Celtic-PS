using CH.Server;
using CH.Server.Game;

namespace CH.Tests;

/// <summary>Owner's rules: no rank caps on player skills and no ceilings on attack or run speed.</summary>
public sealed class LimitTests
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis/extracted/24928510/balance-client.json");

    private static readonly Lazy<Balance> Data = new(() => Balance.Load(BalancePath));

    [Fact]
    public void Extended_skill_tables_continue_the_last_step_and_drop_level_gates()
    {
        var levels = new List<SkillLevelInfo> { new(10, 3, 0, 10, 3, 0, 0), new(12, 6, 0, 10, 4, 1, 15), new(15, 10, 1, 8, 5, 2, 30) };
        var extended = SkillLevels.Extend(levels);
        Assert.Equal(SkillLevels.Ranks, extended.Count);
        Assert.All(extended, l => Assert.Equal(0, l.MinLevel));
        Assert.Equal(Enumerable.Range(0, SkillLevels.Ranks), extended.Select(l => l.TemplateLevel));
        Assert.Equal((18f, 14f), (extended[3].InitialDamage, extended[3].BaseDamage));
        Assert.Equal((15f + 3 * 257, 10f + 4 * 257), (extended[^1].InitialDamage, extended[^1].BaseDamage));
        Assert.Equal((5, 8f, 1f), (extended[^1].EnergyCost, extended[^1].RechargeTime, extended[^1].CastingTime));
        Assert.Equal(extended, SkillLevels.Extend(levels)); // deterministic: the client mod must produce the same table
    }

    [Fact]
    public void Player_skills_have_long_ungated_tables_and_mob_skills_are_untouched()
    {
        if (!File.Exists(BalancePath)) return;
        var pummel = Data.Value.Skills[1];
        Assert.Equal(SkillLevels.Ranks, pummel.Levels.Count);
        Assert.Equal(SkillLevels.Ranks - 1, pummel.MaxRank(1));
        Assert.True(pummel.Levels[200].InitialDamage > pummel.Levels[49].InitialDamage);
        Assert.Equal(6, Data.Value.Skills[2080].Levels.Count); // Cleave, a mob skill (no ability)
    }

    [Fact]
    public void Attack_and_run_speed_have_no_ceiling_but_slows_keep_their_floor()
    {
        Assert.Equal(100, GameServer.AttackSpeedWith(1000, 900));
        Assert.Equal(1, GameServer.AttackSpeedWith(300, 1_000_000));
        Assert.Equal(2000, GameServer.AttackSpeedWith(1000, -90));
        Assert.Equal(50f, GameServer.RunSpeedWith(5f, 900));
        Assert.Equal(1f, GameServer.RunSpeedWith(5f, -95));
    }
}
