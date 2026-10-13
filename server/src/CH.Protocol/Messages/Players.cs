using Lidgren.Network;

namespace CH.Protocol.Messages;

/// <summary>One player in a BatchZonePositionUpdate.</summary>
public readonly record struct PlayerPosition(uint ServerId, float X, float Y, float Z, float YAngle, bool Stopped);

/// <summary>
/// BatchZonePositionUpdate (13004), server → client (NetworkManager.cs:2162-2217). A <b>full snapshot</b> of the other
/// players in the zone: anyone missing is hidden. The client adds 1 s to <see cref="ServerTime"/> as the arrival deadline.
/// </summary>
public sealed record BatchZonePositionUpdate(double ServerTime, IReadOnlyList<PlayerPosition> Players)
{
    public void Write(NetBuffer b)
    {
        b.Write(ServerTime);
        b.WriteVariableInt32(Players.Count);
        foreach (var p in Players)
        {
            b.WriteVariableUInt32(p.ServerId);
            b.Write(p.X);
            b.Write(p.Y);
            b.Write(p.Z);
            b.Write(p.YAngle);
            b.Write((byte)(p.Stopped ? 1 : 0));
        }
    }

    public static BatchZonePositionUpdate Read(NetBuffer b)
    {
        var time = b.ReadDouble();
        var list = new List<PlayerPosition>();
        for (int i = 0, n = b.ReadVariableInt32(); i < n; i++)
            list.Add(new PlayerPosition(b.ReadVariableUInt32(), b.ReadFloat(), b.ReadFloat(), b.ReadFloat(), b.ReadFloat(), b.ReadByte() == 1));
        return new BatchZonePositionUpdate(time, list);
    }
}

/// <summary>
/// PlayerAppearanceUpdate (15004), server → other clients (NetworkManager.cs:5774, CBasicPC.updateAppearance):
/// <c>varuint serverId, varint n × {varint slot, value}</c>. Only for players the recipient already knows (its skip
/// parser can't skip slots 20-22). Never send slots 57/58/59/66/68.
/// </summary>
public static class PlayerAppearanceUpdate
{
    public const int Level = 69;
    public const int ShowHeadgear = 63;
    public const int ShowFashion = 64;
    public const int PvpLevel = 72, PvpRating = 73;

    /// <summary>Equipment slots 0-24 (template id, −1 empty), the display options, the level and the PvP rank and rating.</summary>
    public static void Write(NetBuffer b, uint serverId, IReadOnlyList<int> equipment, bool showHeadgear, bool showFashion, int level, int pvpLevel = 0, int pvpRating = 4)
    {
        b.WriteVariableUInt32(serverId);
        b.WriteVariableInt32(equipment.Count + 5);
        for (var slot = 0; slot < equipment.Count; slot++)
        {
            b.WriteVariableInt32(slot);
            b.WriteVariableInt32(equipment[slot]);
        }
        b.WriteVariableInt32(ShowHeadgear);
        b.Write((byte)(showHeadgear ? 1 : 0));
        b.WriteVariableInt32(ShowFashion);
        b.Write((byte)(showFashion ? 1 : 0));
        b.WriteVariableInt32(Level);
        b.WriteVariableInt32(level);
        b.WriteVariableInt32(PvpLevel);
        b.WriteVariableInt32(pvpLevel);
        b.WriteVariableInt32(PvpRating);
        b.WriteVariableInt32(pvpRating);
    }
}

/// <summary>CharacterPlayingEmote (12018). Client → server: <c>bool loop (1 bit), varint emote</c>.</summary>
public sealed record EmoteRequest(bool Loop, int Emote)
{
    public void Write(NetBuffer b)
    {
        b.Write(Loop);
        b.WriteVariableInt32(Emote);
    }

    public static EmoteRequest Read(NetBuffer b) => new(b.ReadBoolean(), b.ReadVariableInt32());

    /// <summary>Server → every client in the zone, the sender included: <c>varuint id, varint animation, bool loop</c>.</summary>
    public static void WriteToClient(NetBuffer b, uint serverId, int animation, bool loop)
    {
        b.WriteVariableUInt32(serverId);
        b.WriteVariableInt32(animation);
        b.Write(loop);
    }
}

/// <summary>PartyMessage (17002) sub-types (CH_PartyNetworkHandler.PartyMessageType).</summary>
public enum PartyMessageType
{
    RequestFromPlayer = 0,   // C→S varint inviteeId
    RequestFromServer = 1,   // S→invitee varint inviterId
    ReplyFromPlayer = 2,     // C→S varint requesterId, varint reply
    ReplyFromServer = 3,     // S→inviter varint responderId, varint reply
    NewConfiguration = 4,    // S→each member: roster
    Leave = 5,               // C→S
}

/// <summary>HW_FRIEND_REPLY, used for party replies.</summary>
public enum PartyReply
{
    Error = 0,
    Accept = 1,
    Reject = 2,
    Timeout = 3,
}

/// <summary>One party member in a roster (CH_PartyNetworkHandler.processNewPartyConfiguration).</summary>
public sealed record PartyMember(string Name, int CharacterId, int Zone, int Level, int Class);

public static class PartyMessage
{
    public static void WriteRoster(NetBuffer b, IReadOnlyList<PartyMember> members)
    {
        b.WriteVariableInt32((int)PartyMessageType.NewConfiguration);
        b.WriteVariableInt32(members.Count);
        foreach (var m in members)
        {
            b.Write(m.Name);
            b.WriteVariableInt32(m.CharacterId);
            b.WriteVariableInt32(m.Zone);
            b.WriteVariableInt32(m.Level);
            b.WriteVariableInt32(m.Class);
        }
    }

    public static void WriteRequest(NetBuffer b, int inviterId)
    {
        b.WriteVariableInt32((int)PartyMessageType.RequestFromServer);
        b.WriteVariableInt32(inviterId);
    }

    public static void WriteReply(NetBuffer b, int responderId, PartyReply reply)
    {
        b.WriteVariableInt32((int)PartyMessageType.ReplyFromServer);
        b.WriteVariableInt32(responderId);
        b.WriteVariableInt32((int)reply);
    }
}
