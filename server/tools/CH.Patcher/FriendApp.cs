using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CH.Patcher;

/// <summary>
/// The friends' one-click program (docs/friends.md): double-click it and it does everything. The first time (or when a
/// newer kit arrives) it makes the patched private copy from the player's own Steam install, installs itself next to
/// it and puts a "Celtic Heroes Private" shortcut on the desktop. Every time it starts Steam if needed, waits for
/// Steam to sign in, checks the host's server is reachable, launches the game and relays its HTTP traffic to the
/// server until it closes (<see cref="LocalRelay"/>). Problems are shown as plain dialogs.
/// The server address is built in (make-friend-kit.sh).
/// </summary>
public static class FriendApp
{
    public const string ShortcutName = "Celtic Heroes Private";
    private const int HttpPort = 5555;

    /// <summary>
    /// Options (for testing; players pass none): --dest DIR, --steam-dir DIR, --no-launch, --no-shortcut.
    /// </summary>
    public static int Run(string[] args)
    {
        string? dest = null, steamDir = null;
        bool launch = true, shortcut = true;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dest": dest = args[++i]; break;
                case "--steam-dir": steamDir = args[++i]; break;
                case "--no-launch": launch = false; break;
                case "--no-shortcut": shortcut = false; break;
            }
        }
        try
        {
            var platform = FriendSetup.CurrentPlatform();
            var host = FriendSetup.FriendHost() ?? throw new InvalidOperationException("This copy has no server address. Ask the host for a new one.");
            dest = Path.GetFullPath(dest ?? FriendSetup.DefaultDestination(platform));

            if (NeedsSetup(dest, host))
            {
                Ui.Notify("Setting up Celtic Heroes Private. This copies the game once and takes a minute or two.");
                FriendSetup.Install(host, HttpPort, platform, steamDir, dest, Console.WriteLine);
            }
            var installed = InstallSelf(dest, platform);
            if (shortcut) MakeShortcut(installed, dest, platform);
            if (!launch) return 0;

            StartSteam(platform);
            if (!FriendSetup.Reachable(host, HttpPort))
                throw new InvalidOperationException($"Can't reach the server ({host}).\n\nCheck that Tailscale is running and connected, then try again. " +
                                                    "If it still fails, the host's server may be off.");
            // The game talks to the relay on 127.0.0.1 (LocalRelay explains why); keep it up until the game closes.
            using var relay = LocalRelay.Start(host, HttpPort);
            using var game = LaunchGame(dest, platform);
            Ui.HideOwnConsole();
            game.WaitForExit();
            return 0;
        }
        catch (Exception ex) when (FriendSetup.IsSetupError(ex))
        {
            Console.Error.WriteLine(ex.Message);
            Ui.Error(ex.Message);
            return 1;
        }
    }

    /// <summary>No copy yet, a copy for another server, or one made by another kit (updates).</summary>
    private static bool NeedsSetup(string dest, string host)
    {
        var state = Path.Combine(dest, "patch-state.json");
        if (!File.Exists(state)) return true;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(state));
            var root = doc.RootElement;
            return root.GetProperty("host").GetString() != host
                || !root.TryGetProperty("kitVersion", out var kit) || kit.GetString() != FriendSetup.KitVersion;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return true;
        }
    }

    /// <summary>
    /// Puts this program next to the private copy so the shortcut keeps working after the download is deleted. On a Mac
    /// the program lives inside its .app, which is copied whole.
    /// </summary>
    private static string InstallSelf(string dest, ClientPlatform platform)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("can't find this program's own file");
        if (platform == ClientPlatform.Windows)
        {
            var target = Path.Combine(dest, "CelticHeroesPrivate.exe");
            if (!string.Equals(Path.GetFullPath(self), target, StringComparison.OrdinalIgnoreCase)) File.Copy(self, target, overwrite: true);
            return target;
        }
        // …/Celtic Heroes Private.app/Contents/Resources/<arch>/CelticHeroesPrivate → the .app
        var app = Ancestors(self).FirstOrDefault(d => d.EndsWith(".app", StringComparison.Ordinal));
        var installedApp = Path.Combine(dest, ShortcutName + ".app");
        if (app is null) return self; // run from a plain folder (tests): nothing to install
        if (!string.Equals(Path.GetFullPath(app), installedApp, StringComparison.Ordinal))
        {
            if (Directory.Exists(installedApp)) Directory.Delete(installedApp, recursive: true);
            Exec("ditto", app, installedApp);
            Exec("xattr", "-dr", "com.apple.quarantine", installedApp);
            // The game's own icon, taken from the player's copy (the kit itself carries nothing of the game).
            var icon = Path.Combine(dest, "CelticHeroes.app", "Contents", "Resources", "PlayerIcon.icns");
            if (File.Exists(icon))
            {
                File.Copy(icon, Path.Combine(installedApp, "Contents", "Resources", "AppIcon.icns"), overwrite: true);
                Exec("plutil", "-replace", "CFBundleIconFile", "-string", "AppIcon", Path.Combine(installedApp, "Contents", "Info.plist"));
                Exec("codesign", "--force", "--deep", "--sign", "-", installedApp);
            }
        }
        return installedApp;
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var d = Path.GetDirectoryName(path); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)) yield return d;
    }

    private static void MakeShortcut(string installed, string dest, ClientPlatform platform)
    {
        var desktop = platform == ClientPlatform.Mac
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop")
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) return;
        if (platform == ClientPlatform.Mac)
        {
            if (!installed.EndsWith(".app", StringComparison.Ordinal)) return;
            var link = Path.Combine(desktop, ShortcutName + ".app");
            if (File.Exists(link) || Directory.Exists(link))
            {
                if (new FileInfo(link).LinkTarget == installed) return;
                if (new FileInfo(link).LinkTarget is null) return; // a real app the player put there: leave it
                File.Delete(link);
            }
            File.CreateSymbolicLink(link, installed);
            return;
        }
        var lnk = Path.Combine(desktop, ShortcutName + ".lnk");
        var gameExe = Directory.EnumerateFiles(Path.Combine(dest, "game"), "*.exe")
            .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)) ?? installed;
        static string Ps(string s) => s.Replace("'", "''");
        Exec("powershell", "-NoProfile", "-NonInteractive", "-Command",
            $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{Ps(lnk)}'); $s.TargetPath = '{Ps(installed)}'; " +
            $"$s.WorkingDirectory = '{Ps(dest)}'; $s.IconLocation = '{Ps(gameExe)},0'; $s.Description = 'Celtic Heroes on your friend''s private server'; $s.Save()");
    }

    /// <summary>Starts Steam if it isn't running and waits until it has signed in (a game started before that crashes).</summary>
    private static void StartSteam(ClientPlatform platform)
    {
        var processName = platform == ClientPlatform.Mac ? "steam_osx" : "steam";
        if (Process.GetProcessesByName(processName).Length == 0)
        {
            Ui.Notify("Starting Steam...");
            if (platform == ClientPlatform.Mac) Exec("open", "-g", "-a", "Steam");
            else Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
            var started = Stopwatch.StartNew();
            while (Process.GetProcessesByName(processName).Length == 0)
            {
                if (started.Elapsed > TimeSpan.FromSeconds(90)) throw new InvalidOperationException("Steam didn't start. Start Steam yourself, then try again.");
                Thread.Sleep(1000);
            }
        }
        var waited = Stopwatch.StartNew();
        var told = false;
        while (!SteamSignedIn(platform))
        {
            if (!told) { Ui.Notify("Waiting for Steam to sign in (sign in in the Steam window if it asks)..."); told = true; }
            if (waited.Elapsed > TimeSpan.FromMinutes(3)) throw new InvalidOperationException("Steam isn't signed in. Sign in to Steam, then try again.");
            Thread.Sleep(1000);
        }
        if (told) Thread.Sleep(3000); // let Steam settle after signing in
    }

    /// <summary>The last connection state in Steam's logs/connection_log.txt is "Logged On". Unknown means yes.</summary>
    private static bool SteamSignedIn(ClientPlatform platform)
    {
        var root = FriendSetup.SteamRoot(platform);
        var log = root is null ? null : Path.Combine(root, "logs", "connection_log.txt");
        if (log is null || !File.Exists(log)) return true;
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 64 * 1024), SeekOrigin.Begin);
            var tail = new StreamReader(stream).ReadToEnd();
            var last = Regex.Matches(tail, @"\[(Logged On|Logged Off|Logging On|Logging Off|Connecting|Disconnected)[^\]]*\]").LastOrDefault();
            return last is null || last.Groups[1].Value == "Logged On";
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Starts the private copy with its folder as the working directory (steam_appid.txt there names the game).</summary>
    private static Process LaunchGame(string dest, ClientPlatform platform)
    {
        var exe = platform == ClientPlatform.Mac
            ? Path.Combine(dest, "CelticHeroes.app", "Contents", "MacOS", "Celtic Heroes")
            : Directory.EnumerateFiles(Path.Combine(dest, "game"), "*.exe")
                  .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException("The game is missing from the private copy. Delete the CelticHeroesPrivate folder and run this again.");
        if (!File.Exists(exe)) throw new InvalidOperationException("The game is missing from the private copy. Delete the CelticHeroesPrivate folder and run this again.");
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = dest, UseShellExecute = false };
        if (platform == ClientPlatform.Windows) File.WriteAllText(Path.Combine(dest, "steam_appid.txt"), KnownBuild.SteamAppId.ToString());
        return Process.Start(psi) ?? throw new InvalidOperationException("The game didn't start. Try again.");
    }

    private static void Exec(string file, params string[] arguments)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't run {file}");
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {stderr.Result.Trim()}");
    }

    /// <summary>Plain messages: a notification or dialog on a Mac (no Terminal), a message box plus console text on Windows.</summary>
    private static class Ui
    {
        public static void Notify(string text)
        {
            Console.WriteLine(text);
            if (OperatingSystem.IsMacOS()) Osa($"display notification \"{Esc(text)}\" with title \"{ShortcutName}\"");
        }

        public static void Error(string text)
        {
            if (OperatingSystem.IsMacOS())
                Osa($"display dialog \"{Esc(text)}\" with title \"{ShortcutName}\" buttons {{\"OK\"}} default button 1 with icon caution");
            else if (OperatingSystem.IsWindows())
                MessageBoxW(IntPtr.Zero, text, ShortcutName, 0x10 /* MB_ICONERROR */);
        }

        /// <summary>
        /// Windows: hides the console window the program opened when double-clicked, since it stays running (relaying) for
        /// as long as the game is open. A console the player started it from (shared with other processes) is left alone.
        /// </summary>
        public static void HideOwnConsole()
        {
            if (!OperatingSystem.IsWindows()) return;
            var window = GetConsoleWindow();
            if (window != IntPtr.Zero && GetConsoleProcessList(new uint[2], 2) == 1) ShowWindow(window, 0 /* SW_HIDE */);
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static void Osa(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("osascript") { RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);
                using var p = Process.Start(psi);
                p?.WaitForExit(120_000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
