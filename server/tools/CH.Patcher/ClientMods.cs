namespace CH.Patcher;

/// <summary>The P10 mod assembly (tools/CH.ClientMods), embedded at build time; copied into the patched client's Managed folder.</summary>
public static class ClientMods
{
    public static byte[] Bytes()
    {
        using var stream = typeof(ClientMods).Assembly.GetManifestResourceStream("CHPrivateMods.dll")
            ?? throw new InvalidOperationException("CHPrivateMods.dll isn't embedded: build server/tools/CH.ClientMods (Release) before CH.Patcher.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Writes CHPrivateMods.dll next to the patched Assembly-CSharp.dll.</summary>
    public static void Install(string assemblyCSharpPath) =>
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(assemblyCSharpPath)!, AssemblyPatcher.ModsAssembly + ".dll"), Bytes());
}
