using System.Net;
using CH.Protocol;
using CH.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace CH.Tests;

/// <summary>
/// The Stage D vertical slice: fake client → HTTP environment and world list → discovery → Lidgren connect → version
/// check → login → create character → start game → zone in → move → restart the server → position persists.
/// </summary>
public sealed class EndToEndTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ch-e2e-").FullName;

    private ServerOptions Options() => new() { HttpPort = 0, GamePort = 0, DataDirectory = _dir };

    private static Task<ServerHost> Start(ServerOptions o) => ServerHost.StartAsync(o, NullLoggerFactory.Instance);

    [Fact]
    public async Task Login_create_enter_world_move_and_position_survives_restart()
    {
        const string user = "alice";
        uint characterId;

        await using (var host = await Start(Options()))
        using (var client = new CH.FakeClient.FakeClient())
        {
            var env = await client.FetchEnvironmentAsync($"{host.Patch.Root}PatchServerURL.aspx?iav={{0}}&platform={{1}}");
            Assert.Equal(0, env.Env); // Test environment
            Assert.Equal(host.Patch.Root, env.PatchRoot);
            Assert.StartsWith("http://127.0.0.1:", env.BalanceFormat);

            Assert.Equal("0", await client.FetchAsync($"{env.PatchRoot}CheckAccountConflictState.aspx?params=x"));
            var world = Assert.Single(await client.FetchWorldListAsync(env.PatchRoot, user));
            Assert.Equal(("127.0.0.1", host.Game.Port, 0), (world.Ip, world.Port, world.Characters));
            Assert.True(world.Id > 1, "the client auto-selects worlds with id > 1");

            Assert.Equal((true, (byte)1), client.Discover(world.Ip, world.Port));
            client.Connect(world.Ip, world.Port);
            Assert.True(client.VersionCheck().Ok);

            var login = client.Login(user);
            Assert.Equal(CommandStatus.Accepted, login.Status);
            Assert.NotEqual(0u, login.SessionId);

            Assert.Empty(client.RequestCharacterList().Characters);

            var created = client.CreateCharacter("Brigid");
            Assert.Equal(CommandStatus.Accepted, created.Status);
            characterId = created.CharacterId;
            Assert.Equal(CommandStatus.Rejected, client.CreateCharacter("Brigid").Status); // name taken

            var start = client.StartGame(characterId, "Brigid");
            Assert.Equal(CommandStatus.Accepted, start.Status);
            Assert.Equal(2, start.Character.Zone);
            Assert.Equal((-109.51f, -3.7f, -16.86f), (start.X, start.Y, start.Z));

            client.FinishZoning();
            client.Move(-100f, -3.5f, -10f, 1.5f);
            client.Move(-90f, -3.4f, -5f, 1.6f);
            client.Flush();
            await Task.Delay(200); // let the pump thread persist the last move
            client.Disconnect();

            Assert.Equal(1, (await client.FetchWorldListAsync(env.PatchRoot, user))[0].Characters);
        }

        await using (var host = await Start(Options()))
        using (var client = new CH.FakeClient.FakeClient())
        {
            client.Connect("127.0.0.1", host.Game.Port);
            client.VersionCheck();
            client.Login(user);
            var character = Assert.Single(client.RequestCharacterList().Characters);
            Assert.Equal("Brigid", character.Name);

            var start = client.StartGame(characterId, "Brigid");
            Assert.Equal((-90f, -3.4f, -5f, 1.6f), (start.X, start.Y, start.Z, start.Direction));
        }
    }

    [Fact]
    public async Task Frame_log_never_contains_login_credentials()
    {
        string logPath;
        await using (var host = await Start(Options()))
        using (var client = new CH.FakeClient.FakeClient())
        {
            client.Connect("127.0.0.1", host.Game.Port);
            client.VersionCheck();
            client.Login("bob");
            logPath = host.FrameLogPath!;
        }
        var log = await File.ReadAllTextAsync(logPath);
        Assert.Contains("\"name\":\"Login\"", log);
        Assert.Contains("[REDACTED]", log);
        Assert.DoesNotContain(Convert.ToHexString("FAKE-HASH"u8), log);
        Assert.DoesNotContain(Convert.ToHexString("fake-device"u8), log);
    }

    [Fact]
    public async Task Messages_before_login_are_ignored()
    {
        await using var host = await Start(Options());
        using var client = new CH.FakeClient.FakeClient();
        client.Connect("127.0.0.1", host.Game.Port);
        Assert.Throws<TimeoutException>(() =>
        {
            client.Expect(Opcode.RequestCharacterListReply, TimeSpan.FromMilliseconds(300));
        });
    }

    [Fact]
    public void Non_loopback_bind_is_refused_without_allow_remote()
    {
        var o = new ServerOptions { BindAddress = IPAddress.Any };
        Assert.Throws<InvalidOperationException>(o.Validate);
        o.AllowRemote = true;
        o.Validate();
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
