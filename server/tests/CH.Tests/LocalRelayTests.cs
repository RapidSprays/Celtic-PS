using CH.Patcher;
using CH.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace CH.Tests;

/// <summary>
/// Friends' clients reach the server through the launcher's relay on 127.0.0.1, because the client's Unity build refuses
/// plain HTTP to anything but localhost (LocalRelay). Every HTTP link the server hands out must therefore stay on the
/// origin the client used, while the game server's UDP address stays the server's public one.
/// </summary>
public sealed class LocalRelayTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ch-relay-").FullName;

    [Fact]
    public async Task Links_stay_on_the_relay_origin_and_the_world_keeps_the_public_address()
    {
        const string publicHost = "100.64.0.1"; // what a Tailscale-mode server advertises
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = null, PublicHost = publicHost }, NullLoggerFactory.Instance);
        using var relay = LocalRelay.Start("127.0.0.1", host.Patch.Port, port: 0);
        var origin = $"http://127.0.0.1:{relay.ListeningPort}";

        using var client = new CH.FakeClient.FakeClient();
        var env = await client.FetchEnvironmentAsync($"{origin}/patchserver/PatchServerURL.aspx?iav={{0}}&platform={{1}}");
        Assert.Equal($"{origin}/patchserver/", env.PatchRoot);
        Assert.StartsWith($"{origin}/patchserver/balancefiles/", env.BalanceFormat);

        // Several requests through the relay (keep-alive or not), as the client makes them.
        Assert.Equal("0", await client.FetchAsync($"{env.PatchRoot}CheckAccountConflictState.aspx?params=x"));
        Assert.Contains($"{origin}/privacy", await client.FetchAsync($"{env.PatchRoot}PrivacyPolicy.aspx"));
        var worlds = await client.FetchAsync($"{env.PatchRoot}WorldList.aspx?uname=alice");
        Assert.Contains($"{origin}/stats/", worlds);

        var world = Assert.Single(await client.FetchWorldListAsync(env.PatchRoot, "alice"));
        Assert.Equal((publicHost, host.Game.Port), (world.Ip, world.Port));
    }

    [Fact]
    public async Task Direct_requests_still_get_links_on_their_own_origin()
    {
        await using var host = await ServerHost.StartAsync(
            new ServerOptions { HttpPort = 0, GamePort = 0, DataDirectory = _dir, BalancePath = null }, NullLoggerFactory.Instance);
        using var client = new CH.FakeClient.FakeClient();
        var env = await client.FetchEnvironmentAsync($"{host.Patch.Root}PatchServerURL.aspx?iav={{0}}&platform={{1}}");
        Assert.Equal(host.Patch.Root, env.PatchRoot);
    }

    [Fact]
    public void A_second_relay_on_the_same_port_is_refused_with_a_player_message()
    {
        using var first = LocalRelay.Start("127.0.0.1", 1, port: 0);
        var ex = Assert.Throws<InvalidOperationException>(() => LocalRelay.Start("127.0.0.1", 1, port: first.ListeningPort));
        Assert.Contains("already", ex.Message);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
