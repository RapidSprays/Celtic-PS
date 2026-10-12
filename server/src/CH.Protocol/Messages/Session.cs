using Lidgren.Network;

namespace CH.Protocol.Messages;

// Each message has Write (the side that sends it) and Read (the side that receives it). Field order follows the
// client's decompiled reader or writer, cited per type. Line refs are to NetworkManager.cs unless noted.

/// <summary>VersionCheck (0), client → server, unsigned. Sender at 6788-6795.</summary>
public sealed record VersionCheck(int ProtocolVersion, int BalanceVersion)
{
    public void Write(NetBuffer b)
    {
        b.WriteVariableInt32(ProtocolVersion);
        b.WriteVariableInt32(BalanceVersion);
    }

    public static VersionCheck Read(NetBuffer b) => new(b.ReadVariableInt32(), b.ReadVariableInt32());
}

/// <summary>VersionCheckReply (1), server → client. Reader at 6797-6822. <c>Ok</c> = true skips the balance download.</summary>
public sealed record VersionCheckReply(bool Ok, int ProtocolVersion, int BalanceVersion)
{
    public void Write(NetBuffer b)
    {
        b.Write(Ok);
        b.WriteVariableInt32(ProtocolVersion);
        b.WriteVariableInt32(BalanceVersion);
    }

    public static VersionCheckReply Read(NetBuffer b) => new(b.ReadBoolean(), b.ReadVariableInt32(), b.ReadVariableInt32());
}

/// <summary>
/// Login (10001) / LoginAdvanced (10009), client → server, unsigned. Sender at 4335-4373.
/// The private server only uses <see cref="Username"/>. The password hash, device id and Steam id are read
/// past and dropped, never stored or logged.
/// </summary>
public sealed record LoginRequest(
    bool Advanced,
    string Username,
    string PasswordHash,
    string Analytics,
    int PatchVersion,
    string ClientConstant,
    string DeviceId,
    int RegistrationType,
    string PushToken,
    string Locale,
    string AccessToken,
    int InternalAppVersion,
    string CountryCode,
    string AppVersion,
    ulong SteamId)
{
    public void Write(NetBuffer b)
    {
        b.Write(Username);
        b.Write(PasswordHash);
        b.Write((byte)1);
        b.Write(Analytics);
        b.WriteVariableInt32(PatchVersion);
        b.Write(ClientConstant);
        b.Write(DeviceId);
        if (Advanced) b.WriteVariableInt32(RegistrationType);
        b.Write(PushToken);
        b.Write(Locale);
        if (Advanced) b.Write(AccessToken);
        b.WriteVariableInt32(InternalAppVersion);
        b.Write(CountryCode);
        b.Write(AppVersion);
        b.WriteVariableUInt64(SteamId);
    }

    public static LoginRequest Read(NetBuffer b, bool advanced)
    {
        var username = b.ReadString();
        var passwordHash = b.ReadString();
        b.ReadByte();
        var analytics = b.ReadString();
        var patchVersion = b.ReadVariableInt32();
        var constant = b.ReadString();
        var deviceId = b.ReadString();
        var regType = advanced ? b.ReadVariableInt32() : 0;
        var pushToken = b.ReadString();
        var locale = b.ReadString();
        var accessToken = advanced ? b.ReadString() : "";
        var internalAppVersion = b.ReadVariableInt32();
        var country = b.ReadString();
        var appVersion = b.ReadString();
        var steamId = b.ReadVariableUInt64();
        return new LoginRequest(advanced, username, passwordHash, analytics, patchVersion, constant, deviceId, regType,
            pushToken, locale, accessToken, internalAppVersion, country, appVersion, steamId);
    }

    /// <summary>Log-safe form: username and versions only.</summary>
    public override string ToString() => $"Login(user={Username}, patch={PatchVersion}, iav={InternalAppVersion}, app={AppVersion}, [credentials redacted])";
}

/// <summary>
/// LoginReply (10002), server → client. Reader at 770-830. Accepted carries <c>uint32 session, byte emailRegistered,
/// int32 categorySpender, bool deleting [, string deletingText]</c>. Rejected carries <c>string reason, byte flag</c>.
/// </summary>
public sealed record LoginReply(CommandStatus Status, uint SessionId = 0, bool EmailRegistered = false, int CategorySpender = 0, string Reason = "")
{
    public void Write(NetBuffer b)
    {
        b.Write((byte)Status);
        if (Status == CommandStatus.Accepted)
        {
            b.Write(SessionId);
            b.Write((byte)(EmailRegistered ? 1 : 0));
            b.Write(CategorySpender);
            b.Write(false); // not deleting
        }
        else
        {
            b.Write(Reason);
            b.Write((byte)0);
        }
    }

    public static LoginReply Read(NetBuffer b)
    {
        var status = (CommandStatus)b.ReadByte();
        if (status != CommandStatus.Accepted)
        {
            var reason = b.ReadString();
            b.ReadByte();
            return new LoginReply(status, Reason: reason);
        }
        var session = b.ReadUInt32();
        var email = b.ReadByte() != 0;
        var spender = b.ReadInt32();
        if (b.ReadBoolean()) b.ReadString();
        return new LoginReply(status, session, email, spender);
    }
}

/// <summary>Lidgren discovery response payload (CUI_WorldSelect.cs:184-210): <c>byte loginAvailable, byte status (1 = Available)</c>.</summary>
public static class Discovery
{
    public const byte StatusAvailable = 1;

    public static void WriteResponse(NetBuffer b, bool loginAvailable = true, byte status = StatusAvailable)
    {
        b.Write((byte)(loginAvailable ? 1 : 0));
        b.Write(status);
    }
}

/// <summary>ClientErrorMessage (22001), client → server: one string (NetworkManager.cs:6472-6477).</summary>
public sealed record ClientErrorMessage(string Text)
{
    public static ClientErrorMessage Read(NetBuffer b) => new(b.ReadString());
}
