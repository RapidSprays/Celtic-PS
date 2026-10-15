using CH.Protocol;
using CH.Protocol.Messages;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Levelling: attribute points (AttributesUpdate 15016 → BaseStats 15014 + stats) and skill points (BuySkill 16010 →
/// BuySkillReply 16011). Points come from <see cref="Game.Progression"/>; see CharacterSheet for how they're counted.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>
    /// AttributesUpdate (writer NetworkManager.cs:2358): varuint +str, +dex, +focus, +vit, points remaining. The client
    /// sends what the player added on the stats page; the server checks it against the points actually available.
    /// </summary>
    private void OnAttributesUpdate(Session s, uint addStr, uint addDex, uint addFoc, uint addVit)
    {
        var sheet = s.Sheet!;
        var total = (long)addStr + addDex + addFoc + addVit;
        if (total <= 0 || total > sheet.AttributePoints)
        {
            Tell(s, $"You have {sheet.AttributePoints} attribute points to spend.");
            SendProgress(s);
            return;
        }
        _store.SaveAttributes(s.Character!.Id, sheet.Strength + (int)addStr, sheet.Dexterity + (int)addDex, sheet.Focus + (int)addFoc, sheet.Vitality + (int)addVit);
        RefreshSheet(s);
        SendProgress(s);
    }

    /// <summary>BuySkill (writer NetworkManager.cs:2466): skill id and the rank being bought (one above the current one).</summary>
    /// <summary>
    /// BuySkill (16010): <c>vi skillId, vi newRank</c>. Several ranks at once (the client's +/− and number field, or the
    /// Max button, which asks for a very high rank): the server raises the skill as far as the requested rank, the
    /// level's rank cap and the free skill points (one per rank) allow.
    /// </summary>
    private void OnBuySkill(Session s, int skillId, int newRank)
    {
        var sheet = s.Sheet!;
        var known = sheet.Skills.FirstOrDefault(k => k.SkillId == skillId);
        var target = known == default ? 0 : Math.Min(Math.Min(newRank, sheet.MaxRank(skillId)), known.Level + sheet.SkillPoints);
        string? problem = known == default ? "You don't know that skill."
            : newRank <= known.Level ? "That skill is already at that rank."
            : known.Level >= sheet.MaxRank(skillId) ? $"That skill is at its highest trainable rank ({Game.SkillLevels.TrainableRanks}); gear can raise it further."
            : sheet.SkillPoints <= 0 ? "You have no skill points."
            : target <= known.Level ? "You need a higher level to train that skill further."
            : null;
        if (problem is null)
        {
            _store.SetSkillLevel(s.Character!.Id, skillId, target);
            sheet = RefreshSheet(s);
            _log.LogInformation("{Name} raised skill {Skill} by {Ranks} to rank {Rank}", s.Name, skillId, target - known.Level, target + 1);
        }
        else
        {
            Tell(s, problem);
        }
        // -1: no "new skill learned" popup for a rank-up (ProcessBuySkillReplyMessage).
        Send(s, Opcode.BuySkillReply, b => InventoryReplies.WriteBuySkill(b, sheet.SkillPoints, -1, sheet.SkillEntries()));
        SendLiveStats(s);
    }

    /// <summary>Everything the client shows about progression: base attributes, full stats (with points) and skills.</summary>
    /// <summary>
    /// LearnAbility (16005, a trainer's LEARN_ABILITY action): <c>vi abilityId, vi coins</c>; the price comes from the
    /// conversation. Reply 16006: <c>byte status; ok: vi abilityId, vi newCoins, AvailLists</c>.
    /// </summary>
    private void OnLearnAbility(Session s, int abilityId, int price)
    {
        var sheet = s.Sheet!;
        var coins = _store.GetProgress(s.Character!.Id).Coins;
        string? problem = abilityId < 0 || price < 0 ? "The trainer can't teach that."
            : sheet.Kit.Abilities.Contains(abilityId) || sheet.LearnedAbilities.Contains(abilityId) ? "You already know that."
            : price > coins ? "You don't have enough gold."
            : null;
        if (problem is not null)
        {
            Send(s, Opcode.LearnAbilityReply, b => { b.Write((byte)0); b.Write(problem); });
            Tell(s, problem);
            return;
        }
        _store.AddCurrency(s.Character.Id, -price, 0);
        _store.LearnAbility(s.Character.Id, abilityId);
        RefreshSheet(s);
        Send(s, Opcode.LearnAbilityReply, b =>
        {
            b.Write((byte)1);
            b.WriteVariableInt32(abilityId);
            b.WriteVariableInt32(coins - price);
            WriteQuestLists(b, s);
        });
        SendLiveStats(s); // the stats block carries the ability list
    }

    private void SendProgress(Session s)
    {
        var sheet = s.Sheet!;
        Send(s, Opcode.BaseStats, b =>
        {
            b.WriteVariableInt32(sheet.Vitality);
            b.WriteVariableInt32(sheet.Strength);
            b.WriteVariableInt32(sheet.Focus);
            b.WriteVariableInt32(sheet.Dexterity);
        });
        SendLiveStats(s);
        Send(s, Opcode.SkillListUpdate, new SkillListUpdate(sheet.SkillPoints, sheet.SkillEntries()).Write);
        SendSkillToggles(s);
    }
}
