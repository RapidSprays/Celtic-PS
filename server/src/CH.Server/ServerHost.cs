using CH.Persistence;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>Runs the HTTP patch server and the Lidgren game server over one database. Used by Program and the tests.</summary>
public sealed class ServerHost : IAsyncDisposable
{
    private readonly GameStore _store;
    private readonly FrameLog _frames;

    private ServerHost(ServerOptions options, ILoggerFactory logs)
    {
        options.Validate();
        Directory.CreateDirectory(options.DataDirectory);
        Options = options;
        _store = new GameStore(options.DatabasePath);
        _frames = new FrameLog(options.LogDirectory);
        Game = new GameServer(options, _store, _frames, logs.CreateLogger("game"));
        Patch = new PatchServer(options, () => _store, logs.CreateLogger("http")) { GamePortProvider = () => Game.Port };
    }

    public ServerOptions Options { get; }

    public GameServer Game { get; }

    public PatchServer Patch { get; }

    public string? FrameLogPath => _frames.Path;

    public static async Task<ServerHost> StartAsync(ServerOptions options, ILoggerFactory logs)
    {
        var host = new ServerHost(options, logs);
        host.Game.Start();
        await host.Patch.StartAsync();
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        await Patch.DisposeAsync();
        Game.Dispose();
        _frames.Dispose();
        _store.Dispose();
    }
}
