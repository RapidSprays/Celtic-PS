using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Quests (docs/quest-notes.md). The client runs NPC conversations itself and asks the server to start quests
/// (QUEST_ADD → 14001) and to complete talk / hand-in stages (STAGE_COMPLETE → 14003). The server detects kill, loot,
/// travel and skill stages, keeps the quest lists the conversations test, and pays rewards (<b>approximated</b>).
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Quest item drop chance from a monster the stage names, and from any monster in the quest's zone when it names none.</summary>
    private const double QuestItemChance = 0.5;
    private const double QuestItemZoneChance = 0.2;

    private sealed class StageState
    {
        public bool Done { get; set; }
        public bool Active { get; set; }
        public int Sum { get; set; }
    }

    private sealed class QuestState
    {
        public required QuestInfo Info { get; init; }
        public bool Completed { get; set; }
        public bool Tracked { get; set; }
        public List<StageState> Stages { get; } = new();

        public QuestRecord ToRecord() => new(Info.Id, Completed, Stages.Select(s => new QuestStageRecord(s.Done, s.Active, s.Sum)).ToList(), Tracked);
        public ActiveQuest ToWire() => new(Info.Id, Stages.Select(s => new QuestStageState(s.Done, s.Active)).ToList());
    }

    private void LoadQuests(Session s)
    {
        s.Quests.Clear();
        foreach (var r in _store.ListQuests(s.Character!.Id))
        {
            if (!TryQuestInfo(r.QuestId, out var info)) continue;
            var q = new QuestState { Info = info, Completed = r.Completed, Tracked = r.Tracked };
            for (var i = 0; i < info.Stages.Count; i++)
            {
                var st = i < r.Stages.Count ? r.Stages[i] : new QuestStageRecord(false, false, 0);
                q.Stages.Add(new StageState { Done = st.Done, Active = st.Active, Sum = st.Sum });
            }
            s.Quests[r.QuestId] = q;
        }
    }

    private void SaveQuest(Session s, QuestState q) => _store.SaveQuest(s.Character!.Id, q.ToRecord());

    private bool QuestDone(Session s, int questId) =>
        _travel.GateQuests.Contains(questId) || (s.Quests.TryGetValue(questId, out var q) && q.Completed);

    /// <summary>Completed ids for the client: real completions plus the zone-gate quests (GameServer.Travel).</summary>
    private List<int> CompletedQuestIds(Session s) =>
        s.Quests.Values.Where(q => q.Completed).Select(q => q.Info.Id).Union(_travel.GateQuests).Order().ToList();

    private List<ActiveQuest> ActiveQuests(Session s) =>
        s.Quests.Values.Where(q => !q.Completed).OrderBy(q => q.Info.Id).Select(q => q.ToWire()).ToList();

    /// <summary>
    /// AvailLists: quests a conversation may offer now (QUEST_AVAILABLE), and ones that will open with more levels
    /// (grey "!"). Only quests with a giver; seasonal event quests stay closed.
    /// </summary>
    private (List<int> Available, List<int> NotYet) QuestLists(Session s)
    {
        var available = new List<int>();
        var notYet = new List<int>();
        var level = s.Sheet?.Level ?? 1;
        foreach (var q in _quests.Quests.Values)
        {
            if (q.Giver is null || q.Seasonal || q.IsBounty) continue; // bounties come from the boards
            if (s.Quests.TryGetValue(q.Id, out var state) && (!state.Completed || !q.Repeatable)) continue;
            if (!q.Prerequisites.All(p => QuestDone(s, p))) continue;
            (level >= q.MinLevel ? available : notYet).Add(q.Id);
        }
        return (available, notYet);
    }

    private void WriteQuestLists(NetBuffer b, Session s)
    {
        var (available, notYet) = QuestLists(s);
        QuestMessages.WriteAvailable(b, available, notYet);
    }

    /// <summary>RequestAccurateQuestData (14008): per-stage counters for the quest log; also refreshes NPC icons.</summary>
    private void SendQuestData(Session s) =>
        Send(s, Opcode.RequestAccurateQuestData, b => QuestMessages.WriteAccurate(b, s.Quests.Values.Where(q => !q.Completed)
            .Select(q => new QuestProgress(q.Info.Id, q.Tracked, q.Stages.Select((st, i) => st.Done ? Math.Max(st.Sum, q.Info.Stages[i].Total) : st.Sum).ToList()))
            .ToList()));

    // ---- client requests ---------------------------------------------------------------------------------------

    private void OnQuestMessage(Session s, Opcode opcode, NetIncomingMessage msg)
    {
        switch (opcode)
        {
            case Opcode.QuestStart:
                StartQuest(s, msg.ReadVariableInt32());
                break;
            case Opcode.QuestStageComplete:
                var questId = msg.ReadVariableInt32();
                OnStageCompleteRequest(s, questId, msg.ReadVariableInt32());
                break;
            case Opcode.RequestAccurateQuestData:
                SendQuestData(s);
                break;
            case Opcode.PreviewQuestRewards:
            {
                var fromLog = msg.ReadBoolean();
                var id = msg.ReadInt32();
                var info = TryQuestInfo(id, out var found) ? found : null;
                Send(s, Opcode.PreviewQuestRewards, b => QuestMessages.WritePreview(b, fromLog, id, info?.RewardXp ?? 0, info?.RewardCoins ?? 0));
                break;
            }
            case Opcode.DeleteQuest:
            {
                var id = msg.ReadInt32();
                if (s.Quests.TryGetValue(id, out var q) && !q.Completed) DropQuest(s, q);
                else
                    Send(s, Opcode.QuestRefreshMessage, b =>
                    {
                        QuestMessages.WriteFullList(b, CompletedQuestIds(s), ActiveQuests(s));
                        WriteQuestLists(b, s);
                    });
                break;
            }
            case Opcode.TrackQuest:
            {
                var id = msg.ReadInt32();
                var tracked = msg.ReadBoolean();
                if (s.Quests.TryGetValue(id, out var q))
                {
                    q.Tracked = tracked;
                    SaveQuest(s, q);
                }
                break;
            }
            case Opcode.TrackAllQuests:
            {
                var tracked = msg.ReadBoolean();
                foreach (var q in s.Quests.Values.Where(q => !q.Completed))
                {
                    q.Tracked = tracked;
                    SaveQuest(s, q);
                }
                break;
            }
        }
    }

    private void StartQuest(Session s, int questId)
    {
        if (!TryQuestInfo(questId, out var info) || !(info.IsBounty ? CanStartBounty(s, info) : QuestLists(s).Available.Contains(questId)))
        {
            Send(s, Opcode.QuestStartReply, b => QuestMessages.WriteRejected(b, "You can't take that quest right now."));
            return;
        }
        var q = new QuestState { Info = info, Tracked = true };
        var initial = info.InitialStages.ToHashSet();
        for (var i = 0; i < info.Stages.Count; i++) q.Stages.Add(new StageState { Active = initial.Contains(i) });
        s.Quests[questId] = q;
        SaveQuest(s, q);
        Send(s, Opcode.QuestStartReply, b =>
        {
            var (a, n) = QuestLists(s);
            QuestMessages.WriteStartReply(b, q.ToWire(), a, n);
        });
        _log.LogInformation("{Name} started quest {Id} ({Quest})", s.Name, questId, info.Name);
        MarkBounty(s, info, BountyStarted);
        foreach (var i in initial) CheckStageAlreadyMet(s, q, i);
        SendQuestData(s);
    }

    /// <summary>STAGE_COMPLETE from a conversation: talk stages, and hand-ins (the server checks and takes the items).</summary>
    private void OnStageCompleteRequest(Session s, int questId, int stage)
    {
        if (!s.Quests.TryGetValue(questId, out var q) || q.Completed || stage < 0 || stage >= q.Stages.Count
            || !q.Stages[stage].Active || q.Stages[stage].Done)
        {
            Send(s, Opcode.QuestStageProgressReply, b => QuestMessages.WriteRejected(b, "That isn't part of your quest right now."));
            return;
        }
        var info = q.Info.Stages[stage];
        var needs = info.Type switch
        {
            StageType.HandInItem or StageType.LootItem => [info.Target],
            StageType.HandInItemList => info.Items.ToList(),
            _ => new List<(int, int)>(),
        };
        if (needs.Count > 0)
        {
            var items = _store.ListItems(s.Character!.Id).Where(i => i.Slot == ItemRecord.Backpack).ToList();
            if (needs.Any(n => items.Where(i => i.TemplateId == n.Item1).Sum(i => i.Quantity) < n.Item2))
            {
                Send(s, Opcode.QuestStageProgressReply, b => QuestMessages.WriteRejected(b, "You don't have everything yet."));
                return;
            }
            foreach (var (template, count) in needs) TakeItems(s.Character!.Id, template, count);
        }
        CompleteStage(s, q, stage, inventoryChanged: needs.Count > 0);
    }

    private void TakeItems(long characterId, int template, int count)
    {
        foreach (var item in _store.ListItems(characterId).Where(i => i.Slot == ItemRecord.Backpack && i.TemplateId == template).ToList())
        {
            if (count <= 0) break;
            var take = Math.Min(count, item.Quantity);
            _store.SetItemQuantity(item.Id, item.Quantity - take);
            count -= take;
        }
    }

    // ---- stage progress and completion ---------------------------------------------------------------------------

    private void CompleteStage(Session s, QuestState q, int stage, bool inventoryChanged = false)
    {
        var st = q.Stages[stage];
        st.Done = true;
        st.Active = false;
        st.Sum = Math.Max(st.Sum, q.Info.Stages[stage].Total);
        var next = q.Info.Stages[stage].Next;
        var activated = -1;
        if (next >= 0 && next < q.Stages.Count && !q.Stages[next].Done && !q.Stages[next].Active
            && q.Info.Predecessors(next).All(p => q.Stages[p].Done))
        {
            q.Stages[next].Active = true;
            activated = next;
        }
        var finished = q.Stages.All(x => x.Done);
        SaveQuest(s, q);
        var inventory = inventoryChanged ? RefreshSheet(s).InventoryWithMoney() : null;
        Send(s, Opcode.QuestStageProgressReply, b => QuestMessages.WriteStageDone(b, q.Info.Id, stage, activated, activated < 0 ? 0 : 1, inventory));
        if (finished) CompleteQuest(s, q);
        else
        {
            if (activated >= 0) CheckStageAlreadyMet(s, q, activated);
            SendQuestData(s);
        }
    }

    private void CompleteQuest(Session s, QuestState q)
    {
        q.Completed = true;
        SaveQuest(s, q);
        var info = q.Info;
        var c = s.Character!;
        _store.AddCurrency(c.Id, info.RewardCoins, 0);
        var before = s.Sheet!.Level;
        var items = new List<(int, int)>();
        // A piece of gear for the quest's level now and then. Approximated: the real rewards are server-only.
        if (Random.Shared.NextDouble() < 0.4)
            foreach (var drop in _loot.Roll(info.Level, 4, (CharacterClass)c.Class, Random.Shared))
            {
                items.Add((drop, 1));
                GrantItem(c.Id, drop, 1);
            }
        var level = AddXp(s, info.RewardXp, out var sheet);
        Send(s, Opcode.QuestCompleteReply, b =>
        {
            var (a, n) = QuestLists(s);
            QuestMessages.WriteComplete(b, info.Id, info.RewardXp, sheet.XpPercent, info.RewardCoins, items, sheet.InventoryWithMoney(), a, n);
        });
        _log.LogInformation("{Name} completed quest {Id} ({Quest})", s.Name, info.Id, info.Name);
        if (level != before) AnnounceLevel(s, level);
        FactionQuestReward(s, info);
        MarkBounty(s, info, BountyHandedIn);
        // Quests waiting on this one (CompletedQuest stages).
        foreach (var other in s.Quests.Values.Where(o => !o.Completed).ToList())
            for (var i = 0; i < other.Stages.Count; i++)
                if (other.Stages[i].Active && !other.Stages[i].Done && other.Info.Stages[i].Type == StageType.CompletedQuest
                    && other.Info.Stages[i].Target.Id == info.Id)
                    CompleteStage(s, other, i);
        SendQuestData(s);
    }

    /// <summary>A stage that's satisfied the moment it opens: items already carried, a quest already done, standing on the spot.</summary>
    private void CheckStageAlreadyMet(Session s, QuestState q, int stage)
    {
        if (q.Completed || !q.Stages[stage].Active || q.Stages[stage].Done) return;
        var info = q.Info.Stages[stage];
        switch (info.Type)
        {
            case StageType.CompletedQuest when QuestDone(s, info.Target.Id):
                CompleteStage(s, q, stage);
                break;
            case StageType.LootItem:
                var held = _store.ListItems(s.Character!.Id).Where(i => i.Slot == ItemRecord.Backpack && i.TemplateId == info.Target.Id).Sum(i => i.Quantity);
                q.Stages[stage].Sum = Math.Min(held, info.Target.Count);
                if (held >= info.Target.Count) CompleteStage(s, q, stage);
                break;
            case StageType.Position when AtStagePosition(s, info):
                CompleteStage(s, q, stage);
                break;
        }
    }

    private static bool AtStagePosition(Session s, QuestStage stage)
    {
        var p = stage.Details.Split(',');
        if (p.Length < 5 || !int.TryParse(p[0], out var zone) || zone != s.Position.Zone) return false;
        var x = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
        var z = float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
        var r = float.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture);
        return Dist2D(s.Position.X, s.Position.Z, x, z) <= r;
    }

    /// <summary>One more toward a counting stage: "N remaining" in chat, and the stage completes at the target.</summary>
    private void Advance(Session s, QuestState q, int stage, int by = 1)
    {
        var info = q.Info.Stages[stage];
        var st = q.Stages[stage];
        st.Sum = Math.Min(info.Target.Count, st.Sum + by);
        if (st.Sum >= info.Target.Count)
        {
            CompleteStage(s, q, stage);
            return;
        }
        SaveQuest(s, q);
        Send(s, Opcode.QuestStageProgressReply, b => QuestMessages.WriteProgress(b, q.Info.Id, stage, (int)info.Type, info.Target.Count - st.Sum, null));
        SendQuestData(s);
    }

    private IEnumerable<(QuestState Quest, int Stage)> OpenStages(Session s, StageType type) =>
        s.Quests.Values.Where(q => !q.Completed).ToList()
            .SelectMany(q => Enumerable.Range(0, q.Stages.Count).Where(i => q.Stages[i].Active && !q.Stages[i].Done && q.Info.Stages[i].Type == type)
                .Select(i => (q, i)));

    /// <summary>
    /// A kill counts for kill and mob-set stages, and may drop items for loot stages. Returns quest items granted
    /// (for the WonCombat loot list).
    /// </summary>
    private List<(int, int)> QuestKill(Session s, Mob mob)
    {
        var granted = new List<(int, int)>();
        FactionKill(s);
        foreach (var (q, i) in OpenStages(s, StageType.MobKill).ToList())
            if (q.Info.Stages[i].Target.Id == mob.Template.Id) Advance(s, q, i);
        foreach (var (q, i) in OpenStages(s, StageType.MobSetKill).ToList())
            if (_balance.MobSets.TryGetValue(q.Info.Stages[i].Target.Id, out var set) && set.Contains(mob.Template.Id)) Advance(s, q, i);
        foreach (var (q, i) in OpenStages(s, StageType.LootItem).ToList())
        {
            var stage = q.Info.Stages[i];
            var chance = stage.Sources.Contains(mob.Template.Id) ? QuestItemChance
                : stage.Sources.Count == 0 && q.Info.Zone == s.Position.Zone ? QuestItemZoneChance : 0;
            if (Random.Shared.NextDouble() >= chance) continue;
            GrantItem(s.Character!.Id, stage.Target.Id, 1);
            granted.Add((stage.Target.Id, 1));
            Advance(s, q, i);
        }
        return granted;
    }

    private void QuestCast(Session s, int skillId, Mob? target)
    {
        foreach (var (q, i) in OpenStages(s, StageType.CastSkill).ToList())
            if (q.Info.Stages[i].Target.Id == skillId) Advance(s, q, i);
        if (target is null) return;
        foreach (var (q, i) in OpenStages(s, StageType.CastSkillOn).ToList())
        {
            var parts = q.Info.Stages[i].Details.Split(';');
            if (q.Info.Stages[i].Target.Id == skillId && parts.Length > 1 && int.TryParse(parts[1], out var mobTemplate) && mobTemplate == target.Template.Id)
                Advance(s, q, i);
        }
    }

    private void QuestMove(Session s)
    {
        foreach (var (q, i) in OpenStages(s, StageType.Position).ToList())
            if (AtStagePosition(s, q.Info.Stages[i])) CompleteStage(s, q, i);
    }

    // ---- NPCs ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The zone's generated NPCs (scripts/gen-world.py): official spawn ids, so the client's quest and shop icons
    /// apply, and the conversation the client will run when they're clicked.
    /// </summary>
    private IEnumerable<Mob> NpcsFor(int zoneId, double now)
    {
        foreach (var n in _quests.Npcs.Where(n => n.Zone == zoneId))
        {
            if (!_balance.Mobs.TryGetValue(n.Template, out var template)) continue;
            var mob = new Mob
            {
                ServerId = (uint)n.SpawnId,
                Template = template,
                Level = 0,
                HomeX = n.X,
                HomeY = n.Y,
                HomeZ = n.Z,
                Direction = n.Direction,
                Opinion = 100,
                ConversationId = n.Conversation,
                AggroRange = 0,
                RoamRadius = 0,
            };
            mob.Reset(now);
            yield return mob;
        }
    }
}
