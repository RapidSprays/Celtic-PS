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

        /// <summary>The day it was last completed (yyyy-MM-dd): a daily opens again the next day.</summary>
        public string? CompletedOn { get; set; }

        public QuestRecord ToRecord() => new(Info.Id, Completed, Stages.Select(s => new QuestStageRecord(s.Done, s.Active, s.Sum)).ToList(), Tracked, CompletedOn);
        public ActiveQuest ToWire() => new(Info.Id, Stages.Select(s => new QuestStageState(s.Done, s.Active)).ToList());
    }

    private void LoadQuests(Session s)
    {
        s.Quests.Clear();
        foreach (var r in _store.ListQuests(s.Character!.Id))
        {
            if (!TryQuestInfo(r.QuestId, out var info)) continue;
            var q = new QuestState { Info = info, Completed = r.Completed, Tracked = r.Tracked, CompletedOn = r.CompletedOn };
            for (var i = 0; i < info.Stages.Count; i++)
            {
                var st = i < r.Stages.Count ? r.Stages[i] : new QuestStageRecord(false, false, 0);
                q.Stages.Add(new StageState { Done = st.Done, Active = st.Active, Sum = st.Sum });
            }
            s.Quests[r.QuestId] = q;
        }
    }

    /// <summary>
    /// At log-in: drops quests taken that can't be held now, as long as nothing in them is done yet (they can be taken
    /// again later): below the quest's level or without its items (quest-requirements.json), or while a quest whose
    /// turn-in it would hide at the NPC is in progress (quest-exclusions.json). Keeps quests from getting stuck.
    /// </summary>
    private void DropBlockingQuests(Session s, int level)
    {
        HashSet<int>? owned = null;
        ISet<int> Owned() => owned ??= _store.ListItems(s.Character!.Id).Select(i => i.TemplateId).ToHashSet();
        foreach (var q in s.Quests.Values.Where(q => !q.Completed && !q.Info.IsBounty && q.Stages.All(st => !st.Done)).ToList())
        {
            var blocks = _questRequirements.NotWhileActive(q.Info.Id).Any(o => o != q.Info.Id && s.Quests.TryGetValue(o, out var other) && !other.Completed);
            if (!blocks && level >= q.Info.MinLevel && _questRequirements.ItemsMet(q.Info.Id, Owned) && ForClass(q.Info, s.Character!.Class)) continue;
            s.Quests.Remove(q.Info.Id);
            _store.DeleteQuest(s.Character!.Id, q.Info.Id);
            _log.LogInformation("{Name}: dropped quest {Id} ({Quest}), not started and {Why}", s.Name, q.Info.Id, q.Info.Name,
                blocks ? "hiding another quest's turn-in" : "not available yet");
        }
    }

    /// <summary>
    /// Whether a quest is for this class (owner's report, 2026-10-18: other classes were offered the warrior's Meteoric
    /// quests). A quest whose reward is gear only some classes can wear (quest-rewards.json items without a "class", or
    /// the gear it's named after: Astral Gauntlets, Frozen Meteoric Sword, Dragonlord Helm…) is offered only to those
    /// classes. Quests with a piece for every class (the Warden armour) and quests without gear are for everyone.
    /// </summary>
    private bool ForClass(QuestInfo q, int characterClass)
    {
        if (!_questClasses.TryGetValue(q.Id, out var classes))
        {
            var items = _questRewards.Listed(q.Id) is { } listed
                ? (listed.All(i => i.Class is null) ? listed.Select(i => i.Item).ToList() : [])
                : NamesakeReward(q.Name) is { } namesake ? [namesake] : [];
            var gear = items.Select(i => _balance.Items.GetValueOrDefault(i)).ToList();
            classes = gear.Count > 0 && gear.All(g => g is { Classes.Count: > 0 })
                ? gear.SelectMany(g => g!.Classes).Select(c => Enum.TryParse<CharacterClass>(c, true, out var cc) ? (int)cc : -1).ToHashSet()
                : null;
            _questClasses[q.Id] = classes;
        }
        return classes is null || classes.Contains(characterClass);
    }

    private readonly Dictionary<int, HashSet<int>?> _questClasses = new();

    private void SaveQuest(Session s, QuestState q) => _store.SaveQuest(s.Character!.Id, q.ToRecord());

    private bool QuestDone(Session s, int questId) =>
        _travel.GateQuests.Contains(questId) || _questRequirements.AlwaysComplete.Contains(questId) || (s.Quests.TryGetValue(questId, out var q) && q.Completed);

    /// <summary>Completed ids for the client: real completions plus the zone-gate quests (GameServer.Travel).</summary>
    private List<int> CompletedQuestIds(Session s) =>
        s.Quests.Values.Where(q => q.Completed && !IsDaily(q.Info)).Select(q => q.Info.Id).Union(_travel.GateQuests)
            .Union(_questRequirements.AlwaysComplete).Order().ToList(); // quest-requirements.json alwaysComplete

    /// <summary>
    /// Daily quests (RepeatArchetype DayRepeat: Segorax's gladiator spars…) can be done once a day each. The client is
    /// never told they're completed: its conversations would stop at "you've done today's" and hide the other dailies
    /// (owner's report, 2026-10-20); the server keeps one done today out of the offered list instead.
    /// </summary>
    private static bool IsDaily(QuestInfo q) => q.Archetype == "DayRepeat";

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>
    /// A quest's reward XP from quest-requirements.json: "xp" (the same at every level), else xpPercentOfLevel of the
    /// player's current level, else the quest's own.
    /// </summary>
    private int QuestXp(Session s, QuestInfo q) =>
        _questRequirements.Xp(q.Id) is { } flat ? flat
        : _questRequirements.XpPercent(q.Id) is { } pct && s.Sheet is { } sheet
            ? Math.Max(1, (int)(Progression.XpToNextLevel(Math.Max(1, sheet.Level)) * pct / 100))
            : q.RewardXp;

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
        HashSet<int>? owned = null; // read once, only when a quest needs an item (quest-requirements.json)
        ISet<int> Owned() => owned ??= s.Character is { } ch ? _store.ListItems(ch.Id).Select(i => i.TemplateId).ToHashSet() : [];
        foreach (var q in _quests.Quests.Values)
        {
            if (q.Giver is null || q.Seasonal || q.IsBounty) continue; // bounties come from the boards
            if (_questRequirements.AlwaysComplete.Contains(q.Id)) continue; // counts as done for everyone
            if (s.Quests.TryGetValue(q.Id, out var state) && (!state.Completed || !q.Repeatable)) continue;
            if (state is not null && IsDaily(q) && state.CompletedOn == Today) continue; // done today: back tomorrow
            if (!q.Prerequisites.All(p => QuestDone(s, p))) continue;
            if (!_questRequirements.ItemsMet(q.Id, Owned)) continue;
            if (!ForClass(q, s.Character?.Class ?? 0)) continue;
            if (_questRequirements.NotWhileActive(q.Id).Any(o => s.Quests.TryGetValue(o, out var other) && !other.Completed)) continue;
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
                Send(s, Opcode.PreviewQuestRewards, b => QuestMessages.WritePreview(b, fromLog, id, info is null ? 0 : QuestXp(s, info), info?.RewardCoins ?? 0));
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
            var items = _store.ListItems(s.Character!.Id).Where(HandInSlot).ToList();
            if (needs.Any(n => items.Where(i => i.TemplateId == n.Item1).Sum(i => i.Quantity) < n.Item2))
            {
                Send(s, Opcode.QuestStageProgressReply, b => QuestMessages.WriteRejected(b, "You don't have everything yet."));
                return;
            }
            // A "collect" stage a conversation completes (Severina's Focus of Enchantment) only checks the items: they're
            // handed in later. Hand-in stages take them.
            var worn = false;
            if (info.Type != StageType.LootItem)
                foreach (var (template, count) in needs) worn |= TakeItems(s.Character!.Id, template, count, includeWorn: true);
            if (worn)
            {
                RefreshSheet(s);
                SendLiveStats(s);
                BroadcastAppearance(s); // the weapon leaves their hand
            }
        }
        CompleteStage(s, q, stage, inventoryChanged: needs.Count > 0 && info.Type != StageType.LootItem);
    }

    /// <summary>
    /// Takes handed-in items: from the backpack first, then what's equipped (owner's report, 2026-10-20: the rogue's
    /// worn Dagger of the Darkest Night for Meteoric Might; the client counts worn items too). Returns whether any
    /// equipped item went. Only quest hand-ins take worn items (<paramref name="includeWorn"/>); shops and crafting don't.
    /// </summary>
    private bool TakeItems(long characterId, int template, int count, bool includeWorn = false)
    {
        var tookEquipped = false;
        foreach (var item in _store.ListItems(characterId).Where(i => (includeWorn ? HandInSlot(i) : i.Slot == ItemRecord.Backpack) && i.TemplateId == template)
                     .OrderBy(i => i.Slot == ItemRecord.Backpack ? 0 : 1).ToList())
        {
            if (count <= 0) break;
            var take = Math.Min(count, item.Quantity);
            _store.SetItemQuantity(item.Id, item.Quantity - take);
            tookEquipped |= item.Slot >= 0;
            count -= take;
        }
        return tookEquipped;
    }

    /// <summary>Where hand-in items count from: the backpack and what's worn (not the bank).</summary>
    private static bool HandInSlot(ItemRecord i) => i.Slot == ItemRecord.Backpack || i.Slot >= 0;

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
        q.CompletedOn = Today;
        SaveQuest(s, q);
        var info = q.Info;
        var c = s.Character!;
        _store.AddCurrency(c.Id, info.RewardCoins, 0);
        var before = s.Sheet!.Level;
        var items = new List<(int, int)>();
        // The owner's items for this quest (quest-rewards.json), else the gear it's named after; nothing otherwise.
        if (_questRewards.For(info.Id, (CharacterClass)c.Class) is { } own)
            foreach (var (item, quantity) in own.Where(i => _balance.Items.ContainsKey(i.Item)))
            {
                items.Add((item, quantity));
                GrantItem(c.Id, item, quantity);
            }
        else if (NamesakeReward(info.Name) is { } namesake)
        {
            // A quest named after a piece of gear gives it (the Warden armour's Astral Gauntlets, the Dragonlord set, the
            // seasonal hats…), as on live.
            items.Add((namesake, 1));
            GrantItem(c.Id, namesake, 1);
        }
        // Owner's rule (2026-10-20): no random gear for other quests; they give only their XP and gold.
        var questXp = QuestXp(s, info);
        var level = AddXp(s, questXp, out var sheet);
        Send(s, Opcode.QuestCompleteReply, b =>
        {
            var (a, n) = QuestLists(s);
            QuestMessages.WriteComplete(b, info.Id, questXp, sheet.XpPercent, info.RewardCoins, items, sheet.InventoryWithMoney(), a, n);
        });
        _log.LogInformation("{Name} completed quest {Id} ({Quest})", s.Name, info.Id, info.Name);
        if (level != before) AnnounceLevel(s, level);
        FactionQuestReward(s, info);
        _store.AddStat(c.Id, "quests", 1);
        MarkBounty(s, info, BountyHandedIn);
        // Quests waiting on this one (CompletedQuest stages).
        foreach (var other in s.Quests.Values.Where(o => !o.Completed).ToList())
            for (var i = 0; i < other.Stages.Count; i++)
                if (other.Stages[i].Active && !other.Stages[i].Done && other.Info.Stages[i].Type == StageType.CompletedQuest
                    && other.Info.Stages[i].Target.Id == info.Id)
                    CompleteStage(s, other, i);
        SendQuestData(s);
    }

    /// <summary>
    /// The piece of gear a quest is named after ("Astral Gauntlets" → item 17772): the equippable item with exactly that
    /// name (the lowest id when there are a few copies), or null.
    /// </summary>
    private int? NamesakeReward(string questName)
    {
        _namesakes ??= _balance.Items.Values.Where(i => i.Slot >= EquipSlot.Weapon && !i.IsPlaceholder)
            .GroupBy(i => i.Name).Where(g => g.Count() <= 3).ToDictionary(g => g.Key, g => g.Min(i => i.Id));
        // The test copies ("CT Astral Helm") and old versions ("Spiritshadow Helm OLD") give the same piece.
        var name = questName.Trim();
        if (name.StartsWith("CT ", StringComparison.Ordinal)) name = name[3..];
        if (name.EndsWith(" OLD", StringComparison.Ordinal)) name = name[..^4];
        return _namesakes.TryGetValue(name, out var id) ? id : null;
    }

    private Dictionary<string, int>? _namesakes;

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

    /// <summary>
    /// Recounts every open "collect" stage (LootItem) from the backpack, so items that arrive any way count: a trade, mail,
    /// a shop or token vendor, a chest, normal loot, the auction house (the owner's report, 2026-10-17: a Sun Disc traded
    /// to his warrior didn't count). Runs after every sheet refresh; never lowers a count, and doesn't run inside itself.
    /// </summary>
    private void RecountQuestItems(Session s)
    {
        if (s.RecountingQuestItems || s.Character is null || s.Quests.Count == 0) return;
        var backpack = _store.ListItems(s.Character.Id).Where(i => i.Slot == ItemRecord.Backpack).ToList();
        int Held(int template) => backpack.Where(it => it.TemplateId == template).Sum(it => it.Quantity);
        // A collect stage counts what's in the bags, as on live: one that's done but whose items have gone (handed in to
        // another Warden sharing the fragments, traded, sold) opens again until the turn-in after it has happened.
        var reopened = false;
        foreach (var q in s.Quests.Values.Where(q => !q.Completed))
            for (var i = 0; i < q.Stages.Count; i++)
            {
                var info = q.Info.Stages[i];
                if (info.Type != StageType.LootItem || !q.Stages[i].Done || Held(info.Target.Id) >= info.Target.Count || LaterStageDone(q, i)) continue;
                q.Stages[i].Done = false;
                q.Stages[i].Active = true;
                q.Stages[i].Sum = Math.Min(Held(info.Target.Id), info.Target.Count);
                for (var n = info.Next; n >= 0 && n < q.Stages.Count && !q.Stages[n].Done; n = q.Info.Stages[n].Next)
                    q.Stages[n].Active = false; // the "return to …" talk waits for the items again
                SaveQuest(s, q);
                reopened = true;
            }
        if (reopened)
        {
            Send(s, Opcode.QuestRefreshMessage, b =>
            {
                QuestMessages.WriteFullList(b, CompletedQuestIds(s), ActiveQuests(s));
                WriteQuestLists(b, s);
            });
            SendQuestData(s);
        }
        var stages = OpenStages(s, StageType.LootItem).ToList();
        if (stages.Count == 0) return;
        s.RecountingQuestItems = true;
        try
        {
            foreach (var (q, i) in stages)
            {
                if (q.Completed || q.Stages[i].Done || !q.Stages[i].Active) continue;
                var target = q.Info.Stages[i].Target;
                var held = Math.Min(target.Count, backpack.Where(it => it.TemplateId == target.Id).Sum(it => it.Quantity));
                if (held > q.Stages[i].Sum) Advance(s, q, i, held - q.Stages[i].Sum);
            }
        }
        finally
        {
            s.RecountingQuestItems = false;
        }
    }

    /// <summary>Whether any stage after <paramref name="stage"/> (following the chain) is done already.</summary>
    private static bool LaterStageDone(QuestState q, int stage)
    {
        var seen = new HashSet<int>();
        for (var n = q.Info.Stages[stage].Next; n >= 0 && n < q.Stages.Count && seen.Add(n); n = q.Info.Stages[n].Next)
            if (q.Stages[n].Done) return true;
        return false;
    }

    /// <summary>
    /// <c>.quests</c> lists your quests in progress; <c>.dropquest &lt;id or name&gt;</c> abandons one (the client's quest log
    /// has no abandon button on this build). It can be taken again.
    /// </summary>
    private void QuestCommand(Session s, string verb, string[] args)
    {
        var open = s.Quests.Values.Where(q => !q.Completed).OrderBy(q => q.Info.Id).ToList();
        if (verb == "quests" || args.Length == 0)
        {
            Tell(s, open.Count == 0 ? "No quests in progress." : "Quests in progress: " + string.Join(", ", open.Select(q => $"{q.Info.Name} ({q.Info.Id})"))
                + (verb == "quests" ? "\nDrop one with .dropquest <id or name>." : "\nUsage: .dropquest <id or name>"));
            return;
        }
        var query = string.Join(' ', args);
        var match = int.TryParse(query, out var id) ? open.FirstOrDefault(q => q.Info.Id == id)
            : open.FirstOrDefault(q => q.Info.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
              ?? open.FirstOrDefault(q => q.Info.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Tell(s, $"You have no quest in progress called {query}. See .quests");
            return;
        }
        DropQuest(s, match);
        SendQuestData(s);
        Tell(s, $"Dropped {match.Info.Name}. You can take it again.");
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
            // A stage naming no monster lets any monster in the quest's zone drop the item now and then, except an item
            // a quest gives (owner's report, 2026-10-20: Balmon the Gladiator dropped Severina's Focus of Enchantment for
            // Broon's Enchanted, whose zone is Farcrag Castle): those only come from their quest.
            var chance = stage.Sources.Contains(mob.Template.Id) ? QuestItemChance
                : stage.Sources.Count == 0 && q.Info.Zone == s.Position.Zone && !_questRewards.RewardItems.Contains(stage.Target.Id) ? QuestItemZoneChance : 0;
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
