using Lidgren.Network;

namespace CH.Protocol;

/// <summary>
/// Application-level message headers on top of Lidgren (docs/protocol-notes.md §3).
///
/// Client → server: <c>varuint opcode</c>, then, once the client has a session, a 32-byte signature and a
/// <c>uint32 sessionId</c>. A few pre-login messages are never signed.
///
/// Server → client: <c>byte flag</c>, <c>uint32 sessionId</c> if flag == 1, then <c>varuint opcode</c>.
/// </summary>
public static class Envelope
{
    /// <summary>Lidgren application identifier the client connects with (NetworkManager.cs:271).</summary>
    public const string AppIdentifier = "Chat";

    /// <summary>Length of the client's per-message signature. The server reads past it; it does not verify it.</summary>
    public const int SignatureBytes = 32;

    /// <summary>Opcodes the client builds by hand without the signed header (NetworkManager.cs:4279-4631, 6788).</summary>
    public static readonly IReadOnlySet<Opcode> UnsignedOpcodes = new HashSet<Opcode>
    {
        Opcode.VersionCheck,
        Opcode.Login,
        Opcode.LoginAdvanced,
        Opcode.CreateAccount,
        Opcode.ClearGuestAccount,
        Opcode.AccountOptionsChange,
    };

    // ---- server side -------------------------------------------------------------------------

    /// <summary>Reads the client header. <paramref name="sessionEstablished"/> is whether this connection has been given a session id.</summary>
    public static ClientHeader ReadClientHeader(NetIncomingMessage msg, bool sessionEstablished)
    {
        var opcode = (Opcode)msg.ReadVariableUInt32();
        if (!sessionEstablished || UnsignedOpcodes.Contains(opcode))
            return new ClientHeader(opcode, null);

        msg.SkipPadBits(SignatureBytes * 8);
        return new ClientHeader(opcode, msg.ReadUInt32());
    }

    /// <summary>Starts a server → client message. Pass the session id once the client has one, otherwise 0.</summary>
    public static NetOutgoingMessage CreateServerMessage(NetPeer peer, Opcode opcode, uint sessionId)
    {
        var msg = peer.CreateMessage();
        if (sessionId != 0)
        {
            msg.Write((byte)1);
            msg.Write(sessionId);
        }
        else
        {
            msg.Write((byte)0);
        }
        msg.WriteVariableUInt32((uint)opcode);
        return msg;
    }

    // ---- client side (fake client and tests mirror the real client) --------------------------

    /// <summary>Starts a client → server message the way the real client's <c>createMessage</c> does. The signature is zero-filled.</summary>
    public static NetOutgoingMessage CreateClientMessage(NetPeer peer, Opcode opcode, uint sessionId)
    {
        var msg = peer.CreateMessage();
        msg.WriteVariableUInt32((uint)opcode);
        if (sessionId != 0 && !UnsignedOpcodes.Contains(opcode))
        {
            msg.Write(new byte[SignatureBytes]);
            msg.Write(sessionId);
        }
        return msg;
    }

    /// <summary>Reads a server header the way the real client's <c>HandleMessage</c>/<c>processCommand</c> do.</summary>
    public static ServerHeader ReadServerHeader(NetIncomingMessage msg)
    {
        uint? session = msg.ReadByte() == 1 ? msg.ReadUInt32() : null;
        return new ServerHeader((Opcode)msg.ReadVariableUInt32(), session);
    }
}

public readonly record struct ClientHeader(Opcode Opcode, uint? SessionId);

public readonly record struct ServerHeader(Opcode Opcode, uint? SessionId);
