using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Consumables: every usable item links to a skill template (ItemTemplate.SkillLinkID, a SkillType), and the
/// skill's own data says what it does: damage an enemy (scrolls, bombs, snowballs), heal (negative damage), restore
/// energy (negative energy cost), or apply a status effect (potions, elixirs, food, flights), at the item's
/// SkillLinkLevel; anything else is cosmetic (fireworks, cakes) and the client shows it. Chests, skill books, pet food and the Mount
/// Whistle are handled before this. Recharge comes from the skill; items with MaxCharges −1 never run out.
/// </summary>
public sealed partial class GameServer
{
    private const int ResetSkillsSkill = 10000, ResetStatsSkill = 10001;
    private const float ConsumableRechargeSeconds = 3f;

    /// <summary>Item-shop style consumables with no cooldown: elixirs (and the elixir skills), flights. Chests never had one.</summary>
    private static bool IsPremium(ItemInfo item, SkillInfo skill) =>
        item.Name.Contains("Elixir", StringComparison.OrdinalIgnoreCase) || skill.Name.Contains("Elixir", StringComparison.OrdinalIgnoreCase)
        || item.Name.Contains("Flight of", StringComparison.OrdinalIgnoreCase);

    private void UseSkillItem(Session s, ItemRecord item, ItemInfo info, UseItemRequest req)
    {
        void Reject(string why) => Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Rejected, why, 0, null));
        if (s.Dead)
        {
            Reject("You are dead.");
            return;
        }
        if (!_balance.Skills.TryGetValue(info.SkillLinkId, out var skill))
        {
            Reject("Nothing happens.");
            return;
        }
        var now = NetTime.Now;
        if (s.Cooldowns.TryGetValue(skill.Id, out var readyAt) && now < readyAt - 0.25)
        {
            Reject("That isn't ready yet.");
            return;
        }
        var levelIndex = Math.Max(0, info.SkillLinkLevel);
        var level = skill.Level(levelIndex);
        var amount = (int)Math.Round(level.InitialDamage + level.BaseDamage);
        // A negative energy cost restores that much energy (Gruel −20 "restores 20 energy", Super Energy Potion −2000).
        var energyGain = level.EnergyCost < -1 ? -level.EnergyCost : 0;
        string? problem = null;
        var health = s.Health;
        var energy = s.Energy;
        switch (skill.Id)
        {
            case ResetStatsSkill:
                _store.SaveAttributes(s.Character!.Id, -1, -1, -1, -1);
                Tell(s, "Your attribute points have been refunded.");
                break;
            case ResetSkillsSkill:
                foreach (var (skillId, _) in _store.ListSkills(s.Character!.Id)) _store.SetSkillLevel(s.Character.Id, skillId, 0);
                Tell(s, "Your skill points have been refunded.");
                break;
            default:
                problem = skill.CastTarget == "Enemy" ? UseOnEnemy(s, skill, levelIndex, req, now) : UseOnFriend(s, skill, levelIndex, amount, energyGain, req, now);
                break;
        }
        if (problem is not null)
        {
            Reject(problem);
            return;
        }

        if (info.MaxCharges != -1) _store.SetItemQuantity(item.Id, item.Quantity - 1);
        // Owner's rule: consumables recharge in 3 s; premium items (elixirs, flights) have no cooldown at all.
        var recharge = IsPremium(info, skill) ? 0f : ConsumableRechargeSeconds;
        if (recharge > 0) s.Cooldowns[skill.Id] = now + recharge;
        else s.Cooldowns.Remove(skill.Id);
        var sheet = RefreshSheet(s);
        Send(s, Opcode.UseItemReply, b => InventoryReplies.WriteUseItem(b, CommandStatus.Accepted, "", recharge, sheet.InventoryWithMoney()));
        if (skill.Id is ResetStatsSkill or ResetSkillsSkill) SendProgress(s);
        if (s.Health != health || s.Energy != energy)
        {
            _store.SaveVitals(s.Character!.Id, s.Health, s.Energy);
            Send(s, Opcode.StatsUpdate, new StatsDelta([(StatKey.Health, s.Health), (StatKey.Energy, s.Energy)]).Write);
            if (_zones.TryGetValue(s.Position.Zone, out var zone)) ShareHealth(zone, s);
        }
        _log.LogInformation("{Name} used {Item}", s.Name, info.Name);
    }

    /// <summary>Scrolls, bombs, snowballs: the skill's damage (and effect, e.g. Root) on the targeted monster.</summary>
    private string? UseOnEnemy(Session s, SkillInfo skill, int levelIndex, UseItemRequest req, double now)
    {
        if (req.TargetType != TargetType.Mob || !_zones.TryGetValue(s.Position.Zone, out var zone)
            || !zone.Mobs.TryGetValue(req.TargetId, out var mob) || mob.IsNpc || mob.State == MobState.Dead)
            return "You need to target an enemy.";
        if (!InReach(s, mob, Math.Max(skill.CastRange, 2f) + MeleeTolerance + 1f, now)) return "Your target is too far away.";
        var level = skill.Level(levelIndex);
        var damage = (int)Math.Round(Math.Abs(level.InitialDamage) + Math.Abs(level.BaseDamage));
        // Banes, spirit keys, moth nets…: quest objects this server doesn't have yet. Not used up.
        if (damage == 0 && skill.StatusEffectId <= 0) return "That only works on something special.";
        if (damage > 0) PlayerHits(zone, s, mob, AttackKind.Skill, skill.Id, damage, now, allowMiss: false);
        if (skill.StatusEffectId > 0 && mob.State != MobState.Dead) ApplyMobEffect(mob, s, skill, levelIndex, now);
        if (mob.State is MobState.Idle or MobState.Returning) Engage(zone, mob, s, now);
        return null;
    }

    /// <summary>Potions, elixirs, food, flights, mushrooms: heal (negative damage), harm (positive, on yourself), or an effect.</summary>
    private string? UseOnFriend(Session s, SkillInfo skill, int levelIndex, int amount, int energyGain, UseItemRequest req, double now)
    {
        var target = s;
        if (skill.CastTarget == "Friendly" && req.TargetType == TargetType.OtherPlayer && OnlineByCharacter((int)req.TargetId) is { InZone: true } friend
            && friend.Position.Zone == s.Position.Zone && Dist2D(friend.Position.X, friend.Position.Z, s.Position.X, s.Position.Z) <= Math.Max(skill.CastRange, 10f) + 2)
            target = friend;
        if (amount < 0)
        {
            target.Health = Math.Min(target.MaxHealth, target.Health - amount);
        }
        else if (amount > 0 && skill.CastTarget == "Self")
        {
            // Poisonous mushrooms and the like hurt; they leave you on 1 HP rather than killing you outright.
            target.Health = Math.Max(1, target.Health - amount);
            Tell(s, "You feel unwell.");
        }
        if (energyGain > 0)
        {
            target.Energy = Math.Min(target.MaxEnergy, target.Energy + energyGain);
        }
        if (skill.StatusEffectId > 0)
        {
            ApplyEffect(target, skill, levelIndex, now);
        }
        if (!ReferenceEquals(target, s) && (amount != 0 || energyGain > 0))
            Send(target, Opcode.StatsUpdate, new StatsDelta([(StatKey.Health, target.Health), (StatKey.Energy, target.Energy)]).Write);
        // Fireworks, cakes, balloons, beacons, crackers, ale: the client plays the item's visual effect itself.
        return null;
    }
}
