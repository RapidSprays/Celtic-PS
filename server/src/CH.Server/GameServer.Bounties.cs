using CH.Persistence;
using CH.Protocol;
using CH.Protocol.Messages;
using CH.Server.Game;
using Lidgren.Network;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Bounty boards (docs/economy-notes.md). The boards run the client's dynamic "bb" conversation (440, Ancient 800),
/// which asks for the day's bounties with ReqServerDynamicData "BB-DATA" and gets back a "~" separated list
/// (RecServerDynamicData 22009, BountyBoard.RecBounties). Accepting is an ordinary quest start; the server pushes the
/// board again after every change, because the client never asks twice. Which bounties, how many, the costs and the
/// level rule are <b>our own</b> (the official ones are server-only).
/// </summary>
public sealed partial class GameServer
{
    private const int BountyFree = 3, BountyPaid = 2, BountyCost = 1, BountyConcurrent = 1;
    private const int BountyPledge = 217, AncientBountyPledge = 71040;
    private const int BountyMinLevel = 10, AncientBountyMinLevel = 180;

    // BountyBoard.Bounty.tStatus
    private const int BountyPurchasable = 1, BountyAvailable = 2, BountyStarted = 3, BountyHandedIn = 5, BountyDropped = 6;

    /// <summary>
    /// Bounties we can offer, at the level they're pitched at: their kill stages must name monsters that spawn on
    /// this server, and the level is where the easiest of those spawns.
    /// </summary>
    private Dictionary<int, QuestInfo> BuildBounties()
    {
        var spawnLevel = new Dictionary<int, int>();
        void Note(IEnumerable<int> templates, int level)
        {
            foreach (var t in templates) spawnLevel[t] = Math.Min(level, spawnLevel.GetValueOrDefault(t, int.MaxValue));
        }
        foreach (var camps in _generatedCamps.Values) foreach (var c in camps) Note(c.Templates, c.MinLevel);
        foreach (var camps in ZonePopulation.Camps.Values) foreach (var c in camps) Note(c.Templates, c.MinLevel);

        var bounties = new Dictionary<int, QuestInfo>();
        foreach (var q in _quests.Quests.Values.Where(q => q.IsBounty && q.Stages.Count >= 2))
        {
            var levels = new List<int>();
            var ok = true;
            for (var i = 0; i < q.Stages.Count - 1 && ok; i++)
            {
                var st = q.Stages[i];
                IEnumerable<int> targets = st.Type switch
                {
                    StageType.MobKill => [st.Target.Id],
                    StageType.MobSetKill => _balance.MobSets.GetValueOrDefault(st.Target.Id) ?? [],
                    _ => [],
                };
                var found = targets.Where(spawnLevel.ContainsKey).Select(t => spawnLevel[t]).ToList();
                if (found.Count == 0) ok = false;
                else levels.Add(found.Min());
            }
            if (ok && q.Stages[^1].Type == StageType.Conversation)
                bounties[q.Id] = q with { Level = levels.Max(), MinLevel = 1, Prerequisites = [] };
        }
        return bounties;
    }

    private bool TryQuestInfo(int questId, out QuestInfo info) =>
        _bounties.TryGetValue(questId, out info!) || _quests.Quests.TryGetValue(questId, out info!);

