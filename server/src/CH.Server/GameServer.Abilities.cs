using CH.Protocol;
using CH.Server.Game;

namespace CH.Server;

/// <summary>
/// Abilities level up through use (docs/decisions.md). The client shows "&lt;ability&gt; Ability increased to N" and the
/// abilities page from AbilityUpdate (13024: varint abilityId, varint newLevel, then the ability list). DECA's gain
/// rules are server-side, so these are <b>approximated</b>: each use may raise the ability one level, less likely as it
/// nears its cap of twice the character's level (at most <see cref="MaxAbilityLevel"/>).
/// </summary>
public sealed partial class GameServer
{
    private const double AbilityGainChance = 0.35;
    public const int MaxAbilityLevel = 500;

    /// <summary>Client ability ids (Abilities in the balance data).</summary>
    public const int MeleeCombat = 7, RangedCombat = 8, Fortitude = 19, Reflex = 21, CriticalStrike = 29, CriticalSkills = 30, HandToHand = 18,
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

    /// <summary>Maybe raises an ability the character has, by one level, and tells the client.</summary>
    private void TrainAbility(Session s, int abilityId)
    {
        var sheet = s.Sheet;
        if (sheet is null || abilityId < 0 || !sheet.KnownAbilities.Contains(abilityId)) return;
        var current = Math.Max(1, sheet.AbilityLevels.GetValueOrDefault(abilityId, 1));
        var cap = Math.Min(MaxAbilityLevel, 2 * sheet.Level + 5);
        if (current >= cap) return;
        var chance = AbilityGainChance * Math.Max(0.05, 1.0 - (double)current / cap);
        if (Random.Shared.NextDouble() >= chance) return;
        _store.SetAbilityLevel(s.Character!.Id, abilityId, current + 1);
        var updated = RefreshSheet(s);
        Send(s, Opcode.AbilityUpdate, b =>
        {
            b.WriteVariableInt32(abilityId);
            b.WriteVariableInt32(current + 1);
            var list = updated.Abilities();
            b.WriteVariableInt32(list.Count);
            foreach (var a in list)
            {
                b.WriteVariableInt32(a.Id);
                b.WriteVariableInt32(a.Value);
            }
        });
    }

    /// <summary>The caster's level in a skill's ability, for effect strength (at least 1).</summary>
    private float AbilityLevelFor(Session s, SkillInfo skill) => Math.Max(1, skill.AbilityId >= 0 ? s.Sheet!.AbilityLevel(skill.AbilityId) : 1);

    /// <summary>Skill damage grows with the ability: +0.5% per level above 1. <b>Approximated.</b></summary>
    private double AbilityDamageFactor(Session s, SkillInfo skill) => 1 + (AbilityLevelFor(s, skill) - 1) / 200.0;
}
