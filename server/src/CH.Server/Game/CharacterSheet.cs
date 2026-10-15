using System.Text.Json;
using CH.Persistence;
using CH.Protocol.Messages;

namespace CH.Server.Game;

/// <summary>
/// A character's full state for the wire: stats from class kit, level and equipped gear, item lists, skills and the
/// action bar. Stat values are <b>approximated</b> (docs/decisions.md); gear numbers (damage, armour, speed, range,
/// weight) come from the client's item templates.
/// </summary>
public sealed class CharacterSheet
{
    public const int SkillMaxLevelAtStart = 4; // template levels 0-4 need no minimum character level
    public const int HudSlotsPerSet = 8;
    private const int HealthPerLevel = 8;
    private const int EnergyPerLevel = 3;

    private readonly Balance _balance;

    public CharacterSheet(CharacterRecord character, CharacterProgress progress, IReadOnlyList<ItemRecord> items,
        IReadOnlyList<(int SkillId, int Level)> skills, Balance balance, TreeBonuses? tree = null)
    {
        Tree = tree ?? TreeBonuses.None;
        var t = Tree;
        Character = character;
        Progress = progress;
        Items = items;
        Skills = skills;
        _balance = balance;
        Kit = ClassKit.For(character.Class);

        var level = Math.Max(1, progress.Level);
        var (baseVit, baseStr, baseFoc, baseDex) = ClassKit.BaseAttributes;
        Strength = progress.Strength < 0 ? baseStr : progress.Strength;
        Dexterity = progress.Dexterity < 0 ? baseDex : progress.Dexterity;
        Focus = progress.Focus < 0 ? baseFoc : progress.Focus;
        Vitality = progress.Vitality < 0 ? baseVit : progress.Vitality;
        var spentAttributes = Strength + Dexterity + Focus + Vitality - (baseStr + baseDex + baseFoc + baseVit);
        AttributePoints = Math.Max(0, Progression.AttributePointsPerLevel * (level - 1) - spentAttributes);
        SkillPoints = Math.Max(0, Progression.SkillPointsPerLevel * (level - 1) - skills.Sum(k => k.Level));

        var gear = Equipped.Select(i => _balance.Items.GetValueOrDefault(i.TemplateId)).OfType<ItemInfo>().ToList();
        // Completed sets: armour auras give the owner's health, energy and Divine Strike; other sets add their reward
        // item's own bonuses from the game data, as if it were worn.
        var setHealth = 0;
        var setEnergy = 0;
        var auras = new List<string>();
        var auraEffects = new List<int>();
        foreach (var set in _balance.SetBonuses.ActiveSets(Equipped.Select(i => i.TemplateId).ToList()))
        {
            if (!_balance.Items.TryGetValue(set.Reward, out var reward)) continue;
            if (set.Effect is { } effect) auraEffects.Add(effect);
            if (_balance.SetBonuses.AuraFor(reward) is { } aura)
            {
                setHealth += aura.Health;
                setEnergy += aura.Energy;
                DivineDamage += aura.Divine;
                auras.Add(aura.Name);
            }
            else
            {
                gear.Add(reward);
                auras.Add(reward.Name.Trim());
            }
        }
        SetBonuses = auras;
        AuraEffects = auraEffects;
        Gear = gear;
        // Passive-tree attributes count as bonuses (combined), never as spent base points.
        CombinedStrength = Strength + gear.Sum(g => g.StatBonus("Strength")) + (int)t["str"];
        CombinedDexterity = Dexterity + gear.Sum(g => g.StatBonus("Dexterity")) + (int)t["dex"];
        CombinedFocus = Focus + gear.Sum(g => g.StatBonus("Focus")) + (int)t["foc"];
        CombinedVitality = Vitality + gear.Sum(g => g.StatBonus("Vitality")) + (int)t["vit"];

        var healthKeystones = (t.Has(PassiveTree.AssassinsMark) ? 0.75 : 1) * (t.Has(PassiveTree.GlassCannon) ? 0.7 : 1);
        MaxHealth = Math.Max(1, (int)((Kit.Health + (level - 1) * HealthPerLevel + (CombinedVitality - baseVit) * 6 + gear.Sum(g => g.Bonus("HealthBonus")) + setHealth + t["health"])
            * (1 + t["healthPct"] / 100) * healthKeystones));
        MaxEnergy = Math.Max(1, (int)((Kit.Energy + (level - 1) * EnergyPerLevel + (CombinedFocus - baseFoc) * 4 + gear.Sum(g => g.Bonus("EnergyBonus")) + setEnergy + t["energy"])
            * (1 + t["energyPct"] / 100)));
        Health = progress.Health < 0 ? MaxHealth : Math.Clamp(progress.Health, 0, MaxHealth);
        Energy = progress.Energy < 0 ? MaxEnergy : Math.Clamp(progress.Energy, 0, MaxEnergy);

        var equipped = Equipped.Select(i => _balance.Items.GetValueOrDefault(i.TemplateId)).Where(i => i is not null).Select(i => i!).ToList();
        var weapon = Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Weapon) is { } w ? _balance.Items.GetValueOrDefault(w.TemplateId) : null;
        var offhand = Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Offhand) is { } o ? _balance.Items.GetValueOrDefault(o.TemplateId) : null;
        // The stats page's "Damage" is this number as sent (GUIStatsPage shows the WeaponDamage stat unchanged), and the
        // auto-attack uses it: the damage of everything worn, not just the weapon. Arrows, offhand daggers, bangles,
        // necklaces and set bonuses carry damage too. Armour auras' Divine Strike stays a separate proc.
        WeaponDamage = Math.Max(1, gear.Sum(g => g.Damage));
        WeaponSubtype = weapon?.Subtype ?? "None";
        OffhandSubtype = offhand?.Subtype ?? "None";
        AttackSpeed = weapon is { Speed: > 0 } ? weapon.Speed : 1000;
        AttackRange = weapon is { Range: > 0 } ? weapon.Range + (offhand is { Subtype: "Arrow" } ? offhand.Range : 0) : 1f;
        Armour = (int)((equipped.Sum(i => i.Armour) + t["armour"]) * (1 + t["armourPct"] / 100)
            * (t.Has(PassiveTree.IronHide) ? 1.5 : 1) * (t.Has(PassiveTree.Windrunner) ? 0.7 : 1));
        Encumbrance = equipped.Sum(i => i.Weight);
        var ranged = weapon is { Range: > 3 };
        _attackBase = Kit.Attack + level + (ranged ? CombinedDexterity - baseDex : CombinedStrength - baseStr) + gear.Sum(g => g.Bonus("AttackBonus")) + t["attack"];
        _attackFactor = 1 + (t["attackPct"] + gear.Sum(g => g.AttackPercent)) / 100;
        _weaponAbility = GameServer.WeaponAbility(weapon);
        // Owner's rule (2026-10-14): 2 Defence per point of Dexterity; level and armour don't add Defence (armour is
        // physical resist instead). Defence only decides how often monsters hit: attack ÷ (attack + defence).
        Defence = (int)((Kit.Defence + 2 * CombinedDexterity + gear.Sum(g => g.Bonus("DefenceBonus")) + t["defence"])
            * (1 + t["defencePct"] / 100));
    }

    /// <summary>Regen per tick from everything equipped (mount and pet included): always, and out of combat only.</summary>
    public (int Health, int Energy, int HealthOutOfCombat, int EnergyOutOfCombat) GearRegenPerTick
    {
        get
        {
            var all = Gear.Select(GearRegen.Of).Where(r => r.Any).ToList();
            return (all.Where(r => !r.OutOfCombatOnly).Sum(r => r.Health), all.Where(r => !r.OutOfCombatOnly).Sum(r => r.Energy),
                all.Where(r => r.OutOfCombatOnly).Sum(r => r.Health), all.Where(r => r.OutOfCombatOnly).Sum(r => r.Energy));
        }
    }

    /// <summary>The character's passive-tree bonuses (docs/passive-tree.md).</summary>
    public TreeBonuses Tree { get; }

    /// <summary>Base 5% critical strike chance, plus passives; Assassin's Mark doubles it.</summary>
    public double CritChance => (0.05 + Tree["critChance"] / 100) * (Tree.Has(PassiveTree.AssassinsMark) ? 2 : 1);

    /// <summary>Base ×1.5 on a critical strike; passives add to the extra, Assassin's Mark doubles the extra.</summary>
    public double CritMultiplier => 1 + (0.5 + Tree["critMulti"] / 100) * (Tree.Has(PassiveTree.AssassinsMark) ? 2 : 1);

    public double AttackDamageFactor => (1 + (Tree["damagePct"] + Tree["attackDamagePct"]) / 100) * (Tree.Has(PassiveTree.GlassCannon) ? 1.3 : 1);

    public double SkillDamageFactor(int skillId) =>
        (1 + (Tree["damagePct"] + Tree["skillDamagePct"] + Tree.For(_balance.SkillTags(skillId)).DamagePct) / 100) * (Tree.Has(PassiveTree.GlassCannon) ? 1.3 : 1);

    public double HealFactor(int skillId) => 1 + (Tree["healPct"] + Tree.For(_balance.SkillTags(skillId)).DamagePct) / 100;

    /// <summary>
    /// Share of a skill's cooldown left after gear and passives: the best item that cuts it (items say they don't stack
    /// with others for the same skill), times the passive tree's cut.
    /// </summary>
    public double RechargeFactor(int skillId)
    {
        var gear = Gear.SelectMany(g => _balance.RecastReductions(g)).Where(r => r.Skill == skillId).Select(r => r.Percent).DefaultIfEmpty(0).Max();
        return Math.Max(0, (1 - gear / 100.0) * (1 - Tree.For(_balance.SkillTags(skillId)).CooldownPct / 100));
    }

    public int EnergyCost(int skillId, int cost) => (int)Math.Ceiling(cost * Math.Max(0, 1 - Tree.For(_balance.SkillTags(skillId)).CostPct / 100));

    /// <summary>Extra chance for monsters to miss; Iron Hide means they never miss.</summary>
    public double EvadeChance => Tree["evadePct"] / 100;

    public int AttackSpeedPercent => (int)Tree["attackSpeedPct"] + (Tree.Has(PassiveTree.Windrunner) ? 25 : 0) + GearHastePercent;

    /// <summary>
    /// The attribute part of a skill's strength, before the square root: the skill's primary attribute ÷ its divisor
    /// (Sharp Shot: dexterity ÷ 6). Zero for skills without one. Master's Grimoire's formula for skills and their buffs is
    /// ⌈base + initial × (1 + √(ability ÷ 100) + √(this))⌉ (<see cref="StatusEffectLevel.ModifiedAmount"/>).
    /// </summary>
    public double StatRatio(SkillInfo skill)
    {
        var stat = skill.PrimaryStat switch
        {
            0 => CombinedStrength,
            1 => CombinedDexterity,
            2 => CombinedFocus,
            3 => CombinedVitality,
            _ => 0,
        };
        return stat <= 0 ? 0 : stat / (double)(skill.PrimaryStatDivisor > 0 ? skill.PrimaryStatDivisor : 1);
    }

    /// <summary>Pierce, slash and crush damage of everything worn: the part Steady Aim and Frenzy raise by a percentage.</summary>
    public int PhysicalDamage => Gear.Sum(g => g.PhysicalDamage);

    /// <summary>Gear avoidance (AvoidanceBonuses) against one avoidance type (0 physical … 5 mental).</summary>
    public int Avoidance(int avoidanceType) => Gear.SelectMany(g => g.AvoidanceBonuses).Where(a => a.Id == avoidanceType).Sum(a => a.Amount);

    /// <summary>
    /// Gear SkillBonuses up to this are extra ranks (Ring of Double Shot +1…+10, Exalted Dragonlord armour +8, Aetheric
    /// rings +15); larger ones are flat damage ("increases the damage from your Sharp Shot skill by up to 1013").
    /// </summary>
    public const int MaxRankBonus = 50;

    /// <summary>Flat damage gear adds to a skill (the large SkillBonuses).</summary>
    public int SkillBonusDamage(int skillId) =>
        Gear.SelectMany(g => g.SkillBonuses).Where(b => b.Id == skillId && Math.Abs(b.Amount) > MaxRankBonus).Sum(b => b.Amount);

    /// <summary>Damage of everything worn, by type (see <see cref="ItemInfo.DamageTypeIds"/>).</summary>
    public IReadOnlyDictionary<int, int> DamageByType => Gear.SelectMany(g => g.DamageByType)
        .GroupBy(d => d.Type).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));

    /// <summary>The best haste item worn (they don't stack with each other); buffs like Rapid Shot add on top.</summary>
    public int GearHastePercent => Gear.Select(g => g.HastePercent).DefaultIfEmpty(0).Max();

    public int RunSpeedPercent => (int)Tree["runSpeedPct"] + (Tree.Has(PassiveTree.Windrunner) ? 25 : 0);

    /// <summary>
    /// The rank a skill works at: trained rank, plus small gear SkillBonuses (large ones are flat damage), plus passive
    /// ranks. The client indexes Levels[compiled], so stay inside the (extended) table.
    /// </summary>
    public int EffectiveRank(int skillId, int trained) => Math.Clamp(
        trained + Gear.SelectMany(g => g.SkillBonuses).Where(b => b.Id == skillId && Math.Abs(b.Amount) <= MaxRankBonus).Sum(b => b.Amount)
            + Tree.For(_balance.SkillTags(skillId)).Ranks,
        0, Math.Max(0, (_balance.Skills.GetValueOrDefault(skillId)?.Levels.Count ?? 1) - 1));

    public CharacterRecord Character { get; }
    public CharacterProgress Progress { get; }
    public IReadOnlyList<ItemRecord> Items { get; }
    public IReadOnlyList<(int SkillId, int Level)> Skills { get; }
    public ClassKit Kit { get; }

    public int Level => Math.Max(1, Progress.Level);
    public int Strength { get; }
    public int Dexterity { get; }
    public int Focus { get; }
    public int Vitality { get; }

    /// <summary>Unspent points. Earned per level (<see cref="Progression"/>) minus what's been spent, so level changes stay consistent.</summary>
    public int AttributePoints { get; }
    public int SkillPoints { get; }

    /// <summary>Base attributes plus equipment PrimaryStatBonuses: what the stats page shows and what stats derive from.</summary>
    public int CombinedStrength { get; }
    public int CombinedDexterity { get; }
    public int CombinedFocus { get; }
    public int CombinedVitality { get; }

    /// <summary>The equipped mount (slot 22), if any. The client shows the player riding it (MountsManager.CheckForMount).</summary>
    public ItemInfo? Mount => Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Mount) is { } m ? _balance.Items.GetValueOrDefault(m.TemplateId) : null;

    /// <summary>Run speed comes from the "Mounted" status effect while riding (GameServer.Pets), not from the sheet.</summary>
    public int MountSpeedPercent => 0;

    /// <summary>Templates of everything equipped.</summary>
    public IReadOnlyList<ItemInfo> Gear { get; }


    /// <summary>Skills granted by equipped items (EquipSkillID), usable while worn, not trainable.</summary>
    public IReadOnlyList<int> EquipSkills => Gear.Where(g => g.EquipSkillId > 0 && _balance.Skills.ContainsKey(g.EquipSkillId))
        .Select(g => g.EquipSkillId).Distinct().Where(id => Skills.All(k => k.SkillId != id)).ToList();

    /// <summary>Learned skills plus equipment skills, as (id, rank).</summary>
    public IReadOnlyList<(int SkillId, int Level)> UsableSkills => Skills.Concat(EquipSkills.Select(id => (id, 0))).ToList();
    public int MaxHealth { get; }
    public int MaxEnergy { get; }
    public int Health { get; }
    public int Energy { get; }
    /// <summary>
    /// Attack: class, level, attribute, gear and passives, plus the level of the held weapon's ability (Bow, Sword…),
    /// one for one, so 2000 Bow ability adds 2000 attack. Computed on use, since ability levels are set after the sheet.
    /// </summary>
    public int Attack => (int)((_attackBase + Math.Max(0, AbilityLevel(_weaponAbility))) * _attackFactor);

    private readonly double _attackBase;
    private readonly double _attackFactor;
    private readonly int _weaponAbility;
    public int Defence { get; }
    public int WeaponDamage { get; }

    /// <summary>Set bonuses in effect (armour auras by name).</summary>
    public IReadOnlyList<string> SetBonuses { get; } = [];

    /// <summary>The client status effects for the sets worn ("Exalted Aura"), shown as permanent icons under the health bar.</summary>
    public IReadOnlyList<int> AuraEffects { get; } = [];

    /// <summary>
    /// Resist against a damage type (0 pierce, 1 slash, 2 crush, 3 heat, 4 cold, 5 magic, 6 poison, 7 divine, 8 chaos;
    /// the client's BonusType ids) from gear and set bonuses. True damage (9) has none.
    /// </summary>
    public int Resist(int damageType) =>
        Gear.SelectMany(g => g.Bonuses).Where(b => BonusTypeIds.TryGetValue(b.Type, out var id) && id == damageType).Sum(b => b.Amount);

    /// <summary>Divine Strike damage from armour set auras; procs on auto-attack hits (GameServer.World PlayerHits).</summary>
    public int DivineDamage { get; }

    /// <summary>Subtype of the weapon held ("Bow", "Sword"…), "None" when empty-handed.</summary>
    public string WeaponSubtype { get; }

    /// <summary>Subtype of the offhand item ("Shield", "Arrow"…), "None" when empty.</summary>
    public string OffhandSubtype { get; }

    /// <summary>Whether the weapon or offhand held meets the skill's weapon requirement (none → always).</summary>
    public bool CanUse(SkillInfo skill) =>
        skill.WeaponRequirements.Count == 0 || skill.WeaponRequirements.Contains(WeaponSubtype) || skill.WeaponRequirements.Contains(OffhandSubtype);
    public int AttackSpeed { get; }
    public float AttackRange { get; }
    public int Armour { get; }
    public int Encumbrance { get; }

    public IEnumerable<ItemRecord> Equipped => Items.Where(i => i.Slot >= 0);

    public IEnumerable<ItemRecord> Backpack => Items.Where(i => i.Slot == ItemRecord.Backpack);

    public static ItemSlot ToWire(ItemRecord i) => new((int)i.Id, i.TemplateId, i.Quantity, i.Bound, i.Charges, i.Favourite);

    /// <summary>The 25 equipment entries, empty slots as template -1.</summary>
    public IReadOnlyList<ItemSlot> EquipmentSlots()
    {
        var slots = Enumerable.Repeat(ItemSlot.Empty, CharacterSummary.EquipmentSlots).ToArray();
        foreach (var item in Equipped.Where(i => i.Slot < CharacterSummary.EquipmentSlots)) slots[item.Slot] = ToWire(item);
        return slots;
    }

    public IReadOnlyList<ItemSlot> BackpackSlots() => Backpack.Select(ToWire).ToList();

    public InventoryWithMoney InventoryWithMoney() => new(Progress.Coins, BackpackSlots());

    /// <summary>The highest rank a skill can reach: the end of its level table, capped at 50 ranks (see <see cref="SkillLevels"/>).</summary>
    /// <summary>The last rank in a skill's table (room for gear ranks past the training cap).</summary>
    public int TableMaxRank(int skillId) => _balance.Skills.TryGetValue(skillId, out var skill) && skill.Levels.Count > 0 ? skill.Levels.Count - 1 : SkillMaxLevelAtStart;

    public int MaxRank(int skillId) => _balance.Skills.TryGetValue(skillId, out var skill) && skill.Levels.Count > 0 ? skill.MaxRank(Level) : SkillMaxLevelAtStart;

    /// <summary>
    /// The skill list for the client. Its maximum is the whole (extended) table, not the training cap: the client shows a
    /// gear-boosted rank ("+8") only up to that maximum, so a 50 there hid every gear bonus on a trained skill. Buying
    /// ranks still stops at 50 (<see cref="MaxRank"/>, checked by BuySkill).
    /// </summary>
    public IReadOnlyList<SkillEntry> SkillEntries() =>
        Skills.Select(s => new SkillEntry(s.SkillId, s.Level, TableMaxRank(s.SkillId), 0f))
            .Concat(EquipSkills.Select(id => new SkillEntry(id, 0, 0, 0f)))
            .ToList();

    public CharacterSummary Summary(int worldId)
    {
        var appearanceIds = Enumerable.Repeat(CharacterSummary.EmptySlot, CharacterSummary.EquipmentSlots).ToArray();
        foreach (var item in Equipped.Where(i => i.Slot < CharacterSummary.EquipmentSlots)) appearanceIds[item.Slot] = item.TemplateId;
        var c = Character;
        return new CharacterSummary((uint)c.Id, (uint)c.Id, worldId, c.Name, 0, ClanName,
            Appearance(), appearanceIds, c.Zone, Level, FishingLevel, CookingLevel, c.Class, AttackRange, AttackSpeed, PvpLevel, PvpRating, false);
    }

    public Appearance Appearance()
    {
        var c = Character;
        return new Appearance(c.Race, c.Face, c.Skin, c.SkinColour, c.Hair, c.HairColour, c.FaceAccessory, c.FaceAccessoryColour, c.Scale, c.Gender,
            Progress.ShowHeadgear, Progress.ShowFashion);
    }

    public CharacterStats Stats()
    {
        var (vit, str, foc, dex) = (CombinedVitality, CombinedStrength, CombinedFocus, CombinedDexterity);
        return new CharacterStats
        {
            Health = Health, MaxHealth = MaxHealth, Energy = Energy, MaxEnergy = MaxEnergy,
            Attack = Attack, Defence = Defence, AttackSpeed = AttackSpeed, WeaponDamage = WeaponDamage, ArmourValue = Armour,
            Vitality = vit, Strength = str, Focus = foc, Dexterity = dex, Encumbrance = Encumbrance,
            SkillPoints = SkillPoints, AttributePoints = AttributePoints, Concentration = 50, MaxConcentration = 50,
            AttackRange = AttackRange, FastTravelItemLimit = FastTravelItemLimit, SoloBankItemLimit = BankLimit, MaxSpeed = 5f * (100 + MountSpeedPercent) / 100f, Scale = 1f,
            CompiledAbilities = Abilities(),
            CompiledSkills = UsableSkills.Select(s => new IdValue(s.SkillId, EffectiveRank(s.SkillId, s.Level))).ToList(),
            Avoidances = Gear.SelectMany(g => g.AvoidanceBonuses).GroupBy(a => a.Id).Select(g => new IdValue(g.Key, g.Sum(a => a.Amount))).ToList(),
            Bonuses = GearBonuses(),
        };
    }

    /// <summary>Ability levels: class abilities at 1, plus equipment AbilityBonuses (also for abilities the class lacks).</summary>
    /// <summary>Abilities learnt from trainers, on top of the class's own.</summary>
    public IReadOnlyList<int> LearnedAbilities { get; init; } = [];

    /// <summary>Levels abilities have reached by use (GameServer.Abilities); 1 when absent.</summary>
    public IReadOnlyDictionary<int, int> AbilityLevels { get; init; } = new Dictionary<int, int>();

    /// <summary>The abilities this character has: its class's and those learnt from trainers.</summary>
    public IReadOnlySet<int> KnownAbilities => Kit.Abilities.Concat(LearnedAbilities).ToHashSet();

    /// <summary>An ability's working level: trained level plus equipment AbilityBonuses (0 for abilities the character lacks).</summary>
    public int AbilityLevel(int abilityId) => Abilities().Where(a => a.Id == abilityId).Select(a => a.Value).DefaultIfEmpty(0).First();

    public IReadOnlyList<IdValue> Abilities()
    {
        var levels = Kit.Abilities.Concat(LearnedAbilities).Distinct().ToDictionary(a => a, a => Math.Max(1, AbilityLevels.GetValueOrDefault(a, 1)));
        foreach (var (id, amount) in Gear.SelectMany(g => g.AbilityBonuses)) levels[id] = levels.GetValueOrDefault(id) + amount;
        return levels.Select(kv => new IdValue(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Resist and other BonusTypes for the stats page, as client BonusType ids (see <see cref="BonusTypeIds"/>).</summary>
    public IReadOnlyList<IdValue> GearBonuses() =>
        Gear.SelectMany(g => g.Bonuses).Where(b => BonusTypeIds.ContainsKey(b.Type))
            .GroupBy(b => BonusTypeIds[b.Type]).Select(g => new IdValue(g.Key, g.Sum(b => b.Amount))).ToList();

    /// <summary>Client BonusType enum values by name. Filled from the client enum (Balance.Model BonusType).</summary>
    /// <summary>
    /// Client BonusType values (Balance.Model/BonusType.cs) for the resists shown on the Abilities screen. Attack, defence,
    /// health and energy bonuses (10-13) are folded into those stats instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> BonusTypeIds = new Dictionary<string, int>
    {
        ["PiercingResist"] = 0, ["SlashingResist"] = 1, ["CrushingResist"] = 2, ["HeatResist"] = 3, ["ColdResist"] = 4,
        ["MagicResist"] = 5, ["PoisonResist"] = 6, ["DivineResist"] = 7, ["ChaosResist"] = 8, ["FishingResist"] = 15,
        ["ConcentrationBonus"] = 16,
    };

    /// <summary>XP progress into the current level, 0-100.</summary>
    public float XpPercent => Math.Clamp(100f * Progress.Xp / Progression.XpToNextLevel(Level), 0f, 100f);

    public IReadOnlyList<HudSet> Hud()
    {
        if (!string.IsNullOrEmpty(Progress.Hud))
        {
            var saved = JsonSerializer.Deserialize<List<HudSet>>(Progress.Hud);
            if (saved is { Count: > 0 }) return saved;
        }
        return DefaultHud();
    }

    /// <summary>Starter skills on set 0, a potion in the last slot, set 1 empty. The client needs at least one set.</summary>
    public IReadOnlyList<HudSet> DefaultHud()
    {
        var set0 = Enumerable.Repeat(new IdValue(0, -1), HudSlotsPerSet).ToArray();
        for (var i = 0; i < Math.Min(Skills.Count, HudSlotsPerSet - 1); i++) set0[i] = new IdValue(1, Skills[i].SkillId);
        if (Backpack.FirstOrDefault(i => i.TemplateId == ClassKit.MinorHealthPotion) is { } potion)
            set0[HudSlotsPerSet - 1] = new IdValue(2, (int)potion.Id);
        return [new HudSet(0, set0), new HudSet(1, Enumerable.Repeat(new IdValue(0, -1), HudSlotsPerSet).ToArray())];
    }

    /// <param name="teleports">discovered leystones (PlayerSpawnPoint ids)</param>
    /// <param name="completedQuests">quests the client should treat as done (zone-exit gates; quests aren't implemented)</param>
    /// <summary>
    /// Backpack size above which fast travel needs a Leystone Fragment (client default 20). The private server makes
    /// travel free.
    /// </summary>
    public const int FastTravelItemLimit = 10000;

    /// <summary>Personal bank capacity (stats block soloBankItemLimit). <b>Approximated.</b></summary>
    public const int BankSlots = 100;

    /// <summary>Bank Expansions and Backpack Expansions applied to this character (GameServer.Character).</summary>
    public int BankExpansions { get; init; }

    public int PvpLevel { get; init; }
    public int PvpRating { get; init; } = 4;
    public int PvpProgress { get; init; }
    public int FishingLevel { get; init; } = 1;
    public float FishingXpPercent { get; init; }
    public int CookingLevel { get; init; } = 1;
    public float CookingXpPercent { get; init; }

    /// <summary>Shown in brackets on other players' nameplates.</summary>
    public string ClanName { get; init; } = "";
    public int BackpackExpansions { get; init; }

    /// <summary>Bank capacity: the base plus 10 per Bank Expansion.</summary>
    public int BankLimit => BankSlots + BankExpansions * GameServer.BankSlotsPerExpansion;

    public StartGameReply StartGame(int worldId, IReadOnlyList<int>? teleports = null, IReadOnlyList<int>? completedQuests = null,
        IReadOnlyList<ActiveQuest>? activeQuests = null) => new()
    {
        Platinum = Progress.Platinum,
        Character = Summary(worldId),
        FishingLevel = FishingLevel,
        FishingExperience = FishingXpPercent,
        PvpExperience = PvpProgress,
        CookingLevel = CookingLevel,
        CookingExperience = CookingXpPercent,
        Experience = XpPercent,
        Abilities = Abilities(),
        Skills = SkillEntries(),
        Stats = Stats(),
        Level = Level,
        Coins = Progress.Coins,
        Equipment = EquipmentSlots(),
        Inventory = BackpackSlots(),
        HudSets = Hud(),
        BaseVitality = Vitality,
        BaseStrength = Strength,
        BaseFocus = Focus,
        BaseDexterity = Dexterity,
        X = Character.X,
        Y = Character.Y,
        Z = Character.Z,
        Direction = Character.Direction,
        KnownZones = [Character.Zone],
        Teleports = teleports ?? [],
        CompletedQuests = completedQuests ?? [],
        ActiveQuests = activeQuests ?? [],
    };

    public static string SerializeHud(IReadOnlyList<HudSet> sets) => JsonSerializer.Serialize(sets);
}

/// <summary>Levelling curve. <b>Approximated</b>: the client ships none.</summary>
public static class Progression
{
    public const int MaxLevel = 250;
    public const int AttributePointsPerLevel = 5; // owner's rule (2026-10-14)
    public const int SkillPointsPerLevel = 1;

    public static int XpToNextLevel(int level) => (int)(100 * Math.Pow(Math.Max(1, level), 1.6));
}
