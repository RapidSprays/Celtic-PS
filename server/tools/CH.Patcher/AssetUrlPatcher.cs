using System.Text;
using System.Text.RegularExpressions;

namespace CH.Patcher;

/// <summary>
/// P8: Unity's engine telemetry endpoints (UnityConnectSettings: analytics config and events, crash reporting,
/// performance reporting) live as serialized strings in <c>globalgamemanagers</c> and are used natively before any
/// script runs. Each URL is rewritten in place to a localhost URL of exactly the same byte length, so the asset's
/// length-prefixed layout is untouched and no re-serialization is needed.
/// </summary>
public static class AssetUrlPatcher
{
    /// <summary>Unity Technologies service URLs (engine analytics, crash and performance reporting, dashboard).</summary>
    public static readonly Regex UnityServiceUrl = new(@"https?://[a-z0-9.-]*\.(unity3d|unity)\.com[A-Za-z0-9/._-]*", RegexOptions.IgnoreCase);

    public static (byte[] Patched, IReadOnlyList<(string From, string To)> Changes) Patch(byte[] asset, int httpPort)
    {
        var patched = (byte[])asset.Clone();
        var changes = new List<(string, string)>();
        // Latin-1 maps bytes 1:1 to chars, so string offsets are byte offsets.
        var text = Encoding.Latin1.GetString(asset);
        foreach (Match m in UnityServiceUrl.Matches(text))
        {
            var replacement = SameLength(m.Value, httpPort);
            Encoding.ASCII.GetBytes(replacement).CopyTo(patched, m.Index);
            changes.Add((m.Value, replacement));
        }
        return (patched, changes);
    }

    /// <summary>Builds <c>http://127.0.0.1:port/u/…</c> padded with the original host name to the original length.</summary>
    public static string SameLength(string url, int httpPort)
    {
        var prefix = $"http://127.0.0.1:{httpPort}/u/";
        if (url.Length < prefix.Length + 1)
            throw new PatchException("P8", $"URL too short to redirect at the same length: {url}");
        var host = new Uri(url).Host.Replace('.', '-');
        var fill = new StringBuilder(host);
        while (fill.Length < url.Length) fill.Append('-');
        return (prefix + fill)[..url.Length];
    }

    public static IReadOnlyList<string> RemainingUnityUrls(byte[] asset) =>
        UnityServiceUrl.Matches(Encoding.Latin1.GetString(asset)).Select(m => m.Value).ToList();
}
