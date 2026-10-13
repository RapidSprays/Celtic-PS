using CH.Server;
using Microsoft.Extensions.Logging;

// CH.Server backup --data DIR --out FILE: an online copy of the database, safe while the server runs (the background
// service's daily backup; no sqlite3 command needed, which Windows doesn't have).
if (args.Length > 0 && args[0] == "backup") return Backup.Run(args[1..]);

ServerOptions options;
try
{
    options = ServerOptions.FromArgs(args);
    options.Validate();
}
catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("usage: CH.Server [--http-port 5555] [--game-port 5556] [--data DIR] [--bind 127.0.0.1] [--public-host 127.0.0.1] [--allow-remote]");
    return 2;
}

using var logs = LoggerFactory.Create(b => b
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    })
    .SetMinimumLevel(LogLevel.Information));

await using var host = await ServerHost.StartAsync(options, logs);
var log = logs.CreateLogger("main");
log.LogInformation("Data: {Data}. Frame log: {Frames}", options.DataDirectory, host.FrameLogPath);
log.LogInformation("Environment URL for the private client: {Url}", $"{host.Patch.Root}PatchServerURL.aspx?iav={{0}}&platform={{1}}");

// Ctrl+C in a terminal, SIGTERM from scripts (background jobs inherit an ignored SIGINT).
var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    done.TrySetResult();
};
using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c =>
{
    c.Cancel = true;
    done.TrySetResult();
});
await done.Task;
log.LogInformation("Stopping");
return 0;
