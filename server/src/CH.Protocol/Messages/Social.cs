using Lidgren.Network;

namespace CH.Protocol.Messages;

/// <summary>FriendList (17009; NetworkManager.cs:3062): the same six fields as EnteringNewZoneUpdate's tail.</summary>
public static class FriendListMessage
{
    public static void Write(NetBuffer b, IReadOnlyList<FriendEntry> friends)
    {
        b.WriteVariableInt32(friends.Count);
        foreach (var f in friends)
        {
            b.Write(f.Name);
            b.WriteVariableInt32(f.Id);
            b.Write((byte)(f.Online ? 1 : 0));
            b.WriteVariableInt32(f.Zone);
            b.WriteVariableInt32(f.Level);
            b.WriteVariableInt32(f.Class);
        }
    }

    public static List<FriendEntry> Read(NetBuffer b)
    {
        var list = new List<FriendEntry>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++)
            list.Add(new FriendEntry(b.ReadString(), b.ReadVariableInt32(), b.ReadByte() != 0, b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32()));
        return list;
    }
}

/// <summary>BlockedList (12023; NetworkManager.cs:1617): this world's blocks, then the cross-world list (always empty here).</summary>
public static class BlockedListMessage
{
    public static void Write(NetBuffer b, IReadOnlyList<(int Id, string Name)> blocked)
    {
        b.WriteVariableInt32(blocked.Count);
        foreach (var (id, name) in blocked)
        {
            b.WriteVariableInt32(id);
            b.Write(name);
        }
        b.WriteVariableInt32(0);
    }

    public static List<(int Id, string Name)> Read(NetBuffer b)
    {
        var list = new List<(int, string)>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++) list.Add((b.ReadVariableInt32(), b.ReadString()));
        return list;
    }
}

/// <summary>SocialPlayerCharacter record (NetworkManager.cs:3124): name, id, online, zone, level, class, race.</summary>
public sealed record SocialCharacter(string Name, int Id, bool Online, int Zone, int Level, int Class, int Race)
{
    public void Write(NetBuffer b)
    {
        b.Write(Name);
        b.WriteVariableInt32(Id);
        b.Write((byte)(Online ? 1 : 0));
        b.WriteVariableInt32(Zone);
        b.WriteVariableInt32(Level);
        b.WriteVariableInt32(Class);
        b.WriteVariableInt32(Race);
    }

    public static SocialCharacter Read(NetBuffer b) =>
        new(b.ReadString(), b.ReadVariableInt32(), b.ReadByte() != 0, b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32(), b.ReadVariableInt32());
}

/// <summary>
/// The clan block (CH_ClanNetworkHandler.ReadClanFromMessage): clan id (−1 = none, nothing follows), name, members
/// (social record + rank), message, then 5 ranks × 7 permission bits (view bank, inspect items, donate, withdraw,
/// purchase slots, set permissions, see logs).
/// </summary>
public sealed record ClanBlock(int ClanId, string Name, IReadOnlyList<(SocialCharacter Member, int Rank)> Members, string Message, IReadOnlyList<bool> Permissions)
{
    public const int Ranks = 5, PermissionsPerRank = 7;
    public static readonly ClanBlock None = new(-1, "", [], "", []);

    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(ClanId);
        if (ClanId < 0) return;
        b.Write(Name);
        b.WriteVariableInt32(Members.Count);
        foreach (var (member, rank) in Members)
        {
            member.Write(b);
            b.WriteVariableInt32(rank);
        }
        b.Write(Message);
        for (var i = 0; i < Ranks * PermissionsPerRank; i++) b.Write(i < Permissions.Count && Permissions[i]);
    }

    public static ClanBlock Read(NetBuffer b)
    {
        var id = b.ReadVariableInt32();
        if (id < 0) return None;
        var name = b.ReadString();
        var members = new List<(SocialCharacter, int)>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++) members.Add((SocialCharacter.Read(b), b.ReadVariableInt32()));
        var message = b.ReadString();
        var permissions = Enumerable.Range(0, Ranks * PermissionsPerRank).Select(_ => b.ReadBoolean()).ToList();
        return new ClanBlock(id, name, members, message, permissions);
    }
}
