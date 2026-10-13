using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Fishing (docs/gathering-notes.md). A fishing spot is a mob of a FishingSpot template (Type 1) with opinion −15:
/// with a rod equipped the client's action button fishes it, which is ordinary combat on the wire. The rod's
/// FishingDamage wears the spot's health down; the spot "bites" back at the player's concentration (PlayerVitalsUpdate),
/// and at zero concentration the fish gets away. Landing it is WonCombat with level type 1: fishing XP and a catch
/// from the zone's fish. Spot positions, health, bites, catches and the XP curve are <b>our own</b> (server-only).
/// </summary>
public sealed partial class GameServer
{
    private const uint FirstFishingSpotId = 60000;
    private const int BaseConcentration = 50, ConcentrationRegen = 10, FishingRespawnSeconds = 20;
    private const string RodSubtype = "FishingRod";

    private readonly IReadOnlyDictionary<int, FishingZone> _fishing;

    private IEnumerable<Mob> FishingSpotsFor(int zoneId, double now)
    {
        if (!_fishing.TryGetValue(zoneId, out var fz) || fz.Templates.Count == 0) yield break;
        var rng = new Random(zoneId);
        for (var i = 0; i < fz.Spots.Count; i++)
        {
            var rare = fz.RareTemplates.Count > 0 && i % 4 == 3;
            var level = fz.MinLevel + (fz.MaxLevel - fz.MinLevel) * i / Math.Max(1, fz.Spots.Count - 1);
            var family = rare ? fz.RareTemplates : fz.Templates;
            var templateId = family[Math.Clamp(level - 1, 0, family.Count - 1)];
            if (!_balance.Mobs.TryGetValue(templateId, out var template)) continue;
            var (x, y, z) = fz.Spots[i];
            var mob = new Mob
            {
                ServerId = FirstFishingSpotId + (uint)(zoneId * 100 + i), Template = template, Level = level, HomeX = x, HomeY = y, HomeZ = z,
                Direction = (float)(rng.NextDouble() * 360), Opinion = -15, AggroRange = 0, RoamRadius = 0, RespawnSeconds = FishingRespawnSeconds,
                RareSpot = rare, FixedMaxHealth = 25 + level * 12,
            };
            mob.Reset(now);
            yield return mob;
        }
    }

    private ItemInfo? Rod(Session s) => s.Sheet!.Gear.FirstOrDefault(g => g.Slot == EquipSlot.Weapon && g.Subtype == RodSubtype);

    private int MaxConcentration(Session s) => BaseConcentration + s.Sheet!.Gear.Sum(g => g.Bonus("ConcentrationBonus"));

    /// <summary>Starting to fish: a rod, and nobody else on this spot. The spot "engages" at once (the client's Fish
    /// skills need it in combat and targeting the angler).</summary>
    private bool StartFishing(Zone zone, Session s, Mob spot, double now)
    {
        if (Rod(s) is null)
        {
            Tell(s, "You must equip a fishing rod to fish here.");
            return false;
        }
        if (spot.Target is Session other && !ReferenceEquals(other, s) && ReferenceEquals(other.AttackTarget, spot))
        {
            Tell(s, "Fishing spot claimed by another!");
            return false;
        }
        s.Concentration = Math.Min(s.Concentration, MaxConcentration(s));
        Engage(zone, spot, s, now);
        return true;
    }

    /// <summary>The spot bites at its angler's concentration; it settles back when they stop or the fish gets away.</summary>
    private void TickFishingSpot(Zone zone, Mob spot, double now)
    {
        if (spot.State is not (MobState.Chasing or MobState.Fighting)) return;
        if (spot.Target is not Session s || !s.InZone || s.Dead || !zone.Players.Contains(s) || !ReferenceEquals(s.AttackTarget, spot))
        {
            ResetSpot(zone, spot);
            return;
        }
        if (!spot.AttackAnnounced)
        {
            spot.AttackAnnounced = true;
            spot.State = MobState.Fighting;
            Broadcast(zone, Opcode.AttackState, new AttackState(true, true, (int)spot.ServerId, false, s.CharacterId).Write);
            spot.NextAttackAt = now + MobStats.AttackInterval(spot.Template);
        }
        if (now < spot.NextAttackAt) return;
        spot.NextAttackAt = now + MobStats.AttackInterval(spot.Template);
        var fishing = s.Sheet!.FishingLevel;
        var bite = 1 + spot.Level / 20 + Math.Max(0, spot.Level - fishing - 10) / 5;
        s.Concentration = Math.Max(0, s.Concentration - bite);
        s.LastFishingAt = now;
        Send(s, Opcode.PlayerVitalsUpdate, b => b.WriteVariableInt32(s.Concentration));
        Send(s, Opcode.CombatDamageMessage, new CombatDamage([new DamageEntry(DamageDirection.MobToPlayer, (int)spot.ServerId, s.CharacterId,
            AttackKind.Attack, 0, bite, s.Health, Reaction.Hit, ++_damageId, false)], []).Write);
        if (s.Concentration > 0) return;
        SetAttackTarget(s, null);
        ResetSpot(zone, spot);
        Tell(s, "You lost your concentration and the fish got away.");
    }

