namespace CH.Patcher;

public enum ClientPlatform
{
    Mac,
    Windows,
}

/// <summary>
/// A client build the patch set is verified against: where its files sit inside the game folder and their pristine
/// SHA-256 hashes (inventories/sha256.txt for the Mac build). The setup command refuses any other build.
/// </summary>
public sealed record KnownBuild(string Build, ClientPlatform Platform, string DllPath, string DllSha256, string GlobalGameManagersPath, string GlobalGameManagersSha256)
{
    public const int SteamAppId = 2829820;

    public static readonly IReadOnlyList<KnownBuild> All =
    [
        new("24928510", ClientPlatform.Mac,
            "Contents/Resources/Data/Managed/Assembly-CSharp.dll", "4a5ca43e1b09c97f211075a2f6c3198d90cf2da6633ac9b14f0e28b4f64b351b",
            "Contents/Resources/Data/globalgamemanagers", "4ef5eee5aed21e332605219002b6ca9abb4065b356bd8a774fdbe0fd47df84ed"),
        // Windows (depot 2829821, same Steam build): obfuscated, so patched through its name map (docs/windows-names.md),
        // which CH.Patcher embeds from ~/CelticHeroesAnalysis/extracted/<build>/names-<dll sha256>.json.
        new("24928510", ClientPlatform.Windows,
            "CelticHeroes_Data/Managed/Assembly-CSharp.dll", "c257c998faa563a5fd86a8ff0d58a8f7a7f954333f6aa04dd18b0ee7b508d126",
            "CelticHeroes_Data/globalgamemanagers", "38a6c0ba645bd98a13223a74001a434d3bd65fe10ddcfe1b0a40803f58399534"),
    ];
}
