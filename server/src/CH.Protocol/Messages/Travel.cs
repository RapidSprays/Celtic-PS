using Lidgren.Network;

namespace CH.Protocol.Messages;

/// <summary>
/// CharacterZoningRequest (12016), client → server (NetworkManager.cs:5446-5455): sent after the player confirms the
/// travel prompt of a zone exit. <see cref="ZonePointId"/> is the exit's id in the client's Zone&lt;N&gt;_zone_points table.
/// </summary>
public sealed record ZoningRequest(float X, float Y, float Z, int ZonePointId)
{
    public void Write(NetBuffer b)
    {
        b.Write(X);
        b.Write(Y);
        b.Write(Z);
        b.WriteVariableInt32(ZonePointId);
    }

    public static ZoningRequest Read(NetBuffer b) => new(b.ReadFloat(), b.ReadFloat(), b.ReadFloat(), b.ReadVariableInt32());
}

/// <summary>
/// TeleportRequest (13022), client → server (NetworkManager.cs:5457-5464): fast travel to a leystone from the map.
/// The client shows its loading screen before sending, so the server must always answer with CorrectPlayerPosition.
/// </summary>
public sealed record TeleportRequest(uint Zone, uint SpawnPointId)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableUInt32(Zone);
        b.WriteVariableUInt32(SpawnPointId);
    }

    public static TeleportRequest Read(NetBuffer b) => new(b.ReadVariableUInt32(), b.ReadVariableUInt32());
}

/// <summary>
/// CorrectPlayerPosition (12013), server → client (NetworkManager.cs:2141-2160, CGame.CorrectPlayerData). A different
/// zone id makes the client unload and load that zone, then send FinishedZoning; the same zone just snaps the player.
/// <see cref="CharacterId"/> must be the player's own server character id or the client ignores the message.
/// </summary>
public sealed record CorrectPlayerPosition(uint Zone, int CharacterId, float X, float Y, float Z, float Direction)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableUInt32(Zone);
        b.WriteVariableInt32(CharacterId);
        b.Write(X);
        b.Write(Y);
        b.Write(Z);
        b.Write(Direction);
    }

    public static CorrectPlayerPosition Read(NetBuffer b) =>
        new(b.ReadVariableUInt32(), b.ReadVariableInt32(), b.ReadFloat(), b.ReadFloat(), b.ReadFloat(), b.ReadFloat());
}
