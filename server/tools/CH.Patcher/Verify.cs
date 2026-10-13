using System.Security.Cryptography;
using Mono.Cecil;

namespace CH.Patcher;

/// <summary>
/// <c>verify --game-dir DIR [--names FILE]</c>: read-only analysis of a game folder (a .app on macOS, or a Windows game folder such as
/// a downloaded depot). Prints the build's hashes and runs the whole patch set in discovery mode, so a new platform or
/// build can be added to <see cref="KnownBuild"/> with confidence. Writes nothing.
/// </summary>
public static class Verify
{
    public static int Run(string[] args)
    {
        string? dir = null, namesPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--game-dir") dir = Path.GetFullPath(args[++i]);
            else if (args[i] == "--names") namesPath = Path.GetFullPath(args[++i]);
        }
        if (dir is null || !Directory.Exists(dir))
        {
            Console.Error.WriteLine("usage: verify --game-dir <CelticHeroes.app or Windows game folder>");
            return 2;
        }
        var mac = dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
        string dataDir;
        if (mac) dataDir = Path.Combine(dir, "Contents", "Resources", "Data");
        else dataDir = Directory.EnumerateDirectories(dir, "*_Data").FirstOrDefault() ?? throw new InvalidOperationException($"no *_Data folder in {dir}");
        var dll = Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll");
        var ggm = Path.Combine(dataDir, "globalgamemanagers");
        Console.WriteLine($"platform   {(mac ? "Mac" : "Windows")}");
        Console.WriteLine($"exe        {(mac ? "-" : string.Join(", ", Directory.EnumerateFiles(dir, "*.exe").Select(Path.GetFileName)))}");
        Console.WriteLine($"dll        {Path.GetRelativePath(dir, dll).Replace('\\', '/')}  sha256 {Sha256(dll)}");
        Console.WriteLine($"ggm        {Path.GetRelativePath(dir, ggm).Replace('\\', '/')}  sha256 {Sha256(ggm)}");
        var known = KnownBuild.All.FirstOrDefault(b => b.DllSha256 == Sha256(dll));
        Console.WriteLine($"known      {(known is null ? "no (add it to KnownBuilds.cs)" : $"{known.Platform} build {known.Build}")}");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(dll)!);
        using var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(dll)), new ReaderParameters { AssemblyResolver = resolver });
        // Windows: the obfuscated build needs its name map (--names FILE, or the one embedded for this hash).
        var names = namesPath is not null ? NameMap.FromJson(File.ReadAllText(namesPath)) : mac ? NameMap.Identity : NameMap.Embedded(Sha256(dll));
        Console.WriteLine($"names      {(names is null ? "none (pass --names, see docs/windows-names.md)" : names.IsIdentity ? "readable build" : $"{names.Methods.Count} real methods mapped")}");
        var patcher = new AssemblyPatcher(module, 5555, "100.64.0.1", names) { Discover = true };
        patcher.ApplyAll();
        foreach (var r in patcher.Results) Console.WriteLine($"  {r.Id,-4} {r.Sites,2} site(s)  {r.Description}");
        Console.WriteLine($"  live URLs left after patching: {patcher.RemainingLiveUrls().Count}");
        var asset = File.ReadAllBytes(ggm);
        var (_, urls) = AssetUrlPatcher.Patch(asset, 5555);
        Console.WriteLine($"  P8   {urls.Count,2} site(s)  Unity telemetry URLs (expected 7)");
        Console.WriteLine($"  P9   {AssetUrlPatcher.PatchCompanyName((byte[])asset.Clone()),2} site(s)  company name (expected 1)");
        return 0;
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
