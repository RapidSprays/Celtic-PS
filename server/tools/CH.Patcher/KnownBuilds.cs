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
        // Windows: not yet verified (needs the Windows depot's files; see docs/friends.md).
    ];
}
