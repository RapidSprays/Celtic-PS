using CH.Protocol;
using Lidgren.Network;

namespace CH.Server;

/// <summary>
/// The new-player tutorial (Tutorial 13037) and first-time hints (FirstTime 13038; docs/content-systems-notes.md).
/// Off unless a player opts in with <c>.tutorial on</c>: the tutorial follows the official quest flow and on this server
/// its overlay gets stuck (GUIQuestLog.FirstQuestIconRect throws every frame with an empty quest log). Opted-in
/// characters below level 20 get the tutorials they haven't finished and the first-time ids they haven't seen, plus
/// 100, which starts the hint chain; completions come back and are kept.
/// EndTutorial is never sent: the client crashes ending the one that's running.
/// </summary>
public sealed partial class GameServer
{
    private const int TutorialMaxLevel = 20, TutorialOn = -2, TutorialFlagOffset = 1000, FirstTimeChain = 100;
    private static readonly int[] Tutorials = [91, 92, 93, 94];
    private static readonly int[] FirstTimes = [51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63];

    private void SendTutorials(Session s)
    {
        if (s.TutorialsSent || s.Sheet!.Level >= TutorialMaxLevel) return;
        s.TutorialsSent = true;
        var flags = _store.ListTutorialFlags(s.Character!.Id);
        if (!flags.Contains(TutorialOn)) return;
        foreach (var id in Tutorials.Where(t => !flags.Contains(TutorialFlagOffset + t)))
            Send(s, Opcode.Tutorial, b => { b.WriteVariableInt32(1); b.WriteVariableInt32(id); });
        foreach (var id in FirstTimes.Where(f => !flags.Contains(f)).Append(FirstTimeChain))
            Send(s, Opcode.FirstTime, b => b.WriteVariableInt32(id));
    }

    private void OnTutorial(Session s, NetIncomingMessage msg)
    {
        if (msg.ReadVariableInt32() == 3) _store.SetTutorialFlag(s.Character!.Id, TutorialFlagOffset + msg.ReadVariableInt32());
    }

    private void OnFirstTime(Session s, int id)
    {
        if (id != FirstTimeChain) _store.SetTutorialFlag(s.Character!.Id, id);
    }

    private void TutorialCommand(Session s, string[] args)
    {
        var on = args.Length > 0 && args[0].Equals("on", StringComparison.OrdinalIgnoreCase);
        var off = args.Length > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
        if (!on && !off)
        {
            Tell(s, "Usage: .tutorial on|off");
            return;
        }
        _store.SetTutorialFlag(s.Character!.Id, TutorialOn, on);
        Tell(s, off ? "Tutorial hints are off from your next login." : "Tutorial hints are on from your next login (below level 20). They can get stuck; .tutorial off turns them off again.");
    }
}
