using CH.Server.Game;

namespace CH.Tests;

/// <summary>Owner's gear with typed-in numbers (content/custom-gear.json): the examples load with exactly their numbers.</summary>
public sealed class CustomGearTests
{
    private static readonly string BalancePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesAnalysis/extracted/24928510/balance-client.json");

    private static readonly string Content = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../content"));

    [Fact]
    public void A_custom_ring_and_sword_carry_only_their_own_numbers()
    {
        var balance = Balance.Load(BalancePath, Path.Combine(Content, "custom-items.json"));
        var ring = balance.Items[90100];
        Assert.Equal("Ring of the Fortunate", ring.Name);
        Assert.Equal(EquipSlot.RingR1, ring.Slot);
        Assert.Equal([(31, 800), (32, 800)], ring.AbilityBonuses.OrderBy(a => a.Id).ToList());
        Assert.Contains(("HealthBonus", 150), ring.Bonuses);
        Assert.Contains(("ColdResist", 15), ring.Bonuses);
        Assert.Equal(100, ring.LevelRequirement);
        Assert.Empty(ring.Classes);

        var sword = balance.Items[90101];
        Assert.Equal(EquipSlot.Weapon, sword.Slot);
        Assert.Equal(340, sword.Damage);
        Assert.Equal(260, sword.PhysicalDamage);
        Assert.Equal(["Warrior"], sword.Classes);
        Assert.Equal(0.05f, sword.ProcSkillChance, 3);
        Assert.True(sword.ProcSkillId > 0);
    }

    [Fact]
    public void Gilthral_is_a_level_210_killain_with_his_own_daily_spar()
    {
        if (!File.Exists(BalancePath)) return;
        var balance = Balance.Load(BalancePath, Path.Combine(Content, "custom-items.json"));
        Assert.Equal("Gilthral the Gladiator", balance.Mobs[90300].Name);
        Assert.Equal(balance.Mobs[73804].TemplateId, balance.Mobs[90300].TemplateId); // Killain's look
        var monster = CustomContent.LoadMonsters(Path.Combine(Content, "custom-monsters.json")).Single(m => m.Id == 90300);
        var killain = new MobCombat(170, 81141, 1695, 340, [(1, 240)]) { Resists = new Dictionary<int, (int, double)> { [0] = (340, 1.0) } };
        var combat = CustomContent.Combat(monster, killain)!;
        Assert.Equal((210, 140000, 1695, 340), (combat.Level, combat.Health, combat.Attack, combat.Defence));
        Assert.Equal(killain.Resists, combat.Resists);            // his resists, unchanged

        var world = Path.Combine(Path.GetDirectoryName(BalancePath)!, "world.json");
        var quests = QuestBook.Load(world).WithCustomQuests(CustomContent.LoadQuests(Path.Combine(Content, "custom-quests.json"))).Quests;
        var spar = quests[9001];
        Assert.Equal("Spar with Gilthral", spar.Name);
        Assert.Equal("DayRepeat", spar.Archetype);
        Assert.Equal(90300, spar.Stages[0].Target.Id);              // defeat Gilthral
        Assert.Equal((90150, 1), spar.Stages[1].Target);           // his medal
        Assert.Equal([90300], spar.Stages[1].Sources);             // which he drops
        Assert.Equal(90150, spar.Stages[2].Target.Id);              // handed in
        Assert.Equal(73804, quests[506].Stages[0].Target.Id);       // Killain's spar unchanged
    }

    [Fact]
    public void The_heretic_rings_add_double_shot_damage_and_regen()
    {
        if (!File.Exists(BalancePath)) return;
        var balance = Balance.Load(BalancePath, Path.Combine(Content, "custom-items.json"));
        var signet = balance.Items[90113];
        Assert.Equal(130, signet.LevelRequirement);
        Assert.Contains(("Vitality", 80), signet.StatBonuses);
        Assert.Contains(("AttackBonus", 300), signet.Bonuses);
        Assert.Equal(400, signet.Damage); // 100 poison, 150 cold, 150 heat
        var sheet = new[] { signet };
        Assert.True(CharacterSheet.IsDamageBonus(signet, (252, 125)));     // flat damage, not 125 ranks
        var regen = GearRegen.Of(signet);
        Assert.Equal((25, 25), (regen.Health, regen.Energy));
        Assert.True(CharacterSheet.IsDamageBonus(balance.Items[90110], (252, 50)));
    }

    [Fact]
    public void Denya_has_her_own_resists_evasion_rewards_and_four_abilities()
    {
        var denya = CustomContent.LoadMonsters(Path.Combine(Content, "custom-monsters.json")).Single(m => m.Id == 90301);
        var priestess = new MobCombat(160, 50000, 500, 300, [(5, 400)])
        {
            Resists = new Dictionary<int, (int, double)> { [6] = (100, 1.0), [7] = (0, 0.0) },
            Evade = new Dictionary<int, int> { [0] = 100 },
        };
        var c = CustomContent.Combat(denya, priestess)!;
        Assert.Equal((115, 250000, 600, 850), (c.Level, c.Health, c.Attack, c.Defence));
        Assert.Equal((300, 1.0), c.Resists[6]);   // poison
        Assert.Equal((400, 1.0), c.Resists[4]);   // cold
        Assert.Equal((0, 0.0), c.Resists[7]);     // divine as the priestess has it
        Assert.All(Enumerable.Range(0, 6), t => Assert.Equal(500, c.Evade[t]));
        Assert.Equal((200000, (6500, 6500)), (c.Xp, c.Gold));
        Assert.Equal(3f, denya.Scale);
        var abilities = BossAbility.Parse(denya.AbilitiesJson!.Value, name => name == "Shield Bash" ? 46 : null);
        Assert.Equal(["weaponBuff", "selfHot", "selfHeal", "aoeStun"], abilities.Select(a => a.Kind).ToList());
        var venom = abilities[0];
        Assert.Equal((6, 75, 60.0, 30.0), (venom.DamageType, venom.Damage, venom.Duration, venom.RecastAfter));
        var touch = abilities[2];
        Assert.Equal([85.0, 60, 40, 20, 10], touch.AtHealth);
        Assert.Contains(46, touch.InterruptedBy);
        var mez = abilities[3];
        Assert.Equal((45.0, 4.0, 150, 2), (mez.Cooldown, mez.StunSeconds, mez.Damage, mez.EvadedBy));

        var state = new BossState(abilities);
        state.ActiveUntil[venom] = 100;
        Assert.Equal([(6, 75)], state.HitBonuses(50).ToList());
        Assert.Empty(state.HitBonuses(150));
    }

