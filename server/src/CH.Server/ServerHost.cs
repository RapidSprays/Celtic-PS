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
        foreach (var gm in options.GmAccounts) _store.SetGm(_store.GetOrCreateAccount(gm).Username, true);
        _frames = new FrameLog(options.LogDirectory);
        CH.Server.Game.ClassKit.Load(options.ClassKitsPath); // before Balance, which tags skills by class
        Balance = CH.Server.Game.Balance.Load(options.BalancePath, options.CustomItemsPath);
        logs.CreateLogger("main").LogInformation("Balance data: {State}", Balance.IsLoaded
            ? $"version {Balance.Version}, {Balance.Items.Count} items, {Balance.Skills.Count} skills, {Balance.Mobs.Count} mobs, {Balance.Zones.Count} zones"
            : "not loaded (run scripts/extract-balance.py); gear stats will be zero");
        Game = new GameServer(options, _store, _frames, Balance, logs.CreateLogger("game"));
        // HTTP requests run on other threads: each gets its own connection (the game's is single-threaded).
        Patch = new PatchServer(options, () => new GameStore(options.DatabasePath), logs.CreateLogger("http")) { GamePortProvider = () => Game.Port };
    }

    public ServerOptions Options { get; }

    public CH.Server.Game.Balance Balance { get; }

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
