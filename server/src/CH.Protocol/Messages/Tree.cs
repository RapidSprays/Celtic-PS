using Lidgren.Network;

namespace CH.Protocol.Messages;

// The private server's own messages (docs/passive-tree.md). The client has no handler for these opcodes; the client mod
// (CHPrivateMods.PassiveTree, patch P14) reads them at the start of NetworkManager.processCommand. An unmodded client
// only logs "unknown received message".

/// <summary>Opcodes outside the client's NetworkCommandType range, used only by this server and its client mod.</summary>
public static class PrivateOpcode
{
    public const Opcode TreeLayout = (Opcode)31000;
    public const Opcode TreeState = (Opcode)31001;
}

/// <summary>Node kinds on the wire.</summary>
public enum TreeNodeKind : byte
{
    Start = 0,
    Small = 1,
    Notable = 2,
    Keystone = 3,
}

public sealed record TreeLayoutNode(int Id, TreeNodeKind Kind, string Name, int X, int Y, int Class, IReadOnlyList<int> Links, IReadOnlyList<string> Lines);

/// <summary>The whole tree: sent once per session (or when the version changes), cached by the mod.</summary>
public sealed record TreeLayout(string Version, IReadOnlyList<TreeLayoutNode> Nodes)
{
    public void Write(NetBuffer b)
    {
        b.Write(Version);
        b.WriteVariableInt32(Nodes.Count);
        foreach (var n in Nodes)
        {
            b.WriteVariableInt32(n.Id);
            b.Write((byte)n.Kind);
            b.Write(n.Name);
            b.WriteVariableInt32(n.X);
            b.WriteVariableInt32(n.Y);
            b.WriteVariableInt32(n.Class);
            b.WriteVariableInt32(n.Links.Count);
            foreach (var l in n.Links) b.WriteVariableInt32(l);
            b.WriteVariableInt32(n.Lines.Count);
            foreach (var line in n.Lines) b.Write(line);
        }
    }

    public static TreeLayout Read(NetBuffer b)
    {
        var version = b.ReadString();
        var nodes = Enumerable.Range(0, b.ReadVariableInt32()).Select(_ => new TreeLayoutNode(
            b.ReadVariableInt32(), (TreeNodeKind)b.ReadByte(), b.ReadString(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(),
            Enumerable.Range(0, b.ReadVariableInt32()).Select(_ => b.ReadVariableInt32()).ToList(),
            Enumerable.Range(0, b.ReadVariableInt32()).Select(_ => b.ReadString()).ToList())).ToList();
        return new TreeLayout(version, nodes);
    }
}

/// <summary>The character's passives. <see cref="Open"/> asks the mod to show the panel (otherwise it only refreshes).</summary>
public sealed record TreeState(bool Open, string Version, int Start, int PointsFree, int PointsTotal, IReadOnlyList<int> Owned, IReadOnlyList<string> Summary)
{
    public void Write(NetBuffer b)
    {
        b.Write((byte)(Open ? 1 : 0));
        b.Write(Version);
        b.WriteVariableInt32(Start);
        b.WriteVariableInt32(PointsFree);
        b.WriteVariableInt32(PointsTotal);
        b.WriteVariableInt32(Owned.Count);
        foreach (var id in Owned) b.WriteVariableInt32(id);
        b.WriteVariableInt32(Summary.Count);
        foreach (var line in Summary) b.Write(line);
    }

    public static TreeState Read(NetBuffer b) => new(
        b.ReadByte() == 1, b.ReadString(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(),
        Enumerable.Range(0, b.ReadVariableInt32()).Select(_ => b.ReadVariableInt32()).ToList(),
        Enumerable.Range(0, b.ReadVariableInt32()).Select(_ => b.ReadString()).ToList());
}
