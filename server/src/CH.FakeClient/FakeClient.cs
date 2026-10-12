using System.Diagnostics;
using System.Net;
using CH.Protocol;
using CH.Protocol.Messages;
using Lidgren.Network;

namespace CH.FakeClient;

/// <summary>World line from worldlist.aspx (NetworkManager.cs:5598-5610).</summary>
public sealed record WorldEntry(int Id, string Name, string Ip, int Port, bool Blocked, string HostName, int GlobalZone, int Characters);

/// <summary>
/// Scripted stand-in for the real client: the same HTTP calls, Lidgren handshake, message layouts and session rules,
/// driven step by step. Used by the tests and for manual protocol checks; the real client stays the final judge.
/// </summary>
public sealed class FakeClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly NetClient _client;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private uint _session;

    public FakeClient()
    {
        var config = new NetPeerConfiguration(Envelope.AppIdentifier);
        config.EnableMessageType(NetIncomingMessageType.DiscoveryResponse);
        _client = new NetClient(config);
        _client.Start();
    }

    public uint SessionId => _session;

    // ---- HTTP ---------------------------------------------------------------------------------

    /// <summary>The first request the real client makes (EnvironmentSetup.cs:93). Returns (env, patch root, balance URL format).</summary>
    public async Task<(int Env, string PatchRoot, string BalanceFormat)> FetchEnvironmentAsync(string environmentUrl)
    {
        var body = await _http.GetStringAsync(string.Format(environmentUrl, 10032, "standalone"));
        var parts = body.Split(',');
        return (int.Parse(parts[0]), parts[1], parts.Length > 3 ? parts[3] : "");
    }

    public async Task<List<WorldEntry>> FetchWorldListAsync(string patchRoot, string username)
    {
        var body = await _http.GetStringAsync($"{patchRoot}worldlist.aspx?uname={Uri.EscapeDataString(username)}&regt=0&cver=10032");
        var lines = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines[0] != "ok") throw new InvalidDataException($"world list not ok: {lines[0]}");
        var count = int.Parse(lines[4]);
        return lines.Skip(5).Take(count).Select(l =>
        {
            var f = l.Split(',');
            return new WorldEntry(int.Parse(f[0]), f[1], f[2], int.Parse(f[3]), f[4] == "1", f[7], int.Parse(f[8]), int.Parse(f[9]));
        }).ToList();
    }

    public async Task<string> FetchAsync(string url) => await _http.GetStringAsync(url);

    // ---- Lidgren ------------------------------------------------------------------------------

    /// <summary>World status ping, as the world select screen does (CWorldData). Returns (loginAvailable, status).</summary>
    public (bool LoginAvailable, byte Status) Discover(string host, int port, TimeSpan? timeout = null)
    {
        _client.DiscoverKnownPeer(host, port);
        var msg = Next(m => m.MessageType == NetIncomingMessageType.DiscoveryResponse, timeout);
        return (msg.ReadByte() == 1, msg.ReadByte());
    }

    public void Connect(string host, int port, TimeSpan? timeout = null)
    {
        _client.Connect(host, port);
        var deadline = Stopwatch.StartNew();
        while (_client.ConnectionStatus != NetConnectionStatus.Connected)
        {
            if (deadline.Elapsed > (timeout ?? DefaultTimeout)) throw new TimeoutException("connect timed out");
            Drain();
            Thread.Sleep(10);
        }
    }

    public VersionCheckReply VersionCheck(int protocolVersion = 1, int balanceVersion = 101)
    {
        Send(Opcode.VersionCheck, new VersionCheck(protocolVersion, balanceVersion).Write);
        return VersionCheckReply.Read(Expect(Opcode.VersionCheckReply));
    }

    public LoginReply Login(string username, string password = "FAKE-HASH")
    {
        var request = new LoginRequest(false, username, password, "fake,0,0,mac", 143, "", "fake-device", 0, "", "EN_UK", "", 10032, "GB", "6.15.0", 0);
        Send(Opcode.Login, request.Write);
        var reply = LoginReply.Read(Expect(Opcode.LoginReply));
        if (reply.Status == CommandStatus.Accepted) _session = reply.SessionId;
        return reply;
    }

    public CharacterListReply RequestCharacterList()
    {
        Send(Opcode.RequestCharacterList, _ => { });
        return CharacterListReply.Read(Expect(Opcode.RequestCharacterListReply));
    }

    public CreateCharacterReply CreateCharacter(string name, int race = 1, int @class = 1, byte gender = 0)
    {
        var appearance = new Appearance(race, 1, 1, 1, 1, 1, 0, 0, 1f, gender);
        Send(Opcode.CreateCharacter, new CreateCharacterRequest(name, appearance, @class).Write);
        return CreateCharacterReply.Read(Expect(Opcode.CreateCharacterReply));
    }

    /// <summary>RequestTweak, then StartGame, as CController does after character select/create.</summary>
    public StartGameReply StartGame(uint characterId, string name)
    {
        Send(Opcode.RequestTweak, _ => { });
        TweakReply.Read(Expect(Opcode.TweakReply));
        Send(Opcode.StartGame, new StartGameRequest(name, characterId).Write);
        return StartGameReply.Read(Expect(Opcode.StartGameReply));
    }

    /// <summary>Zone scene loaded: FinishedZoning, then wait for EnteringNewZoneUpdate and AllZoningMessagesSent.</summary>
    public void FinishZoning()
    {
        Send(Opcode.FinishedZoning, _ => { });
        Expect(Opcode.EnteringNewZoneUpdate);
        Expect(Opcode.AllZoningMessagesSent);
    }

    public void Move(float x, float y, float z, float angle, float speed = 5f) =>
        Send(Opcode.PlayerMove, new PlayerMove(NetTime.Now, x, y, z, angle, speed).Write, NetDeliveryMethod.ReliableSequenced);

    /// <summary>Waits until everything sent so far has been acknowledged by the server.</summary>
    public void Flush(TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (_client.ServerConnection is { } c && HasUnacked(c))
        {
            if (deadline.Elapsed > (timeout ?? DefaultTimeout)) throw new TimeoutException("flush timed out");
            Drain();
            Thread.Sleep(10);
        }
    }

    private static bool HasUnacked(NetConnection c)
    {
        c.GetSendQueueInfo(NetDeliveryMethod.ReliableOrdered, 0, out var windowSize, out var freeWindowSlots);
        c.GetSendQueueInfo(NetDeliveryMethod.ReliableSequenced, 0, out var seqWindow, out var seqFree);
        return freeWindowSlots < windowSize || seqFree < seqWindow;
    }

    public void Disconnect()
    {
        _client.Disconnect("bye");
        Thread.Sleep(100);
    }

    // ---- plumbing -----------------------------------------------------------------------------

    /// <summary>Sends any client message with the real client's header (for messages without a helper).</summary>
    public void Send(Opcode opcode, Action<NetBuffer> write, NetDeliveryMethod delivery = NetDeliveryMethod.ReliableOrdered)
    {
        var msg = Envelope.CreateClientMessage(_client, opcode, _session);
        write(msg);
        _client.SendMessage(msg, delivery);
    }

    /// <summary>Waits for a Data message with the given opcode, applying the real client's session check. Returns it positioned after the header.</summary>
    public NetIncomingMessage Expect(Opcode opcode, TimeSpan? timeout = null)
    {
        // One deadline for the whole wait: other messages (mob movement, regen) must not extend it.
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? DefaultTimeout;
        while (true)
        {
            var remaining = limit - deadline.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException($"no {opcode} from the server within {limit.TotalSeconds:F1}s");
            var msg = Next(m => m.MessageType == NetIncomingMessageType.Data, remaining);
            var header = Envelope.ReadServerHeader(msg);
            if (header.SessionId is { } sid && sid != _session)
                throw new InvalidDataException($"{header.Opcode}: session {sid} does not match {_session} (the real client would drop it)");
            if (header.Opcode == opcode) return msg;
        }
    }

    private NetIncomingMessage Next(Func<NetIncomingMessage, bool> match, TimeSpan? timeout)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? DefaultTimeout;
        while (deadline.Elapsed < limit)
        {
            var msg = _client.ReadMessage();
            if (msg is null)
            {
                _client.MessageReceivedEvent.WaitOne(20);
                continue;
            }
            if (match(msg)) return msg;
        }
        throw new TimeoutException("no matching message from the server");
    }

    /// <summary>Lets Lidgren deliver status messages while waiting. Only called when no reply is expected.</summary>
    private void Drain()
    {
        while (_client.ReadMessage() is not null) { }
    }

    public void Dispose()
    {
        _client.Shutdown("dispose");
        _http.Dispose();
    }
}