    [Fact]
    public void Monster_overrides_change_only_what_the_file_sets()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var stats = Path.Combine(dir, "mob-stats.json");
        File.WriteAllText(stats, """{"mobs": {"70722": {"level": 180, "health": 900000, "attack": 2000, "defence": 500, "xp": 5000, "gold": [100, 200], "resists": [[0, 300, 1.0], [5, 300, 0.5]], "evade": {"0": 400}}}}""");
        var file = Path.Combine(dir, "monster-overrides.json");
        File.WriteAllText(file, """{"monsters": [{"id": 70722, "health": 1500000, "gold": 9000, "resists": {"Poison": 450}, "evasion": {"all": 600}}]}""");
        var official = OfficialMobStats.Load(stats);
        Assert.Equal(1, official.ApplyOverrides(file));
        Assert.True(official.TryGet(70722, out var c));
        Assert.Equal((180, 1500000, 2000, 500, 5000), (c.Level, c.Health, c.Attack, c.Defence, c.Xp));
        Assert.Equal((9000, 9000), c.Gold);
        Assert.Equal((300, 1.0), c.Resists[0]);
        Assert.Contains(c.Resists, r => r.Value.Flat == 450);
        Assert.All(c.Evade.Values, v => Assert.Equal(600, v));
    }

    [Fact]
    public void A_skill_can_count_less_of_a_monsters_evasion_and_have_a_floor()
    {
        if (!File.Exists(BalancePath)) return;
        var balance = Balance.Load(BalancePath, null);
        Assert.Equal((100.0, 0.0), (balance.Skills[192].EvasionPercent, balance.Skills[192].MinLandChance));
        var file = Path.Combine(Directory.CreateTempSubdirectory().FullName, "skill-overrides.json");
        File.WriteAllText(file, """{"192": {"name": "Shadowstrike", "evasionPercent": 50, "minLandChance": 60}}""");
        SkillOverrides.Apply(balance, file);
        Assert.Equal((50.0, 60.0), (balance.Skills[192].EvasionPercent, balance.Skills[192].MinLandChance));
    }

    [Fact]
    public void A_copy_of_a_monster_without_official_numbers_keeps_the_owners_numbers()
    {
        var m = new CustomContent.Monster(90399, 71377, "Chicken", 130, 1200000, 1500, 1000, 1) { Resists = new Dictionary<int, int> { [0] = 600 } };
        var combat = CustomContent.Combat(m, null)!;
        Assert.Equal((130, 1200000, 1500, 1000), (combat.Level, combat.Health, combat.Attack, combat.Defence));
        Assert.Equal((600, 1.0), combat.Resists[0]);
    }

    [Fact]
    public void A_monsters_auto_attack_can_be_set_outright()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory().FullName, "custom-monsters.json");
        File.WriteAllText(file, """{"monsters": [{"id": 90399, "base": 71377, "name": "Chicken", "level": 130, "health": 9, "hitDamage": 900, "hitDamageType": "Poison"}, {"id": 90398, "base": 70722, "name": "Copy", "level": 1, "health": 1, "hitDamage": 500}, {"id": 90397, "base": 70722, "name": "Copy", "level": 1, "health": 1, "damageScale": 2}]}""");
        var m = CustomContent.LoadMonsters(file);
        var baseDamage = new List<(int, int)> { (1, 570), (3, 285) };
        Assert.Equal([(6, 900)], CustomContent.Combat(m[0], null)!.Damage);
        Assert.Equal([(1, 500)], CustomContent.Combat(m[1], new MobCombat(1, 1, 0, 0, baseDamage))!.Damage);
        Assert.Equal([(1, 1140), (3, 570)], CustomContent.Combat(m[2], new MobCombat(1, 1, 0, 0, baseDamage))!.Damage);
    }

    [Fact]
    public void Ysolde_at_the_lirs_reach_leystones_trades_two_heretic_rings_for_the_next()
    {
        if (!File.Exists(BalancePath)) return;
        var world = Path.Combine(Path.GetDirectoryName(BalancePath)!, "world.json");
        var vendor = TokenVendorCatalog.Load(world, Path.Combine(Content, "token-vendor-extras.json")).Vendors[9200];
        Assert.Equal([(90111, 90110), (90112, 90111), (90113, 90112)], vendor.Stock.Select(x => (x.Template, x.Cost.Single().Template)).ToList());
        Assert.All(vendor.Stock, x => Assert.Equal(2, x.Cost.Single().Quantity));
        var npc = QuestBook.Load(world).WithExtraNpcs(Path.Combine(Content, "npc-extras.json")).Npcs.Single(n => n.Template == 90303);
        Assert.Equal((2, 20000), (npc.Zone, npc.Conversation));
        Assert.Equal("[Ring Exchange] Ysolde the Ringwright", Balance.Load(BalancePath, Path.Combine(Content, "custom-items.json")).Mobs[90303].Name);
    }
}
