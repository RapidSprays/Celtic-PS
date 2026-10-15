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
        Assert.Equal(extended, SkillLevels.Extend(levels)); // deterministic: the client mod must produce the same table
    }

    [Fact]
    public void Player_skills_train_to_fifty_ungated_with_room_for_gear_ranks_and_mob_skills_are_untouched()
    {
        if (!File.Exists(BalancePath)) return;
        var pummel = Data.Value.Skills[1];
        Assert.Equal(SkillLevels.Ranks, pummel.Levels.Count); // gear ranks go past 50
        Assert.Equal(SkillLevels.TrainableRanks - 1, pummel.MaxRank(1)); // training: every rank to 50 open at level 1
        Assert.Equal(6, Data.Value.Skills[2080].Levels.Count); // Cleave, a mob skill (no ability)
    }

    [Fact]
    public void Skill_overrides_scale_set_ranks_and_skip_disabled_entries()
    {
        if (!File.Exists(BalancePath)) return;
        var balance = Balance.Load(BalancePath);
        var before = balance.Skills[13].Levels; // Sharp Shot
        var path = Path.Combine(Path.GetTempPath(), $"skill-overrides-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "13": { "damageMultiplier": 2, "ranks": { "1": { "base": 99 } }, "cooldown": 4 }, "_52": { "damageMultiplier": 9 },
              "214": { "dotMultiplier": 2, "dotRanks": { "50": { "base": 30 } }, "dotTick": 4 } }
            """);
        try
        {
            Assert.Equal(2, SkillOverrides.Apply(balance, path)); // Sharp Shot and Rupture; "_52" is off
            var after = balance.Skills[13].Levels;
            Assert.Equal((before[0].InitialDamage * 2, 99f, 4f), (after[0].InitialDamage, after[0].BaseDamage, after[0].RechargeTime));
            Assert.Equal(before[10].BaseDamage * 2, after[10].BaseDamage);
            Assert.Equal(Data.Value.Skills[52].Levels[0].BaseDamage, balance.Skills[52].Levels[0].BaseDamage); // "_" entries are off
            var bleed = balance.StatusEffects[balance.Skills[214].StatusEffectId]; // Rupture's damage over time
            var original = Data.Value.StatusEffects[bleed.Id];
            Assert.Equal(original.Levels[0].Amount * 2, bleed.Levels[0].Amount);
            Assert.Equal((30f, 4f), (bleed.Levels[49].BaseAmount, bleed.TickSeconds));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Cooldown_items_are_read_from_their_descriptions()
    {
        if (!File.Exists(BalancePath)) return;
        var b = Data.Value;
        (int, int) Cut(string item) => b.RecastReductions(b.Items.Values.First(i => i.Name == item)).Single();
        int Skill(string name) => b.Skills.Values.Where(s => s.Name == name).Min(s => s.Id);
        Assert.Equal((Skill("Longshot"), 3), Cut("Minor Emberdrake Charm of Long Shot")); // "Long Shot" is the skill Longshot
        Assert.Equal((Skill("Rapid Shot"), 20), Cut("Sprout Ring of Hasted Might"));
        Assert.Equal((Skill("Fire Bolt"), 20), Cut("Dark Skull of Fire Bolt"));
        Assert.Equal((Skill("Double Attack"), 15), Cut("Mighty Bloodleaf Ring of Double Attack"));
        Assert.Equal((Skill("Rupture"), 20), Cut("Royal Necral Band of Rupture")); // wrist slot
        Assert.Equal(2, b.RecastReductions(b.Items.Values.First(i => i.Name == "Darksworn Wand of Earth")).Count); // Energy Shield and Energy Boost
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
