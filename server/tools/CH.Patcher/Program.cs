// CH.Patcher: builds the private client from the pinned copy (docs/patch-manifest.md).
//
//   dotnet run --project tools/CH.Patcher -- [--build 24928510] [--http-port 5555] [--host 127.0.0.1] [--dry-run] [--discover] [--inspect OUT.dll]
//   CH.Patcher setup [--host <server address>] [--steam-dir DIR] [--dest DIR]   (friends: see docs/friends.md)
//   CH.Patcher play [--dest DIR] [--steam-dir DIR] [--no-launch] [--no-shortcut]  (friends' one-click flow; the default with no
//                                                                                 arguments in a kit build)
//   CH.Patcher verify --game-dir <CelticHeroes.app | Windows game folder>      (read-only: hashes and patch site counts)
//
// Always patches from the pristine, hash-verified snapshot DLL, so running it twice gives the same result. Writes only
// inside ~/CelticHeroesAnalysis/client/<build>/; the Steam install is never touched.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CH.Patcher;
using Mono.Cecil;

// Friends' one-click program: a kit build (server address built in) does everything when double-clicked.
if (args.Length > 0 && args[0] == "play") return FriendApp.Run(args[1..]);
if (args.Length > 0 && args[0] == "notices")
{
    using var notices = typeof(FriendApp).Assembly.GetManifestResourceStream("THIRD-PARTY-NOTICES.txt");
    if (notices is not null) Console.WriteLine(new StreamReader(notices).ReadToEnd());
    return 0;
}
if (args.Length == 0 && FriendSetup.FriendHost() is not null) return FriendApp.Run(args);
if (args.Length > 0 && args[0] == "setup") return FriendSetup.Run(args[1..]);
if (args.Length > 0 && args[0] == "verify") return Verify.Run(args[1..]);
if (args.Length > 0 && args[0] == "map")
{
    // CH.Patcher map --mac <Mac Assembly-CSharp.dll> --windows <Windows Assembly-CSharp.dll> [--out names.json]
    string? macPath = null, winPath = null, outPath = null;
    for (var i = 1; i < args.Length; i++)
        switch (args[i])
        {
            case "--mac": macPath = args[++i]; break;
            case "--windows": winPath = args[++i]; break;
            case "--out": outPath = args[++i]; break;
        }
    if (macPath is null || winPath is null) { Console.Error.WriteLine("usage: map --mac DLL --windows DLL [--out FILE]"); return 2; }
    ModuleDefinition Read(string path)
    {
        var r = new DefaultAssemblyResolver();
        r.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = r });
    }
    using var macModule = Read(macPath);
    using var winModule = Read(winPath);
    var started = Stopwatch.StartNew();
    var matcher = new NameMatcher(macModule, winModule);
    var map = matcher.Build();
    Console.WriteLine($"{matcher.Report} ({started.ElapsedMilliseconds} ms)");
    if (outPath is not null) File.WriteAllText(outPath, map.ToJson());
    // --check a,b,c: where each member name is declared on the Mac side, and whether the map covers it.
    var check = Array.IndexOf(args, "--check") is var ci and >= 0 ? args[ci + 1].Split(',') : [];
    foreach (var name in check)
        foreach (var t in macModule.GetTypes())
        {
            var hits = t.Fields.Where(f => f.Name == name).Select(f => $"{t.FullName}::{name}")
                .Concat(t.Properties.Where(p => p.Name == name).Select(p => $"{t.FullName}::{name}"))
                .Concat(t.Methods.Where(m => m.Name == name).Select(m => $"{t.FullName}::{name}/{m.Parameters.Count}"));
            foreach (var key in hits)
                Console.WriteLine($"  {key} -> {(map.Members.TryGetValue(key, out var w) ? w : "MISSING")}  (type {(map.Types.TryGetValue(t.FullName, out var wt) ? wt : "MISSING")})");
        }
    return 0;
}

var build = "24928510";
var host = "127.0.0.1";
var httpPort = 5555;
var dryRun = false;
var discover = false;
string? inspectPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--build": build = args[++i]; break;
        case "--http-port": httpPort = int.Parse(args[++i]); break;
        case "--host": host = args[++i]; break;
        case "--dry-run": dryRun = true; break;
        case "--discover": discover = dryRun = true; break;
        case "--inspect": inspectPath = Path.GetFullPath(args[++i]); dryRun = true; break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var repo = FindRepoRoot();
const string dllRel = "Contents/Resources/Data/Managed/Assembly-CSharp.dll";
var pristineApp = Path.Combine(home, "CelticHeroesAnalysis", "snapshots", build);
var pristineDll = Path.Combine(pristineApp, dllRel);
var clientDir = Path.Combine(home, "CelticHeroesAnalysis", "client", build);
var clientApp = Path.Combine(clientDir, "CelticHeroes.app");
var clientDll = Path.Combine(clientApp, dllRel);
var steamApp = Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "Celtic Heroes", "CelticHeroes.app");

