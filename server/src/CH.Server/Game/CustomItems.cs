using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// Our own items (server/content/custom-items.json, in git). Each one clones an existing template ("base": its model,
/// icon, slot and class rules) under a new id and name, with optional stat overrides and a client visual effect
/// ("rainbow"). The same file is what the client mod adds to the client's item list, so both sides agree.
/// </summary>
public sealed record CustomItem(int Id, int Base, string Name, string Description, int? Damage, int? Speed, string? Effect, string? IconColour)
{
    /// <summary>Item whose combat stats and requirements are copied (e.g. a level 230 weapon).</summary>
    public int? Stats { get; init; }

    /// <summary>New model id when the item gets its own model (base model plus particle effects; drawn by the client).</summary>
    public int? ModelId { get; init; }
}

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
            Str(e, "description") ?? "", Opt(e, "damage"), Opt(e, "speed"), Str(e, "effect"), Str(e, "iconColour"))
        {
            Stats = Opt(e, "stats"),
            ModelId = e.TryGetProperty("model", out var model) ? Opt(model, "id") : null,
        }).ToList();
    }

    /// <summary>Adds each custom item to <paramref name="items"/> as a copy of its base template with the overrides.</summary>
    public static void Apply(Dictionary<int, ItemInfo> items, IEnumerable<CustomItem> custom)
    {
        foreach (var c in custom)
        {
            if (!items.TryGetValue(c.Base, out var baseItem)) continue;
            if (c.Stats is { } donorId && items.TryGetValue(donorId, out var donor))
                baseItem = baseItem with
                {
                    Damage = donor.Damage, Speed = donor.Speed, Range = donor.Range, Armour = donor.Armour, Weight = donor.Weight,
                    Classes = donor.Classes, Bonuses = donor.Bonuses, Requirements = donor.Requirements, StatBonuses = donor.StatBonuses,
                    AbilityBonuses = donor.AbilityBonuses, AvoidanceBonuses = donor.AvoidanceBonuses, SkillBonuses = donor.SkillBonuses,
                    ProcSkillId = donor.ProcSkillId, ProcSkillLevel = donor.ProcSkillLevel, ProcSkillChance = donor.ProcSkillChance,
                    EquipSkillId = donor.EquipSkillId, StarLevel = donor.StarLevel, Tradeable = donor.Tradeable,
                };
            if (items.ContainsKey(c.Id) && items[c.Id].Name != c.Name) throw new InvalidOperationException($"custom item id {c.Id} is taken by {items[c.Id].Name}");
            items[c.Id] = baseItem with
            {
                Id = c.Id,
                Name = c.Name,
                Description = string.IsNullOrEmpty(c.Description) ? baseItem.Description : c.Description,
                Damage = c.Damage ?? baseItem.Damage,
                Speed = c.Speed ?? baseItem.Speed,
                ModelId = c.ModelId ?? baseItem.ModelId,
            };
        }
    }
}
