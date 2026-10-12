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

    /// <summary>
    /// server/content/custom-items.json (embedded) as lines for the mod:
    /// <c>id|base|effect|name|description|stats|modelBase|modelId|particles</c>, particles as
    /// <c>effect:bone:x,y,z:scale</c> joined by ';' (empty fields when unused).
    /// </summary>
    public static IEnumerable<string> ItemLines()
    {
        using var stream = typeof(ClientMods).Assembly.GetManifestResourceStream("custom-items.json");
        if (stream is null) yield break;
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        static string Clean(string? v) => (v ?? "").Replace('|', '/').Replace('\n', ' ').Replace(';', ',');
        static string F(float f) => f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string Str(string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";
            string modelBase = "", modelId = "", particles = "";
            if (e.TryGetProperty("model", out var model))
            {
                modelBase = model.GetProperty("base").GetInt32().ToString();
                modelId = model.GetProperty("id").GetInt32().ToString();
                particles = string.Join(";", model.GetProperty("particles").EnumerateArray().Select(p =>
                {
                    var o = p.GetProperty("offset").EnumerateArray().Select(x => F(x.GetSingle())).ToArray();
                    return $"{p.GetProperty("effect").GetInt32()}:{p.GetProperty("bone").GetString()}:{string.Join(",", o)}:{F(p.GetProperty("scale").GetSingle())}";
                }));
            }
            yield return string.Join("|", e.GetProperty("id").GetInt32(), e.GetProperty("base").GetInt32(), Clean(Str("effect")),
                Clean(e.GetProperty("name").GetString()), Clean(Str("description")), Str("stats"), modelBase, modelId, particles);
        }
    }
}
