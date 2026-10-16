using CH.Protocol;
using CH.Server.Game;

namespace CH.Server;

/// <summary>
/// Abilities level up through use (docs/decisions.md). The client shows "&lt;ability&gt; Ability increased to N" and the
/// abilities page from AbilityUpdate (13024: varint abilityId, varint newLevel, then the ability list). DECA's gain
/// rules are server-side, so these are <b>approximated</b>: each use may raise the ability one level, up to its cap of
/// ten times the character's level (the client's own cap on the abilities page: 2,000 at level 200).
/// </summary>
public sealed partial class GameServer
{
    private const double AbilityGainChance = 0.35;
    public const int AbilityCapPerLevel = 10;

    /// <summary>Client ability ids (Abilities in the balance data).</summary>
    public const int MeleeCombat = 7, RangedCombat = 8, Fortitude = 19, Warding = 20, Reflex = 21, Vigour = 22, Willpower = 23, CriticalStrike = 29, CriticalSkills = 30, HandToHand = 18,
        Novelty = 17, FishingAbility = 26, CookingProficiency = 38, CookingMastery = 39;

    /// <summary>A weapon's own ability, by item subtype.</summary>
    public static int WeaponAbility(ItemInfo? weapon) => weapon?.Subtype switch
    {
        null or "" or "HandToHand" => HandToHand,
        "Sword" or "SwordTwoHanded" or "BansheeBlade" => 0,
        "Blunt" or "BluntTwoHanded" or "NoveltyBlunt" => 1,
        "Axe" or "AxeTwoHanded" => 2,
        "Staff" => 3,
        "Dagger" => 4,
        "Wand" or "NoveltyWand" => 5,
        "Bow" => 6,
        "Spear" => 15,
        "Totem" => 16,
        "FishingRod" => FishingAbility,
        _ => Novelty,
    };

    private ItemInfo? Weapon(Session s) =>
        s.Sheet?.Equipped.FirstOrDefault(i => i.Slot == (int)EquipSlot.Weapon) is { } w ? _balance.Items.GetValueOrDefault(w.TemplateId) : null;

    /// <summary>
    /// Chance a combat action raises its ability by one (server/content/ability-gain.json; 1% by default). Fishing and
    /// cooking keep their own easing chance.
    /// </summary>
    private double _combatAbilityChance = 0.01;

    private static double LoadCombatAbilityChance(string? path)
    {
        if (path is null || !File.Exists(path)) return 0.01;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.TryGetProperty("combatChance", out var c) ? c.GetDouble() : 0.01;
    }

    /// <summary>Combat training: a flat <see cref="_combatAbilityChance"/> per action, up to the cap.</summary>
    private void TrainCombatAbility(Session s, int abilityId) => TrainAbility(s, abilityId, _combatAbilityChance);

    /// <summary>
    /// Maybe raises an ability the character has, by one level, and tells the client. With <paramref name="flatChance"/>
    /// the chance is that, flat; otherwise it eases off towards the cap (fishing, cooking).
    /// </summary>
    private void TrainAbility(Session s, int abilityId, double? flatChance = null)
    {
        var sheet = s.Sheet;
        if (sheet is null || abilityId < 0 || !sheet.KnownAbilities.Contains(abilityId)) return;
        var current = Math.Max(1, sheet.AbilityLevels.GetValueOrDefault(abilityId, 1));
        // Combat abilities (flat chance) go up to the client's cap of ten per level; fishing and cooking keep their own,
        // easing curve and cap (twice the level + 5, at most 500), left as they were.
        var cap = flatChance is not null ? AbilityCapPerLevel * Math.Max(1, sheet.Level) : Math.Min(500, 2 * sheet.Level + 5);
        if (current >= cap) return;
        var chance = flatChance ?? AbilityGainChance * Math.Max(0.05, 1.0 - (double)current / cap);
        if (Random.Shared.NextDouble() >= chance) return;
        SetAbility(s, abilityId, current + 1);
    }

    /// <summary>
    /// Stores an ability level and tells the client (AbilityUpdate: "&lt;ability&gt; Ability increased to N" and the list),
    /// then resends the stats, since a weapon ability is part of Attack.
    /// </summary>
    private void SetAbility(Session s, int abilityId, int level)
    {
        _store.SetAbilityLevel(s.Character!.Id, abilityId, level);
        var updated = RefreshSheet(s);
        Send(s, Opcode.AbilityUpdate, b =>
        {
            b.WriteVariableInt32(abilityId);
            b.WriteVariableInt32(level);
            var list = updated.TrainedAbilities(); // base levels; the stats that follow carry the gear bonuses
            b.WriteVariableInt32(list.Count);
            foreach (var a in list)
            {
                b.WriteVariableInt32(a.Id);
                b.WriteVariableInt32(a.Value);
            }
        });
        SendLiveStats(s);
    }

    /// <summary>
    /// GM: <c>.ability</c> lists your abilities; <c>.ability &lt;name or id&gt; &lt;level | +n | -n&gt;</c> sets one, past the
    /// training cap, for testing (e.g. <c>.ability bow 2000</c>). Only abilities the character has.
    /// </summary>
    private void AbilityCommand(Session s, string[] args)
    {
        var sheet = Sheet(s);
        string Name(int id) => _balance.AbilityNames.GetValueOrDefault(id, $"ability {id}");
        if (args.Length == 0)
        {
            Tell(s, "Abilities: " + string.Join(", ", sheet.Abilities().OrderBy(a => a.Id).Select(a => $"{Name(a.Id)} ({a.Id}) {a.Value}")));
            return;
        }
        if (args.Length < 2)
        {
            Tell(s, "Usage: .ability <name or id> <level | +n | -n>   e.g. .ability bow 2000, .ability ranged combat +100");
            return;
        }
        var amount = args[^1];
        var query = string.Join(' ', args[..^1]);
        int? id = int.TryParse(query, out var n) ? n
            : _balance.AbilityNames.Where(a => a.Value.Equals(query, StringComparison.OrdinalIgnoreCase)).Select(a => (int?)a.Key).FirstOrDefault()
              ?? _balance.AbilityNames.Where(a => a.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Key).Select(a => (int?)a.Key).FirstOrDefault();
        if (id is not { } abilityId || !sheet.KnownAbilities.Contains(abilityId))
        {
            Tell(s, id is null ? $"No ability called {query}." : $"{s.Name} doesn't have {Name(id.Value)}.");
            return;
        }
        var current = Math.Max(1, sheet.AbilityLevels.GetValueOrDefault(abilityId, 1));
        if (!int.TryParse(amount.TrimStart('+'), out var value))
        {
            Tell(s, $"Not a number: {amount}");
            return;
        }
        var target = Math.Max(1, amount.StartsWith('+') || amount.StartsWith('-') ? current + value : value);
        SetAbility(s, abilityId, target);
        Tell(s, $"{Name(abilityId)}: {current} -> {target}.");
    }

    /// <summary>The caster's level in a skill's ability, for effect strength (at least 1).</summary>
    private float AbilityLevelFor(Session s, SkillInfo skill) => Math.Max(1, skill.AbilityId >= 0 ? s.Sheet!.AbilityLevel(skill.AbilityId) : 1);

}