    private void ResetSpot(Zone zone, Mob spot)
    {
        if (spot.AttackAnnounced && spot.Target is Session t)
            Broadcast(zone, Opcode.AttackState, new AttackState(false, true, (int)spot.ServerId, false, t.CharacterId).Write);
        spot.Target = null;
        spot.AttackAnnounced = false;
        spot.State = MobState.Idle;
        spot.Health = spot.MaxHealth;
        spot.Damage.Clear();
        spot.Owner = null;
        Broadcast(zone, Opcode.EntityCombatStateChanged, new CombatStateChanged([(1, (int)spot.ServerId, false)]).Write);
        Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([], [new HealthUpdate(HealthUpdate.Mob, (int)spot.ServerId, spot.Health)]).Write);
    }

    /// <summary>A rod swing or Fish skill: the rod's FishingDamage plus half the fishing level, halved well above one's level.</summary>
    private void FishHit(Zone zone, Session s, Mob spot, AttackKind kind, int skillId, double now)
    {
        if (Rod(s) is not { } rod || !ReferenceEquals(spot.Target, s))
        {
            SetAttackTarget(s, null);
            return;
        }
        var fishing = s.Sheet!.FishingLevel;
        var skillFactor = 1.0;
        if (skillId > 0 && _balance.Skills.TryGetValue(skillId, out var skill))
        {
            var known = s.Sheet.UsableSkills.FirstOrDefault(k => k.SkillId == skillId);
            skillFactor = 1 + Math.Abs(skill.Level(known.Level).BaseDamage) / 10;
        }
        var damage = (rod.Damage + fishing / 2.0) * skillFactor * (0.85 + Random.Shared.NextDouble() * 0.3);
        if (spot.Level > fishing + 15) damage /= 2;
        var dealt = Math.Max(1, (int)damage);
        spot.Health -= dealt;
        TrainAbility(s, FishingAbility);
        s.LastFishingAt = now;
        var entry = new DamageEntry(DamageDirection.PlayerToMob, s.CharacterId, (int)spot.ServerId, kind, skillId, dealt, Math.Max(0, spot.Health), Reaction.Hit, ++_damageId, false);
        Broadcast(zone, Opcode.CombatDamageMessage, new CombatDamage([entry], [new HealthUpdate(HealthUpdate.Mob, (int)spot.ServerId, Math.Max(0, spot.Health))]).Write);
        if (spot.Health > 0) return;
        spot.Owner = s;
        KillMob(zone, spot, s, now);
    }

    /// <summary>The catch: fishing XP (level type 1) and one item from the zone's waters.</summary>
    private void FishingReward(Session s, Mob spot)
    {
        var c = s.Character!;
        var catchId = RollCatch(s.Position.Zone, spot);
        var loot = new List<(int, int)>();
        if (catchId is { } fish)
        {
            GrantItem(c.Id, fish, 1);
            loot.Add((fish, 1));
            QuestGathered(s, fish, 1);
        }
        var (level, xp, _, _) = _store.GetCrafts(c.Id);
        var gained = (10 + 3 * spot.Level) * (100 + Math.Max(0, Modifiers(s).XpBoostPercent)) / 100;
        var (newLevel, newXp) = Crafting.Add(level, xp, gained);
        _store.SaveFishing(c.Id, newLevel, newXp);
        var sheet = RefreshSheet(s);
        Send(s, Opcode.WonCombat, new WonCombat(spot.Template.Id, (int)spot.ServerId, s.CharacterId, gained, sheet.FishingXpPercent, 0, loot, sheet.InventoryWithMoney())
        {
            LevelType = 1,
        }.Write);
        if (newLevel > level)
        {
            var (available, notYet) = QuestLists(s);
            Send(s, Opcode.LevelUp, new LevelUp(newLevel) { LevelType = 1, AvailableQuests = available, NotYetAvailableQuests = notYet }.Write);
            _log.LogInformation("{Name} is now fishing level {Level}", s.Name, newLevel);
        }
    }

    private int? RollCatch(int zoneId, Mob spot)
    {
        if (!_fishing.TryGetValue(zoneId, out var fz)) return null;
        var roll = Random.Shared.NextDouble();
        var rare = spot.RareSpot ? 0.25 : 0.06;
        IReadOnlyList<int> pool = roll < rare ? fz.Rare : roll < rare + 0.04 ? fz.Relics : roll < rare + 0.11 ? fz.Junk : roll < rare + 0.14 ? fz.Clams : fz.Common;
        if (pool.Count == 0) pool = fz.Common.Count > 0 ? fz.Common : fz.Junk;
        return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
    }

    /// <summary>Items gathered outside combat loot (fish, ground pickups) count for the quest stages that ask for them.</summary>
    private void QuestGathered(Session s, int templateId, int quantity)
    {
        foreach (var (q, i) in OpenStages(s, StageType.LootItem).ToList())
            if (q.Info.Stages[i].Target.Id == templateId) Advance(s, q, i, quantity);
    }

    /// <summary>Concentration comes back while not fishing.</summary>
    private void RegenConcentration(Session s, double now)
    {
        var max = MaxConcentration(s);
        if (s.Concentration >= max || now - s.LastFishingAt < 5) return;
        s.Concentration = Math.Min(max, s.Concentration + ConcentrationRegen);
        Send(s, Opcode.PlayerVitalsUpdate, b => b.WriteVariableInt32(s.Concentration));
    }
}
