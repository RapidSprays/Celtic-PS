using CH.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CH.Server;

/// <summary>
/// Local stand-in for the client's HTTP "patch server" (docs/boot-sequence.md). Every page answers in the plain-text
/// formats the client parses. Unknown pages are logged so iteration shows what the client asks for next.
/// </summary>
public sealed class PatchServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly Func<GameStore> _store;
    private readonly ILogger _log;
    private WebApplication? _app;

    public PatchServer(ServerOptions options, Func<GameStore> store, ILogger log)
    {
        _options = options;
        _store = store;
        _log = log;
    }

    public int Port { get; private set; }

    public string Root => $"http://{_options.PublicHost}:{Port}/patchserver/";

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(_options.BindAddress, _options.HttpPort));
        _app = builder.Build();
        _app.MapGet("/patchserver/{page}", (HttpContext ctx, string page) => Handle(ctx, page));
        _app.MapGet("/patchserver/balancefiles/{version}/balance-client.json", (string version) =>
        {
            // The server replies VersionCheck OK, so the client never needs this. Serving nothing keeps the
            // TypeNameHandling.Auto download path (docs/risk-register.md R8) closed.
            _log.LogWarning("Client requested balance download for version {Version}; not served", version);
            return Results.NotFound();
        });
        // Redirected by the client patch set (docs/patch-manifest.md): Unity engine telemetry (P8) and live web links (P4).
        _app.MapMethods("/u/{**rest}", ["GET", "POST", "PUT"], (HttpContext ctx) =>
        {
            _log.LogInformation("Blocked Unity telemetry request {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            return Results.NotFound();
        });
        _app.MapGet("/external/{**rest}", (HttpContext ctx) =>
        {
            _log.LogInformation("Client asked for external page {Path}", ctx.Request.Path);
            return Results.NotFound();
        });
        // Connectivity check (P4b): the client only needs a 200.
        _app.MapGet("/ping", () => Results.Text("ok"));
        _app.MapFallback((HttpContext ctx) =>
        {
            _log.LogWarning("HTTP unhandled {Path}", ctx.Request.Path);
            return Results.NotFound();
        });
        await _app.StartAsync();
        Port = new Uri(_app.Urls.First()).Port;
        _log.LogInformation("Patch server listening on {Url}", _app.Urls.First());
    }

    private IResult Handle(HttpContext ctx, string page)
    {
        var q = ctx.Request.Query;
        _log.LogInformation("HTTP {Page} [{Params}]", page, string.Join(",", q.Keys));
        switch (page.ToLowerInvariant())
        {
            case "patchserverurl.aspx":
                // env,patchServerUrl,redeemEnabled,balanceCdnUrlFormat (EnvironmentSetup.cs:115-128). Env 0 = Test.
                return Text($"0,{Root},0,{Root}balancefiles/{{0}}/balance-client.json");

            case "patchinforequest.aspx":
                return Text(_options.PatchLevel.ToString());

            case "patchfilerequest.aspx":
                return Text("");

            case "worldlist.aspx":
                return Text(WorldList(q["uname"].ToString()));

            case "checkaccountconflictstate.aspx":
                return Text("0"); // no conflict (AccountConflictHelper.cs:71-91)

            case "privacypolicy.aspx":
                // version, top text, bottom text, policy url, terms url (CController.cs:1609-1615)
                return Text(string.Join("\n",
                    "1",
                    "Private test server.",
                    "No data leaves this machine.",
                    $"http://{_options.PublicHost}:{Port}/privacy",
                    $"http://{_options.PublicHost}:{Port}/terms"));

            case "purchaseslist.aspx":
                return Text("");

            case "namegenerator.aspx":
                // "0" then one suggested name per line (RandomCharNames.cs:76-95).
                return Text("0\n" + string.Join("\n", NameSuggestions.Take(Random.Shared.Next(8, 13))));

            default:
                _log.LogWarning("HTTP page not implemented: {Page}", page);
                return Results.NotFound();
        }
    }

    private static IEnumerable<string> NameSuggestions
    {
        get
        {
            string[] first = ["Aed", "Bran", "Cael", "Dara", "Eithne", "Fionn", "Gorm", "Iona", "Lugh", "Maeve", "Niall", "Orla", "Ronan", "Saoirse", "Tadhg", "Una"];
            string[] last = ["mor", "an", "ach", "wyn", "ric", "dan", "lin", "ra", "gal", "bran"];
            while (true) yield return first[Random.Shared.Next(first.Length)] + last[Random.Shared.Next(last.Length)];
        }
    }

    /// <summary>World list (NetworkManager.cs:5580-5639). One world, pointing at our game server.</summary>
    internal string WorldList(string username)
    {
        var characters = 0;
        if (!string.IsNullOrWhiteSpace(username))
        {
            var store = _store();
            characters = store.ListCharacters(store.GetOrCreateAccount(username).Id).Count;
        }
        var o = _options;
        return string.Join("\n",
            "ok",
            $"{o.PatchLevel},0",
            $"http://{o.PublicHost}:{Port}/stats/",
            o.MaxCharacterSlots.ToString(),
            "1",
            // worldID,name,ip,port,blocked,private,unused,hostName,globalServerZone,numCharacters
            $"{o.WorldId},{o.WorldName},{o.PublicHost},{GamePortProvider?.Invoke() ?? o.GamePort},0,0,0,{o.PublicHost},-1,{characters}");
    }

    /// <summary>Set by the host so the world list advertises the game server's actual (possibly ephemeral) port.</summary>
    public Func<int>? GamePortProvider { get; set; }

    private static IResult Text(string body) => Results.Text(body, "text/plain");

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
