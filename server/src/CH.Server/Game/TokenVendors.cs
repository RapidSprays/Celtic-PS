using System.Text.Json;

namespace CH.Server.Game;

/// <summary>One thing a token vendor offers and what it costs (items handed in, e.g. Pet Tokens or two of the same pet).</summary>
public sealed record TokenStock(int StockId, int Template, IReadOnlyList<(int Template, int Quantity)> Cost)
{
    /// <summary>How many of <see cref="Template"/> one purchase gives (a Primal Hellsteed sells for six Dark Essences).</summary>
    public int Quantity { get; init; } = 1;
}

/// <summary>
/// Token vendors (TOKENVENDOR conversation actions) from scripts/gen-world.py: pet and mount shops, breeders,
/// exchanges and tier upgrades. Their stock and costs are server-only, so both are <b>approximated</b> from the
/// vendors' titles and the pet/mount item names.
/// </summary>
public sealed class TokenVendorCatalog
{
    public IReadOnlyDictionary<int, (string Title, IReadOnlyList<TokenStock> Stock)> Vendors { get; }

    private TokenVendorCatalog(IReadOnlyDictionary<int, (string, IReadOnlyList<TokenStock>)> vendors) => Vendors = vendors;

    /// <summary>The orange Pet Token, and the green Uncommon and Rare ones it replaces.</summary>
    public const int PetToken = 56051, UncommonPetToken = 56052, RarePetToken = 56053;

    /// <summary>
    /// Owner's rule (2026-10-16): pet vendors take only the orange Pet Token. The green tokens' prices become Pet Tokens in
    /// the mount vendors' steps (1, 2, 3, 4): 1 Uncommon → 2, 1 Rare → 3, 2 Rare → 4.
    /// </summary>
    public static List<(int Template, int Quantity)> OrangeTokensOnly(IEnumerable<(int Template, int Quantity)> cost) =>
        cost.Select(c => c.Template switch
            {
                UncommonPetToken => (PetToken, c.Quantity * 2),
                RarePetToken => (PetToken, c.Quantity + 2),
                _ => c,
            })
            .GroupBy(c => c.Item1).Select(g => (g.Key, g.Sum(c => c.Item2))).ToList();

    public static TokenVendorCatalog Load(string? worldPath, string? extrasPath = null)
    {
        var vendors = new Dictionary<int, (string, IReadOnlyList<TokenStock>)>();
        // The owner's vendors (server/content/token-vendor-extras.json) over the world data's: ones it lacks (the Hellsteed
        // and Nightmare Steed shops, wolf and bear breeding…) or replaced whole.
        void ReadExtras()
        {
            if (extrasPath is null || !File.Exists(extrasPath)) return;
            using var extras = JsonDocument.Parse(File.ReadAllBytes(extrasPath));
            foreach (var v in extras.RootElement.GetProperty("vendors").EnumerateObject())
            {
                var stock = v.Value.GetProperty("stock").EnumerateArray().Select((e, i) => new TokenStock(i + 1, e.GetProperty("template").GetInt32(),
                        OrangeTokensOnly(e.GetProperty("cost").EnumerateArray().Select(c => (c.GetProperty("template").GetInt32(), c.GetProperty("qty").GetInt32()))))
                    { Quantity = e.TryGetProperty("qty", out var q) ? Math.Max(1, q.GetInt32()) : 1 }).ToList();
                vendors[int.Parse(v.Name)] = (v.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "", stock);
            }
        }
        if (worldPath is not null && File.Exists(worldPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(worldPath));
            if (doc.RootElement.TryGetProperty("tokenVendors", out var all))
                foreach (var v in all.EnumerateObject())
                {
                    var stock = v.Value.GetProperty("stock").EnumerateArray().Select((e, i) => new TokenStock(i + 1, e.GetProperty("template").GetInt32(),
                        OrangeTokensOnly(e.GetProperty("cost").EnumerateArray().Select(c => (c.GetProperty("template").GetInt32(), c.GetProperty("qty").GetInt32()))))).ToList();
                    vendors[int.Parse(v.Name)] = (v.Value.GetProperty("title").GetString() ?? "", stock);
                }
        }
        ReadExtras();
        return new TokenVendorCatalog(vendors);
    }
}
