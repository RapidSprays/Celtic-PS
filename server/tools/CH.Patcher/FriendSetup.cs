using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace CH.Patcher;

/// <summary>
/// <c>setup</c>: makes a private copy of the player's <b>own</b> Steam install of Celtic Heroes, patched to talk to a
/// friend's private server (docs/friends.md). Nothing of the game is shipped in the kit: the copy is made locally from
/// the player's install and only the copy is changed. The Steam install is never modified.
/// </summary>
public static class FriendSetup
{
    public const string PrivateBundleId = "com.onethumbmobile.celticheroes.private";

    public static int Run(string[] args)
    {
        string? host = null;
        string? steamDir = null;
        string? dest = null;
        var httpPort = 5555;
        ClientPlatform? forced = null; // --platform windows: lets the host rehearse the Windows path on a Mac with a downloaded depot
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": host = args[++i]; break;
                case "--steam-dir": steamDir = args[++i]; break;
                case "--dest": dest = args[++i]; break;
                case "--http-port": httpPort = int.Parse(args[++i]); break;
                case "--platform": forced = Enum.Parse<ClientPlatform>(args[++i], ignoreCase: true); break;
                default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
            }
        }
        host ??= FriendHost();
        try
        {
            var platform = forced ?? CurrentPlatform();
            dest = Path.GetFullPath(dest ?? DefaultDestination(platform));
            Install(host, httpPort, platform, steamDir, dest, Console.WriteLine);
            Console.WriteLine();
            Console.WriteLine($"Done. Your private copy talks only to {host}.");
            Console.WriteLine(Reachable(host!, httpPort)
                ? "The server is reachable right now."
                : $"Can't reach the server at {host}:{httpPort} right now: check Tailscale is connected and the host has the server running.");
            Console.WriteLine($"To play, run this program with: play --dest \"{dest}\" (it relays the game to the server while you play).");
            return 0;
        }
        catch (Exception ex) when (IsSetupError(ex))
        {
            Console.Error.WriteLine($"SETUP FAILED: {ex.Message}");
            return 1;
        }
    }

    public static bool IsSetupError(Exception ex) => ex is InvalidOperationException or IOException or PatchException or UnauthorizedAccessException;

    public static ClientPlatform CurrentPlatform() => OperatingSystem.IsMacOS() ? ClientPlatform.Mac
        : OperatingSystem.IsWindows() ? ClientPlatform.Windows
        : throw new InvalidOperationException("Celtic Heroes runs on macOS and Windows only.");

    /// <summary>
    /// Identifies this kit: the program's own build (it carries the mods, the item list and the patch set). A private copy
    /// made by another kit is redone, so friends get updates just by running the new program.
    /// </summary>
    public static string KitVersion => typeof(FriendSetup).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>
    /// Makes (or remakes) the patched private copy in <paramref name="dest"/>. The copy talks to <see cref="LocalRelay"/> on
    /// 127.0.0.1, which <see cref="FriendApp"/> runs while the game is open and forwards to <paramref name="host"/>:<paramref name="httpPort"/>.
    /// Throws <see cref="IsSetupError"/> exceptions with a message meant for the player.
    /// </summary>
    public static void Install(string? host, int httpPort, ClientPlatform platform, string? steamDir, string dest, Action<string> log)
    {
        {
            if (string.IsNullOrWhiteSpace(host) || !Regex.IsMatch(host, @"^[A-Za-z0-9.:\-\[\]]+$"))
                throw new InvalidOperationException("No server address. Ask the host for a new copy of this program.");
            var game = steamDir is not null ? Path.GetFullPath(steamDir) : FindSteamGame(platform)
                ?? throw new InvalidOperationException("Celtic Heroes isn't installed in Steam. Install it (it's free), start it once, quit it, then run this again.");
            var source = platform == ClientPlatform.Mac ? Path.Combine(game, "CelticHeroes.app") : game;
            log($"Celtic Heroes found at {source}");

            // 1. The install must be a build this kit was verified against.
            var dllInSource = FindDll(source, platform);
            var dllHash = Sha256(dllInSource);
            var known = KnownBuild.All.FirstOrDefault(b => b.Platform == platform && b.DllSha256 == dllHash)
                ?? throw new InvalidOperationException(platform == ClientPlatform.Windows && KnownBuild.All.All(b => b.Platform != ClientPlatform.Windows)
                    ? "This kit doesn't support Windows yet. Ask the host for a kit with Windows support."
                    : "Your Celtic Heroes version is different from the one this kit was made for. Ask the host for an updated kit.");
            log($"Version check passed (build {known.Build}).");

            // 2. Fresh copy. Only a folder this tool made before (it holds patch-state.json) is ever replaced.
            var copy = platform == ClientPlatform.Mac ? Path.Combine(dest, "CelticHeroes.app") : Path.Combine(dest, "game");
            if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any() && !File.Exists(Path.Combine(dest, "patch-state.json")))
                throw new InvalidOperationException($"{dest} already exists and wasn't made by this tool. Pass --dest <another folder>.");
            if (Path.GetFullPath(copy).StartsWith(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to write inside the Steam install.");
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
            Directory.CreateDirectory(dest);
            log($"Copying the game to {copy} (a few GB, this takes a minute)...");
            if (platform == ClientPlatform.Mac) Exec("ditto", source, copy);
            else CopyDirectory(source, copy);

            // 3. Patch the copy.
            var dll = Path.Combine(copy, known.DllPath);
            var ggm = Path.Combine(copy, known.GlobalGameManagersPath);
            Require(Sha256(dll) == known.DllSha256 && Sha256(ggm) == known.GlobalGameManagersSha256, "the copy doesn't match the verified build");
            var names = platform == ClientPlatform.Mac ? NameMap.Identity
                : NameMap.Embedded(known.DllSha256) ?? throw new InvalidOperationException("This kit is missing its Windows support files. Ask the host for a new kit.");
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(dll)!);
            var results = new List<PatchResult>();
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(dll)), new ReaderParameters { AssemblyResolver = resolver }))
            {
                var patcher = new AssemblyPatcher(module, LocalRelay.Port, LocalRelay.Address, names);
                patcher.ApplyAll();
                Require(patcher.RemainingLiveUrls().Count == 0, "live URLs remain after patching");
                results.AddRange(patcher.Results);
                module.Write(dll);
            }
            ClientMods.Install(dll, names); // the client mod (P10-P15), plus its name map on Windows
            var (ggmPatched, ggmChanges) = AssetUrlPatcher.Patch(File.ReadAllBytes(ggm), LocalRelay.Port);
            Require(ggmChanges.Count == 7 && AssetUrlPatcher.RemainingUnityUrls(ggmPatched).Count == 0, "Unity telemetry URLs not patched as expected");
            results.Add(new PatchResult("P8", "globalgamemanagers: Unity engine telemetry URLs → same-length localhost URLs", ggmChanges.Count));
            if (platform == ClientPlatform.Windows)
            {
                // P9: own company name → own registry PlayerPrefs and AppData folder, never the live game's saved login.
                Require(AssetUrlPatcher.PatchCompanyName(ggmPatched) == 1, "company name not found exactly once");
                results.Add(new PatchResult("P9", $"globalgamemanagers: companyName → \"{AssetUrlPatcher.PrivateCompanyName}\"", 1));
            }
            File.WriteAllBytes(ggm, ggmPatched);

            if (platform == ClientPlatform.Mac)
            {
                // P1: own bundle id, so the private copy never sees the live game's saved login.
                Exec("plutil", "-replace", "CFBundleIdentifier", "-string", PrivateBundleId, Path.Combine(copy, "Contents", "Info.plist"));
                Exec("codesign", "--force", "--deep", "--sign", "-", copy);
            }

            // 4. Steam app id (so Steam doesn't relaunch the live game). Older kits also wrote a script that started the game
            // directly; without the relay that hangs on the loading screen, so remove it.
            File.WriteAllText(Path.Combine(dest, "steam_appid.txt"), KnownBuild.SteamAppId.ToString());
            if (platform == ClientPlatform.Windows) File.WriteAllText(Path.Combine(copy, "steam_appid.txt"), KnownBuild.SteamAppId.ToString());
            foreach (var old in new[] { "Play Celtic Heroes Private.bat", "Play Celtic Heroes Private.command" })
                File.Delete(Path.Combine(dest, old));
            File.WriteAllText(Path.Combine(dest, "patch-state.json"), JsonSerializer.Serialize(new
            {
                known.Build,
                platform = platform.ToString(),
                host,
                httpPort,
                relayPort = LocalRelay.Port,
                kitVersion = KitVersion,
                patchedUtc = DateTime.UtcNow,
                patches = results,
                patchedSha256 = Sha256(dll),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>The server address: host.txt next to the program if present, else the one built in (make-friend-kit.sh).</summary>
    public static string? FriendHost()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "host.txt");
        if (File.Exists(file)) return File.ReadAllText(file).Trim();
        var built = typeof(FriendSetup).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "FriendHost")?.Value;
        return string.IsNullOrWhiteSpace(built) ? null : built;
    }

    public static string DefaultDestination(ClientPlatform platform) => platform == ClientPlatform.Mac
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CelticHeroesPrivate")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelticHeroesPrivate");

    /// <summary>Steam's default folder plus every library listed in libraryfolders.vdf.</summary>
    public static string? FindSteamGame(ClientPlatform platform)
    {
        var roots = new List<string>();
        if (platform == ClientPlatform.Mac)
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Steam"));
        else
        {
            if (SteamRoot(platform) is { } registered) roots.Add(registered);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        }
        var libraries = new List<string>(roots);
        foreach (var root in roots)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }
        return libraries.Select(l => Path.Combine(l, "steamapps", "common", "Celtic Heroes")).FirstOrDefault(Directory.Exists);
    }

    private static string FindDll(string gameRoot, ClientPlatform platform)
    {
        if (platform == ClientPlatform.Mac) return Path.Combine(gameRoot, "Contents", "Resources", "Data", "Managed", "Assembly-CSharp.dll");
        var data = Directory.EnumerateDirectories(gameRoot, "*_Data").FirstOrDefault()
            ?? throw new InvalidOperationException($"no *_Data folder in {gameRoot}");
        return Path.Combine(data, "Managed", "Assembly-CSharp.dll");
    }

    /// <summary>Steam's own folder: on Windows from the registry (HKCU\Software\Valve\Steam\SteamPath), else the default.</summary>
    public static string? SteamRoot(ClientPlatform platform)
    {
        if (platform == ClientPlatform.Mac)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Steam");
        if (!OperatingSystem.IsWindows()) return null;
        var path = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path.Replace('/', '\\'));
    }

    public static bool Reachable(string host, int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var url = host.Contains(':') && !host.StartsWith('[') ? $"http://[{host}]:{port}/ping" : $"http://{host}:{port}/ping";
            return http.GetAsync(url).GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static void Exec(string file, params string[] arguments)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {stderr.Result.Trim()}");
    }
}
