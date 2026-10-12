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

    /// <summary>Writes CHPrivateMods.dll and its custom item list next to the patched Assembly-CSharp.dll.</summary>
    public static void Install(string assemblyCSharpPath)
    {
        var dir = Path.GetDirectoryName(assemblyCSharpPath)!;
        File.WriteAllBytes(Path.Combine(dir, AssemblyPatcher.ModsAssembly + ".dll"), Bytes());
        File.WriteAllLines(Path.Combine(dir, AssemblyPatcher.ModsAssembly + ".items.txt"), ItemLines());
    }

    /// <summary>server/content/custom-items.json (embedded) as <c>id|base|effect|name|description</c> lines for the mod.</summary>
    public static IEnumerable<string> ItemLines()
    {
        using var stream = typeof(ClientMods).Assembly.GetManifestResourceStream("custom-items.json");
        if (stream is null) yield break;
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string Clean(string? v) => (v ?? "").Replace('|', '/').Replace('\n', ' ');
            var effect = e.TryGetProperty("effect", out var fx) ? fx.GetString() : "";
            var description = e.TryGetProperty("description", out var d) ? d.GetString() : "";
            yield return $"{e.GetProperty("id").GetInt32()}|{e.GetProperty("base").GetInt32()}|{Clean(effect)}|{Clean(e.GetProperty("name").GetString())}|{Clean(description)}";
        }
    }
}
