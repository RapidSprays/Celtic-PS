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
    public void Vendors_pay_the_owners_price_then_half_the_lux_price_then_the_live_price()
    {
        if (!File.Exists(BalancePath)) return;
        var extras = Path.Combine(AppContext.BaseDirectory, "content", "shop-extras.json");
        var prices = Path.Combine(Path.GetDirectoryName(BalancePath)!, "item-prices.json");
        var shops = new ShopCatalog(Data.Value, new Dictionary<int, ShopInfo>(), ShopCatalog.LoadExtras(extras), ShopCatalog.LoadReplaced(extras))
        {
            GrimoirePrices = ShopCatalog.LoadPrices(prices),
            SellPrices = new Dictionary<int, int> { [15055] = 123 },
        };
        if (File.Exists(prices))
        {
            Assert.Equal(500, shops.SellPrice(Data.Value.Items[15054]));   // Sapphire, as on live
            Assert.Equal(1500, shops.SellPrice(Data.Value.Items[15057]));  // Diamond, as on live
        }
        Assert.Equal(123, shops.SellPrice(Data.Value.Items[15055]));       // the owner's own price wins
        Assert.Equal(100000, shops.SellPrice(Data.Value.Items[20808]));    // Master's Grimoire: half its 200,000 lux price
    }

    [Fact]
    public void Pet_vendors_take_only_orange_pet_tokens()
    {
        Assert.Equal([(TokenVendorCatalog.PetToken, 1)], TokenVendorCatalog.OrangeTokensOnly([(TokenVendorCatalog.PetToken, 1)]));
        Assert.Equal([(TokenVendorCatalog.PetToken, 2)], TokenVendorCatalog.OrangeTokensOnly([(TokenVendorCatalog.UncommonPetToken, 1)]));
        Assert.Equal([(TokenVendorCatalog.PetToken, 3)], TokenVendorCatalog.OrangeTokensOnly([(TokenVendorCatalog.RarePetToken, 1)]));
        Assert.Equal([(TokenVendorCatalog.PetToken, 4)], TokenVendorCatalog.OrangeTokensOnly([(TokenVendorCatalog.RarePetToken, 2)]));
        Assert.Equal([(60145, 8)], TokenVendorCatalog.OrangeTokensOnly([(60145, 8)])); // Mount Tokens unchanged
    }

    [Fact]
    public void The_mount_vendors_hellsteed_and_nightmare_shops_have_stock()
    {
        var extras = Path.Combine(AppContext.BaseDirectory, "content", "token-vendor-extras.json");
        var vendors = TokenVendorCatalog.Load(null, extras).Vendors;
        Assert.Equal([(65776, 1)], vendors[438].Stock.Single().Cost);              // Tame Hellsteed for a Dark Essence
        Assert.Equal(65770, vendors[438].Stock.Single().Template);
        Assert.Equal([(65770, 1), (65776, 1)], vendors[439].Stock[0].Cost);       // Trained: the Tame one and an essence
        Assert.Equal(6, vendors[458].Stock[5].Quantity);                          // a Primal sells for six essences
        Assert.Equal(4, vendors[485].Stock.Count);                                // the four Tame Nightmare Steeds
        foreach (var id in new[] { 486, 488, 489, 534 }) Assert.Equal(5, vendors[id].Stock.Count); // James: Trained … Primal
        foreach (var id in new[] { 182, 186, 190, 194, 267, 318, 322, 487, 490, 491, 535 }) Assert.NotEmpty(vendors[id].Stock);
        // Spirits (owner's rules): two Primal Silver War Wolves make a Spirit War Wolf, two of those a Spirit War Wolf II.
        Assert.Contains(vendors[379].Stock, e => e.Template == 65050 && e.Cost.SequenceEqual([(60169, 2)]));
        Assert.Contains(vendors[602].Stock, e => e.Template == 73001 && e.Cost.SequenceEqual([(65050, 2)]));
        Assert.Contains(vendors[469].Stock, e => e.Template == 66120 && e.Cost.SequenceEqual([(58712, 2)])); // Spirit Hare: two Giant Golden Hares
        foreach (var id in Enumerable.Range(602, 35).Concat([457, 469, 470, 471, 472, 473, 474, 475])) Assert.NotEmpty(vendors[id].Stock);
    }

    [Fact]
    public void Spawn_tuning_multiplies_weights_and_sets_respawns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ch-spawn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "t.json"), """
            { "useLiveRespawnTimes": true, "defaultRespawnSeconds": 45,
              "monsters": [ { "id": 141029, "divine": true, "weightMultiplier": 5, "respawnSeconds": 300, "extraDrops": [ { "id": 15057, "chance": 100, "quantity": 2 }, { "id": 15054, "chance": 0 } ] },
                            { "id": 70413, "weightMultiplier": 1, "respawnSeconds": null, "extraDrops": [] } ],
              "spawnPoints": [ { "spawnId": 7, "respawnSeconds": 60, "y": -58.31, "scale": 1.3 } ] }
            """);
        File.WriteAllText(Path.Combine(dir, "r.json"), """{ "respawns": { "400": [120, 120] } }""");
        var t = SpawnTuning.Load(Path.Combine(dir, "t.json"), Path.Combine(dir, "r.json"));
        Assert.Equal(600, t.Weight(141029, 120));                         // Boar Wisehoof × 5
        Assert.Equal(4677, t.Weight(70413, 4677));                        // unchanged
        Assert.Equal(300, t.RespawnSeconds(141029, 400, new Random(1)));  // the monster's own time
        Assert.Equal(120, t.RespawnSeconds(70413, 400, new Random(1)));   // live time for its spawn point
        Assert.Equal(60, t.RespawnSeconds(141029, 7, new Random(1)));     // the spawn point's time beats the monster's
        Assert.Equal(45, t.RespawnSeconds(70413, 999, new Random(1)));    // no live time: the default
        Assert.Equal((-58.31f, 1.3f), (t.Y(7, -60.31f), t.Scale(7)));     // Anmoch's spot: lifted and bigger
        Assert.True(t.TakesDivine(141029));                               // marked: takes Divine Strike
        Assert.False(t.TakesDivine(70413));                               // not marked: none
        Assert.Equal((-60.31f, (float?)null), (t.Y(8, -60.31f), t.Scale(8)));
        Assert.Equal([(15057, 2)], t.RollExtraDrops(141029, new Random(1)).ToList()); // 100%: always; 0%: never
        Assert.Empty(t.RollExtraDrops(70413, new Random(1)));
        File.WriteAllText(Path.Combine(dir, "n.json"), """
            { "monsters": [ { "id": 1, "noDropChance": 100, "extraDrops": [ { "id": 15057, "chance": 100 } ] },
                            { "id": 2, "noDropChance": 0, "extraDrops": [ { "id": 15057, "chance": 30 }, { "id": 15054, "chance": 70 } ] } ] }
            """);
        var n = SpawnTuning.Load(Path.Combine(dir, "n.json"), null);
        Assert.Empty(n.RollExtraDrops(1, new Random(1)));                                    // 100% no drop
        for (var i = 0; i < 50; i++) Assert.Single(n.RollExtraDrops(2, new Random(i)));      // 0% no drop: always exactly one
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Divine_strike_hits_between_the_owners_min_and_max()
    {
        // A sample file, so the owner's own numbers in content/divine-strike.json never break the test.
        var sample = Path.Combine(Path.GetTempPath(), "ch-divine-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(sample, """
            { "weaponProc": [ { "rank": 4, "min": 3000, "max": 3000 } ], "setAura": [ { "aura": "Warden's Aura", "min": 50, "max": 50 } ],
              "offhandSkills": [ { "skill": 2213, "min": 6000, "max": 6000 }, { "effect": 5323, "min": 2000, "max": 2000 } ] }
            """);
        var d = DivineStrikeDamage.Load(sample);
        File.Delete(sample);
        Assert.Equal(3000, d.Weapon(4, new Random(1)));                 // Exalted Dragonlord weapons
        Assert.Equal(50, d.Aura(["Warden's Aura"], new Random(1)));
        Assert.Null(d.Weapon(9, new Random(1)));                        // not listed: the game data's
        Assert.Equal(6000, d.Offhand(2213, new Random(1)));              // Dragonlord Fury
        Assert.Equal(2000, d.Offhand(5323, new Random(1)));              // Eldritch Spite's tick
        // The real file loads, and every range in it is in order.
        var real = DivineStrikeDamage.Load(Path.Combine(AppContext.BaseDirectory, "content", "divine-strike.json"));
        Assert.NotNull(real.Weapon(4, new Random(1)));
        if (File.Exists(BalancePath))
        {
            var path2 = Path.Combine(Path.GetTempPath(), "ch-divine-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path2, """{ "offhandBuffs": [ { "effect": 2144, "amount": 950 } ] }""");
            var balance = Balance.Load(BalancePath);
            Assert.Equal(1, DivineStrikeDamage.Load(path2).ApplyBuffs(balance));
            Assert.Equal(950f, balance.StatusEffects[2144].Levels[0].BaseAmount);   // Dragonlord Wrath now +950
            Assert.Equal(0f, balance.StatusEffects[2144].Levels[0].Amount);
            File.Delete(path2);
        }
        var path = Path.Combine(Path.GetTempPath(), "ch-divine-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "weaponProc": [ { "rank": 3, "min": 800, "max": 1200 } ] }""");
        var r = DivineStrikeDamage.Load(path);
        for (var i = 0; i < 100; i++) Assert.InRange(r.Weapon(3, new Random(i))!.Value, 800, 1200);
        File.Delete(path);
    }

    [Fact]
    public void Boss_spots_start_with_a_random_four_five_or_six_star()
    {
        MobInfo Info(int id, int power) => new(id, id, $"m{id}", "", 0, 1, 1, 2000, power);
        MobVariant V(int id, int power, double weight) => new(Info(id, power), 100, 1000, weight);
        var seen = new HashSet<int>();
        for (var i = 0; i < 200; i++)
        {
            var mob = new Mob
            {
                ServerId = 1, Template = Info(1, 1), Level = 1, HomeX = 0, HomeY = 0, HomeZ = 0, Opinion = 0,
                Variants = [V(1, 1, 1000), V(4, 4, 50), V(5, 5, 35), V(6, 6, 15)],
            };
            mob.RollStartTier(new Random(i));
            Assert.InRange(mob.Template.Power, 4, 6);   // never the 1-star, despite its weight
            seen.Add(mob.Template.Power);
        }
        Assert.Equal(3, seen.Count);                   // all three tiers come up

        // startAnyTier (the Carrowmore gold bosses): the 3-star placeholder or the 5-star boss, both come up.
        var gold = new HashSet<int>();
        for (var i = 0; i < 100; i++)
        {
            var spider = new Mob
            {
                ServerId = 2, Template = Info(3, 3), Level = 1, HomeX = 0, HomeY = 0, HomeZ = 0, Opinion = 0,
                Variants = [V(3, 3, 90), V(5, 5, 10)],
            };
            spider.RollStartTier(new Random(i), anyTier: true);
            gold.Add(spider.Template.Power);
        }
        Assert.Equal(new HashSet<int> { 3, 5 }, gold);
    }

    [Fact]
    public void Call_for_help_reaches_the_owners_share_of_the_script_range()
    {
        var path = Path.Combine(Path.GetTempPath(), "ch-call-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "rangeMultiplier": 0.5, "maxRange": 15, "monsters": [ { "id": 7, "range": 0 }, { "id": 8, "range": null } ] }""");
        var c = CallForHelpRanges.Load(path);
        File.Delete(path);
        Assert.Equal(10f, c.Range(1, 20));   // half of 20
        Assert.Equal(15f, c.Range(1, 60));   // half of 60, capped at 15
        Assert.Equal(0f, c.Range(7, 40));    // its own range: never calls
        Assert.Equal(5f, c.Range(8, 10));    // null: the multiplier
        Assert.Equal(20f, CallForHelpRanges.None.Range(1, 20)); // no file: the script's range
    }

    [Fact]
    public void Dot_ticks_vary_between_the_owners_percents_and_get_damage_types()
    {
        var path = Path.Combine(Path.GetTempPath(), "ch-dot-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "default": { "minPercent": 80, "maxPercent": 100 }, "dots": [ { "effect": 133, "minPercent": 50, "maxPercent": 50, "damageType": "crush" } ] }""");
        var d = DotDamage.Load(path);
        File.Delete(path);
        for (var i = 0; i < 100; i++) Assert.InRange(d.Factor(10, new Random(i)), 0.8, 1.0); // Rend: the default
        Assert.Equal(0.5, d.Factor(133, new Random(1)), 6);                                // Rupture: its own
        Assert.Equal(1.0, DotDamage.None.Factor(10, new Random(1)), 6);                    // no file: full ticks
        if (!File.Exists(BalancePath)) return;
        var balance = Balance.Load(BalancePath);
        Assert.Equal(1, d.ApplyTypes(balance));
        Assert.Equal(2, balance.StatusEffects[133].DamageType);                            // Rupture now crush
    }

    [Fact]
    public void Threat_follows_the_owners_numbers()
    {
        var path = Path.Combine(Path.GetTempPath(), "ch-threat-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "damageMultiplier": 1.5, "switchMargin": 1.25, "classMultipliers": { "Warrior": 2 }, "skills": [ { "id": 8, "flatThreat": 0 }, { "id": 215, "multiplier": 3 }, { "id": 3, "flatThreat": null, "multiplier": 0.5 } ] }""");
        var t = ThreatRules.Load(path);
        File.Delete(path);
        Assert.Equal((1.5, 1.25), (t.DamageMultiplier, t.SwitchMargin));
        Assert.Equal(2, t.ClassMultiplier((int)CharacterClass.Warrior));
        Assert.Equal(1, t.ClassMultiplier((int)CharacterClass.Druid));
        Assert.Equal(0, t.SkillFlat(8, 2324));      // Strangling Vines: none
        Assert.Equal(830, t.SkillFlat(3, 1660));    // Shield Bash: half the data's
        Assert.Equal(3, t.SkillDamageMultiplier(215));
        Assert.Equal(1660, ThreatRules.Defaults.SkillFlat(3, 1660));
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
