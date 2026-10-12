using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// Our own items (server/content/custom-items.json, in git). Each one clones an existing template ("base": its model,
/// icon, slot and class rules) under a new id and name, with optional stat overrides and a client visual effect
/// ("rainbow"). The same file is what the client mod adds to the client's item list, so both sides agree.
/// </summary>
public sealed record CustomItem(int Id, int Base, string Name, string Description, int? Damage, int? Speed, string? Effect, string? IconColour);

public static class CustomItems
{
    public static IReadOnlyList<CustomItem> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        int? Opt(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(e => new CustomItem(
            e.GetProperty("id").GetInt32(), e.GetProperty("base").GetInt32(), e.GetProperty("name").GetString() ?? "",
            Str(e, "description") ?? "", Opt(e, "damage"), Opt(e, "speed"), Str(e, "effect"), Str(e, "iconColour"))).ToList();
    }

    /// <summary>Adds each custom item to <paramref name="items"/> as a copy of its base template with the overrides.</summary>
    public static void Apply(Dictionary<int, ItemInfo> items, IEnumerable<CustomItem> custom)
    {
        foreach (var c in custom)
        {
            if (!items.TryGetValue(c.Base, out var baseItem)) continue;
            if (items.ContainsKey(c.Id) && items[c.Id].Name != c.Name) throw new InvalidOperationException($"custom item id {c.Id} is taken by {items[c.Id].Name}");
            items[c.Id] = baseItem with
            {
                Id = c.Id,
                Name = c.Name,
                Description = string.IsNullOrEmpty(c.Description) ? baseItem.Description : c.Description,
                Damage = c.Damage ?? baseItem.Damage,
                Speed = c.Speed ?? baseItem.Speed,
            };
        }
    }
}
