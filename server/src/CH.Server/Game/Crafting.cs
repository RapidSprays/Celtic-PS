using System.Text.Json;

namespace CH.Server.Game;

/// <summary>Fishing and cooking levels (<b>our own</b> curve: the official one is server-only). Both cap at 200.</summary>
public static class Crafting
{
    public const int MaxLevel = 200;

    public static int XpToNext(int level) => (int)(40 + 12 * Math.Pow(Math.Max(1, level), 1.4));

    public static float Percent(int level, int xp) => level >= MaxLevel ? 0f : Math.Clamp(xp * 100f / XpToNext(level), 0f, 100f);

    /// <summary>Adds XP; returns the new level and leftover XP.</summary>
    public static (int Level, int Xp) Add(int level, int xp, int gained)
    {
        level = Math.Max(1, level);
        xp = Math.Max(0, xp + gained);
        while (level < MaxLevel && xp >= XpToNext(level))
        {
            xp -= XpToNext(level);
            level++;
        }
        return (level, level >= MaxLevel ? 0 : xp);
    }
}

/// <summary>A zone's fishing spots and catches (scripts/gen-fishing.py → fishing.json beside the balance JSON).</summary>
public sealed record FishingZone(int ZoneId, int MinLevel, int MaxLevel, IReadOnlyList<(float X, float Y, float Z)> Spots,
    IReadOnlyList<int> Templates, IReadOnlyList<int> RareTemplates, IReadOnlyList<int> Common, IReadOnlyList<int> Rare,
    IReadOnlyList<int> Relics, IReadOnlyList<int> Junk, IReadOnlyList<int> Clams)
{
    public static IReadOnlyDictionary<int, FishingZone> Load(string? path)
    {
        var zones = new Dictionary<int, FishingZone>();
        if (path is null || !File.Exists(path)) return zones;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        static List<int> Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToList();
        foreach (var z in doc.RootElement.GetProperty("zones").EnumerateObject())
        {
            var e = z.Value;
            var c = e.GetProperty("catches");
            zones[int.Parse(z.Name)] = new FishingZone(int.Parse(z.Name), e.GetProperty("minLevel").GetInt32(), e.GetProperty("maxLevel").GetInt32(),
                e.GetProperty("spots").EnumerateArray().Select(p => (p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(), p.GetProperty("z").GetSingle())).ToList(),
                Ints(e.GetProperty("templates")), Ints(e.GetProperty("rareTemplates")), Ints(c.GetProperty("common")), Ints(c.GetProperty("rare")),
                Ints(c.GetProperty("relics")), Ints(c.GetProperty("junk")), Ints(c.GetProperty("clams")));
        }
        return zones;
    }
}