try
{
    // 1. Inputs must be exactly the inventoried build.
    var inventory = File.ReadLines(Path.Combine(repo, "inventories", "sha256.txt"))
        .Select(l => (Hash: l[..64], Path: l[66..]))
        .ToDictionary(e => e.Path, e => e.Hash);
    var expected = inventory[dllRel];
    Require(Sha256(pristineDll) == expected, $"pristine DLL hash does not match inventories/sha256.txt ({pristineDll})");
    Require(Directory.Exists(clientApp), $"pinned client not found: {clientApp} (run scripts/pin-client.sh)");
    Require(!Path.GetFullPath(clientApp).StartsWith(Path.GetFullPath(steamApp), StringComparison.Ordinal), "refusing to patch the Steam install");

    // 2. Patch in memory.
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(pristineDll)!);
    using var module = ModuleDefinition.ReadModule(pristineDll, new ReaderParameters { AssemblyResolver = resolver });
    var patcher = new AssemblyPatcher(module, httpPort, host) { Discover = discover };
    patcher.ApplyAll();
    foreach (var r in patcher.Results) Console.WriteLine($"  {r.Id,-4} {r.Sites,2} site(s)  {r.Description}");
    var remaining = patcher.RemainingLiveUrls();
    foreach (var r in remaining) Console.WriteLine($"  LIVE URL REMAINS: {r}");
    Require(remaining.Count == 0, "live URLs remain in the patched assembly");

    // P8: Unity engine telemetry URLs in globalgamemanagers, rewritten at the same length. The pristine file is kept
    // next to the pinned copy (hash-verified against the inventory) so re-running starts from the original bytes.
    const string ggmRel = "Contents/Resources/Data/globalgamemanagers";
    var ggmPristine = Path.Combine(clientDir, "pristine", "globalgamemanagers");
    var ggmClient = Path.Combine(clientApp, ggmRel);
    if (!File.Exists(ggmPristine))
    {
        Require(Sha256(ggmClient) == inventory[ggmRel], "globalgamemanagers in the pinned copy is neither pristine nor backed up");
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ggmPristine)!);
            File.Copy(ggmClient, ggmPristine);
        }
    }
    var ggmSource = File.Exists(ggmPristine) ? ggmPristine : ggmClient;
    Require(Sha256(ggmSource) == inventory[ggmRel], "pristine globalgamemanagers hash does not match the inventory");
    var (ggmPatched, ggmChanges) = AssetUrlPatcher.Patch(File.ReadAllBytes(ggmSource), httpPort);
    Console.WriteLine($"  P8   {ggmChanges.Count,2} site(s)  globalgamemanagers: Unity engine telemetry URLs → same-length localhost URLs");
    foreach (var (from, to) in ggmChanges) Console.WriteLine($"         {from}  →  {to}");
    Require(discover || ggmChanges.Count == 7, $"[P8] expected 7 Unity service URLs, found {ggmChanges.Count}");
    Require(ggmPatched.Length == new FileInfo(ggmSource).Length && AssetUrlPatcher.RemainingUnityUrls(ggmPatched).Count == 0, "[P8] patched asset changed length or still has Unity URLs");

    if (inspectPath is not null)
    {
        module.Write(inspectPath);
        Console.WriteLine($"Inspection copy written to {inspectPath} (pinned client untouched).");
    }
    if (dryRun)
    {
        Console.WriteLine(discover ? "Discovery run: nothing written." : "Dry run OK: nothing written.");
        return 0;
    }

    // 3. Write the patched DLL into the pinned copy (via a temp file so a failure leaves the copy unchanged).
    var tmp = clientDll + ".patching";
    module.Write(tmp);
    File.Move(tmp, clientDll, overwrite: true);
    ClientMods.Install(clientDll); // P10's Max button
    File.WriteAllBytes(ggmClient, ggmPatched);

    // P1: separate bundle id → separate PlayerPrefs and persistentDataPath from the live game.
    const string privateBundleId = "com.onethumbmobile.celticheroes.private";
    Run("plutil", "-replace", "CFBundleIdentifier", "-string", privateBundleId, Path.Combine(clientApp, "Contents", "Info.plist"));

    // Ad-hoc re-sign the modified copy.
    Run("codesign", "--force", "--deep", "--sign", "-", clientApp);

    var state = new
    {
        build,
        patchedUtc = DateTime.UtcNow,
        httpPort,
        host,
        bundleId = privateBundleId,
        pristineSha256 = expected,
        patchedSha256 = Sha256(clientDll),
        patches = patcher.Results.Append(new PatchResult("P8", "globalgamemanagers: Unity engine telemetry URLs → same-length localhost URLs", ggmChanges.Count)),
        globalgamemanagersSha256 = Sha256(ggmClient),
    };
    File.WriteAllText(Path.Combine(clientDir, "patch-state.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Patched {clientApp}");
    Console.WriteLine($"  Assembly-CSharp.dll sha256 {state.patchedSha256}");
    Console.WriteLine($"  bundle id {privateBundleId}, environment URL http://{host}:{httpPort}/patchserver/PatchServerURL.aspx");
    return 0;
}
catch (Exception ex) when (ex is PatchException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine($"PATCH FAILED: {ex.Message}");
    return 1;
}

static void Require(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

static string Sha256(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
}

static void Run(string file, params string[] arguments)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {p.StandardError.ReadToEnd().Trim()}");
}

static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "inventories", "sha256.txt"))) return d.FullName;
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "inventories", "sha256.txt"))) return d.FullName;
    throw new InvalidOperationException("repo root (with inventories/sha256.txt) not found");
}
