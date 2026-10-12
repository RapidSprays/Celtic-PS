using System.Text.Json;

namespace CH.Server.Game;

/// <summary>One thing a token vendor offers and what it costs (items handed in, e.g. Pet Tokens or two of the same pet).</summary>
public sealed record TokenStock(int StockId, int Template, IReadOnlyList<(int Template, int Quantity)> Cost);

/// <summary>
/// Token vendors (TOKENVENDOR conversation actions) from scripts/gen-world.py: pet and mount shops, breeders,
/// exchanges and tier upgrades. Their stock and costs are server-only, so both are <b>approximated</b> from the
/// vendors' titles and the pet/mount item names.
/// </summary>
public sealed class TokenVendorCatalog
{
    public IReadOnlyDictionary<int, (string Title, IReadOnlyList<TokenStock> Stock)> Vendors { get; }

    private TokenVendorCatalog(IReadOnlyDictionary<int, (string, IReadOnlyList<TokenStock>)> vendors) => Vendors = vendors;

    public static TokenVendorCatalog Load(string? worldPath)
    {
        var vendors = new Dictionary<int, (string, IReadOnlyList<TokenStock>)>();
        if (worldPath is not null && File.Exists(worldPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(worldPath));
            if (doc.RootElement.TryGetProperty("tokenVendors", out var all))
                foreach (var v in all.EnumerateObject())
                {
                    var stock = v.Value.GetProperty("stock").EnumerateArray().Select((e, i) => new TokenStock(i + 1, e.GetProperty("template").GetInt32(),
                        e.GetProperty("cost").EnumerateArray().Select(c => (c.GetProperty("template").GetInt32(), c.GetProperty("qty").GetInt32())).ToList())).ToList();
                    vendors[int.Parse(v.Name)] = (v.Value.GetProperty("title").GetString() ?? "", stock);
                }
        }
        return new TokenVendorCatalog(vendors);
    }
}