    private static int BountyDay => (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;

    /// <summary>Today's board for the character, drawn when first looked at each day. A bounty in progress carries over.</summary>
    private IReadOnlyList<BountyRecord> Board(Session s, bool ancient)
    {
        var c = s.Character!;
        var day = BountyDay;
        var saved = _store.ListBounties(c.Id, ancient);
        if (saved.Count > 0 && saved[0].Day == day) return saved;

        var level = s.Sheet?.Level ?? 1;
        var carried = saved.Where(b => b.Status == BountyStarted && s.Quests.TryGetValue(b.QuestId, out var q) && !q.Completed).Select(b => b.QuestId).ToList();
        var archetype = ancient ? "AncientBountyBoard" : "BountyBoard";
        var rng = new Random(unchecked((int)(c.Id * 7919 + day * 31 + (ancient ? 1 : 0))));
        var pool = _bounties.Values.Where(q => q.Archetype == archetype && !carried.Contains(q.Id))
            .OrderBy(q => q.Level >= level - 12 && q.Level <= level + 3 ? 0 : Math.Abs(q.Level - level))
            .ThenBy(_ => rng.Next()).Take(BountyFree + BountyPaid).Select(q => q.Id).ToList();
        var board = new List<BountyRecord>();
        foreach (var id in carried) board.Add(new BountyRecord(day, board.Count, id, 0, BountyStarted));
        foreach (var (id, i) in pool.Select((id, i) => (id, i)))
            board.Add(i < BountyFree ? new BountyRecord(day, board.Count, id, 0, BountyAvailable) : new BountyRecord(day, board.Count, id, BountyCost, BountyPurchasable));
        _store.ReplaceBounties(c.Id, ancient, board);
        return board;
    }

    private void SendBoard(Session s, bool ancient)
    {
        var board = Board(s, ancient);
        static string Clean(string text) => text.Replace("~", "-").Replace("^", "-");
        var parts = new List<string>
        {
            ancient ? "ABB-DATA" : "BB-DATA", (ancient ? AncientBountyPledge : BountyPledge).ToString(), (BountyFree + BountyPaid).ToString(),
            BountyConcurrent.ToString(), BountyFree.ToString(), BountyPaid.ToString(), (ancient ? AncientBountyMinLevel : BountyMinLevel).ToString(),
        };
        foreach (var b in board)
            parts.Add($"{Clean(_bounties.GetValueOrDefault(b.QuestId)?.Name ?? $"Bounty {b.QuestId}")}^{b.QuestId}^{b.Cost}^{b.Status}");
        Send(s, Opcode.RecServerDynamicData, m =>
        {
            m.Write((byte)0);
            m.Write(string.Join("~", parts));
        });
    }

    /// <summary>ReqServerDynamicData (22008): the board's data, or buying the next paid bounty.</summary>
    private void OnServerDynamicData(Session s, string request)
    {
        switch (request)
        {
            case "BB-DATA" or "ABB-DATA":
                SendBoard(s, request[0] == 'A');
                break;
            case "BB-BUY" or "ABB-BUY":
                BuyBounty(s, request[0] == 'A');
                break;
            default:
                _log.LogDebug("Unhandled server data request {Request}", request);
                break;
        }
    }

    /// <summary>
    /// "Get More Bounties": the conversation destroys the pledges (DELETE_ITEM) and then sends BB-BUY. Pledges
    /// destroyed this session pay first; otherwise they're taken from the backpack here.
    /// </summary>
    private void BuyBounty(Session s, bool ancient)
    {
        var board = Board(s, ancient);
        var token = ancient ? AncientBountyPledge : BountyPledge;
        var next = board.FirstOrDefault(b => b.Status == BountyPurchasable);
        if (next is null || board.Count(b => b.Status == BountyStarted) >= BountyConcurrent)
        {
            SendBoard(s, ancient);
            return;
        }
        var spent = s.PledgesSpent.GetValueOrDefault(token);
        var fromBag = Math.Max(0, next.Cost - spent);
        var held = Sheet(s).Backpack.Where(i => i.TemplateId == token).Sum(i => i.Quantity);
        if (fromBag > held)
        {
            Tell(s, $"You need {next.Cost} {_balance.Items.GetValueOrDefault(token)?.Name ?? "Bounty Pledge"} for another bounty today.");
            SendBoard(s, ancient);
            return;
        }
        s.PledgesSpent[token] = spent - (next.Cost - fromBag);
        if (fromBag > 0)
        {
            TakeItems(s.Character!.Id, token, fromBag);
            Send(s, Opcode.InventoryUpdate, RefreshSheet(s).InventoryWithMoney().Write);
        }
        _store.SetBountyStatus(s.Character!.Id, ancient, next.Slot, BountyAvailable);
        SendBoard(s, ancient);
    }

    /// <summary>A bounty quest may start when today's board has it open and no other bounty is under way.</summary>
    private bool CanStartBounty(Session s, QuestInfo quest)
    {
        var board = Board(s, quest.Archetype == "AncientBountyBoard");
        return board.Count(b => b.Status == BountyStarted) < BountyConcurrent
            && board.Any(b => b.QuestId == quest.Id && b.Status == BountyAvailable)
            && (s.Sheet?.Level ?? 1) >= (quest.Archetype == "AncientBountyBoard" ? AncientBountyMinLevel : BountyMinLevel);
    }

    private void MarkBounty(Session s, QuestInfo quest, int status)
    {
        if (!quest.IsBounty) return;
        var ancient = quest.Archetype == "AncientBountyBoard";
        var entry = Board(s, ancient).FirstOrDefault(b => b.QuestId == quest.Id && b.Status is not (BountyHandedIn or BountyDropped));
        if (entry is null) return;
        _store.SetBountyStatus(s.Character!.Id, ancient, entry.Slot, status);
        SendBoard(s, ancient);
    }

    /// <summary>BountyBoard (23004): u8 ancient, vi 1 (DropBounty), vi questId. The drop still counts for the day.</summary>
    private void OnBountyBoard(Session s, NetIncomingMessage msg)
    {
        msg.ReadByte();
        if (msg.ReadVariableInt32() != 1) return;
        var questId = msg.ReadVariableInt32();
        if (!s.Quests.TryGetValue(questId, out var q) || q.Completed || !q.Info.IsBounty) return;
        DropQuest(s, q);
    }

    private void DropQuest(Session s, QuestState q)
    {
        s.Quests.Remove(q.Info.Id);
        _store.DeleteQuest(s.Character!.Id, q.Info.Id);
        MarkBounty(s, q.Info, BountyDropped);
        Send(s, Opcode.QuestRefreshMessage, b =>
        {
            QuestMessages.WriteFullList(b, CompletedQuestIds(s), ActiveQuests(s));
            WriteQuestLists(b, s);
        });
    }

    /// <summary>Pledges the player destroyed (the board's "Get More Bounties" runs DELETE_ITEM before BB-BUY).</summary>
    private void NotePledgeDestroyed(Session s, int templateId, int quantity)
    {
        if (templateId is BountyPledge or AncientBountyPledge) s.PledgesSpent[templateId] = s.PledgesSpent.GetValueOrDefault(templateId) + quantity;
    }
}
