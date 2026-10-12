using Lidgren.Network;

namespace CH.Protocol.Messages;

/// <summary>A quest stage as the client tracks it: done, and whether it's currently active.</summary>
public readonly record struct QuestStageState(bool Done, bool Active);

/// <summary>An active quest in the FullQuestList (NetworkManager.cs:3156-3197).</summary>
public sealed record ActiveQuest(int QuestId, IReadOnlyList<QuestStageState> Stages);

/// <summary>Per-stage counters for RequestAccurateQuestData (completionSum shown as "sum/total" in the quest log).</summary>
public sealed record QuestProgress(int QuestId, bool Tracked, IReadOnlyList<int> StageSums);

/// <summary>Quest message blocks and replies (docs/quest-notes.md).</summary>
public static class QuestMessages
{
    /// <summary>AvailLists (NetworkManager.cs:2286-2307): available quest ids, then not-yet-available ones.</summary>
    public static void WriteAvailable(NetBuffer b, IReadOnlyCollection<int> available, IReadOnlyCollection<int> notYet)
    {
        b.WriteVariableInt32(available.Count);
        foreach (var q in available) b.WriteVariableInt32(q);
        b.WriteVariableInt32(notYet.Count);
        foreach (var q in notYet) b.WriteVariableInt32(q);
    }

    /// <summary>FullQuestList: completed ids, then active quests with each stage's done/active bytes.</summary>
    public static void WriteFullList(NetBuffer b, IReadOnlyCollection<int> completed, IReadOnlyCollection<ActiveQuest> active)
    {
        b.WriteVariableInt32(completed.Count);
        foreach (var q in completed) b.WriteVariableInt32(q);
        b.WriteVariableInt32(active.Count);
        foreach (var q in active)
        {
            b.WriteVariableInt32(q.QuestId);
            b.WriteVariableInt32(q.Stages.Count);
            b.Write(false); // isFromOldEvent (1 bit)
            WriteStages(b, q.Stages);
        }
    }

    public static (List<int> Completed, List<ActiveQuest> Active) ReadFullList(NetBuffer b)
    {
        var completed = new List<int>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++) completed.Add(b.ReadVariableInt32());
        var active = new List<ActiveQuest>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++)
        {
            var id = b.ReadVariableInt32();
            var count = b.ReadVariableInt32();
            b.ReadBoolean();
            var stages = new List<QuestStageState>();
            for (var j = 0; j < count; j++) stages.Add(new QuestStageState(b.ReadByte() == 1, b.ReadByte() == 1));
            active.Add(new ActiveQuest(id, stages));
        }
        return (completed, active);
    }

    private static void WriteStages(NetBuffer b, IReadOnlyList<QuestStageState> stages)
    {
        foreach (var s in stages)
        {
            b.Write((byte)(s.Done ? 1 : 0));
            b.Write((byte)(s.Active ? 1 : 0));
        }
    }

    /// <summary>QuestStartReply (14002), accepted.</summary>
    public static void WriteStartReply(NetBuffer b, ActiveQuest quest, IReadOnlyCollection<int> available, IReadOnlyCollection<int> notYet)
    {
        b.Write((byte)1);
        b.WriteVariableInt32(quest.QuestId);
        b.WriteVariableInt32(quest.Stages.Count);
        b.Write(false);
        WriteStages(b, quest.Stages);
        WriteAvailable(b, available, notYet);
    }

    public static void WriteRejected(NetBuffer b, string reason)
    {
        b.Write((byte)0);
        b.Write(reason);
    }

    /// <summary>QuestStageProgressReply (14004), mode 0: progress on a counting stage ("N remaining").</summary>
    public static void WriteProgress(NetBuffer b, int questId, int stage, int completionType, int remaining, InventoryWithMoney? inventory)
    {
        b.Write((byte)1);
        b.WriteVariableInt32(0);
        b.WriteVariableInt32(questId);
        b.WriteVariableInt32(stage);
        b.WriteVariableInt32(completionType);
        if (completionType is 0 or 1 or 8 or 12)
        {
            b.WriteVariableInt32(0);
            b.WriteVariableInt32(remaining);
        }
        b.WriteVariableInt32(stage);
        WriteInventory(b, inventory);
    }

    /// <summary>QuestStageProgressReply (14004), mode 1: stage done; stages first..first+count−1 become active (−1 = none).</summary>
    public static void WriteStageDone(NetBuffer b, int questId, int stage, int firstNew, int count, InventoryWithMoney? inventory)
    {
        b.Write((byte)1);
        b.WriteVariableInt32(1);
        b.WriteVariableInt32(questId);
        b.WriteVariableInt32(stage);
        b.WriteVariableInt32(firstNew);
        b.WriteVariableInt32(count);
        WriteInventory(b, inventory);
    }

    private static void WriteInventory(NetBuffer b, InventoryWithMoney? inventory)
    {
        b.Write((byte)(inventory is null ? 0 : 1));
        inventory?.Write(b);
    }

    /// <summary>QuestCompleteReply (14005).</summary>
    public static void WriteComplete(NetBuffer b, int questId, int xp, float newXpPercent, int coins, IReadOnlyList<(int TemplateId, int Quantity)> items,
        InventoryWithMoney inventory, IReadOnlyCollection<int> available, IReadOnlyCollection<int> notYet)
    {
        b.Write((byte)1);
        b.WriteVariableInt32(questId);
        b.WriteVariableInt32(xp);
        b.Write(newXpPercent);
        b.WriteVariableInt32(coins);
        b.WriteVariableInt32(0);
        b.WriteVariableInt32(0);
        b.WriteVariableInt32(items.Count);
        foreach (var (t, q) in items)
        {
            b.WriteVariableInt32(t);
            b.WriteVariableInt32(q);
        }
        b.Write((byte)0);
        inventory.Write(b);
        WriteAvailable(b, available, notYet);
    }

    /// <summary>RequestAccurateQuestData (14008), server → client.</summary>
    public static void WriteAccurate(NetBuffer b, IReadOnlyCollection<QuestProgress> quests)
    {
        b.WriteVariableInt32(quests.Count);
        foreach (var q in quests)
        {
            b.WriteVariableInt32(q.QuestId);
            b.Write(q.Tracked);
            b.WriteVariableInt32(q.StageSums.Count);
            for (var i = 0; i < q.StageSums.Count; i++)
            {
                b.WriteVariableInt32(i);
                b.WriteVariableInt32(q.StageSums[i]);
            }
        }
    }

    /// <summary>PreviewQuestRewards (14009), server → client.</summary>
    public static void WritePreview(NetBuffer b, bool fromLog, int questId, int xp, int coins)
    {
        b.Write(fromLog);
        b.WriteVariableInt32(questId);
        b.WriteVariableInt32(xp);
        b.Write("");
        b.WriteVariableInt32(coins);
        b.WriteVariableInt32(0);   // xp type: experience
        b.WriteVariableInt32(-1);  // no item or skill
        b.Write(false);
        b.Write(false);
        b.WriteVariableInt32(0);
        b.WriteVariableInt32(-1);  // no faction
        b.WriteVariableInt32(0);
    }
}
