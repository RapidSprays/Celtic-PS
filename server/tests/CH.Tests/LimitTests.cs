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
              "214": { "dotMultiplier": 2, "dotRanks": { "50": { "base": 30 } }, "dotTick": 4 },
              "223": { "effectRanks": { "50": { "amount": 150, "base": 400 } } } }
            """);
        try
        {
            Assert.Equal(3, SkillOverrides.Apply(balance, path)); // Sharp Shot, Rupture and Bless; "_52" is off
            var bless = balance.StatusEffects[balance.Skills[223].StatusEffectId]; // all skill evasions
            Assert.Equal((150f, 400f), (bless.Levels[49].Amount, bless.Levels[49].BaseAmount));
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

    [Fact]
    public void Super_elixirs_give_flat_plus_percent_and_do_not_stack()
    {
        if (!File.Exists(BalancePath)) return;
        ElixirRules.Current = ElixirRules.Load(Path.Combine(AppContext.BaseDirectory, "content", "elixirs.json"));
        var combination = Data.Value.StatusEffects[56]; // Combination Elixir; level index 1 = super
        var attackElixir = Data.Value.StatusEffects[44];
        ActiveEffect On(StatusEffectInfo info, int level) => new() { Info = info, LevelIndex = level, ExpiresAt = double.MaxValue };
        var m = EffectModifiers.From([On(combination, 1), On(attackElixir, 1)], 7825, 1139, 895, 2650, 2296); // max health, max energy, armour, defence, attack
        Assert.Equal((int)System.Math.Round(100 + 2296 * 0.2), m.Attack);  // once, not twice
        Assert.Equal((int)System.Math.Round(100 + 2650 * 0.2), m.Defence);
        Assert.Equal((int)System.Math.Round(200 + 7825 * 0.2), m.MaxHealth);
        Assert.Equal((int)System.Math.Round(10 + 7825 * 0.02), m.HealthRegen);
        Assert.Equal((50, 60, 4.0, 4.0), (m.ElixirHaste, m.RunSpeedPercent, m.XpMultiplier, m.AbilityGainMultiplier));
        Assert.Equal(2850 * 100 / 150, GameServer.AttackSpeedWith(2850, m.ElixirHaste)); // 50% haste on a 2.85 s halberd: 1.9 s
    }

    [Fact]
    public void Pet_skills_rank_up_with_the_pets_size()
    {
        if (!File.Exists(BalancePath)) return;
        // Hexbound Pierce: Tiny imp rank 1 (46 initial), Small (level 40) rank 2 (126), confirmed on live; Spirit III rank 9.
        Assert.Equal(0, Data.Value.EquipSkillRank(65960));
        Assert.Equal(1, Data.Value.EquipSkillRank(65961));
        Assert.Equal(8, Data.Value.EquipSkillRank(73044));
        Assert.Equal(126f, Data.Value.Skills[8470].Level(Data.Value.EquipSkillRank(65961)).InitialDamage);
        // Live max hits before resists (2026-10-15): Giant imp (rank 6) at 2,000 Imp Taming, Spirit III (rank 9) at 2,300.
        var pierce = Data.Value.Skills[8470];
        Assert.Equal(3852, SkillInfo.Amount(pierce.Level(Data.Value.EquipSkillRank(65965)), 2000, 0));
        Assert.Equal(7535, SkillInfo.Amount(pierce.Level(Data.Value.EquipSkillRank(73044)), 2300, 0));
        // 27-rank families: pets size by size, mounts colour by colour (live: a Ferocious Brown War Wolf's Howl is rank 5).
        Assert.Equal(4, Data.Value.EquipSkillRank(60150));  // Ferocious Brown War Wolf → Howl rank 5
        Assert.Equal(6, Data.Value.EquipSkillRank(60152));  // Tame Red War Wolf → rank 7 (Howl 44)
        Assert.Equal(26, Data.Value.EquipSkillRank(73011)); // Spirit War Wolf III → rank 27
        Assert.Equal(0, Data.Value.EquipSkillRank(58665));  // Tiny Brown Mastiff → Canine Bite rank 1
        Assert.Equal(4, Data.Value.EquipSkillRank(58666));  // Small Brown Mastiff → rank 5 (the TEST Eagles don't count)
        Assert.Equal(26, Data.Value.EquipSkillRank(73058)); // Spirit Mastiff III → rank 27
        // Natural Focus heals (live): Tiny Brown Hare (rank 1) at 10 Rabbit Taming 14, Giant Golden Hare (rank 24) at 2,000: 1,149.
        var focus = Data.Value.Skills[7879];
        Assert.Equal((0, 23), (Data.Value.EquipSkillRank(58689), Data.Value.EquipSkillRank(58712)));
        Assert.Equal(14, SkillInfo.Amount(focus.Level(Data.Value.EquipSkillRank(58689)), 10, 0));
        Assert.Equal(1149, SkillInfo.Amount(focus.Level(Data.Value.EquipSkillRank(58712)), 2000, 0));
    }

    [Fact]
    public void The_scavenger_sells_exactly_his_live_stock()
    {
        if (!File.Exists(BalancePath)) return;
        var extras = Path.Combine(AppContext.BaseDirectory, "content", "shop-extras.json");
        var shops = new ShopCatalog(Data.Value, new Dictionary<int, ShopInfo>(), ShopCatalog.LoadExtras(extras), ShopCatalog.LoadReplaced(extras));
        Assert.Equal(12, shops.Stock(7000).Count);  // Offhand Weapons: grimoires, blades, quivers
        Assert.Equal(24, shops.Stock(7310).Count);  // Shields and Armour: helms, crowns, shields
        Assert.Contains(20808, shops.Stock(7000));  // Master's Grimoire
        Assert.Contains(20835, shops.Stock(7310));  // Golden Helm of Prowess
        Assert.Empty(shops.Stock(7311));            // Jewellery: nothing listed yet
        var placed = QuestBook.Empty.WithExtraNpcs(Path.Combine(AppContext.BaseDirectory, "content", "npc-extras.json")).Npcs;
        Assert.Contains(placed, n => n.Template == 177 && n.Zone == 7 && n.Conversation == 181);
        var world = QuestBook.Load(Path.Combine(Path.GetDirectoryName(BalancePath)!, "world.json"))
            .WithExtraNpcs(Path.Combine(AppContext.BaseDirectory, "content", "npc-extras.json"));
        if (world.Npcs.FirstOrDefault(n => n.Template == 287) is { } ealdwin) Assert.Equal(330f, ealdwin.Direction); // turned round
    }

    [Fact]
    public void Gear_skill_bonuses_are_ranks_or_damage_as_the_item_says()
    {
        if (!File.Exists(BalancePath)) return;
        bool Damage(int item, int amount) => CharacterSheet.IsDamageBonus(Data.Value.Items[item], (13, amount)); // Sharp Shot
        Assert.False(Damage(56236, 15));   // Godly Aetheric Ring: +15 ranks…
        Assert.True(Damage(56236, 1013));  // …and +1013 damage
        Assert.False(Damage(21367, 10));   // Godly Dragon Ring: +10 ranks
        Assert.True(Damage(50434, 10));    // Minor Zealot's Ring: "boosts the damage … by 10"
        Assert.True(Damage(34228, 40));    // Minor Bracelet: "increases the power of"
        Assert.True(Damage(35605, 300));   // Godly Sunfire Charm: an additional 300 damage
    }
}
