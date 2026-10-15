using System.Text.Json;

namespace CH.Server.Game;

/// <summary>An equipment set: wearing <see cref="Needed"/> of its pieces grants the reward item's bonus.</summary>
/// <remarks><see cref="Effect"/> is the client's status effect for it ("Greythorn Aura"), shown as an icon under the health bar.</remarks>
public sealed record ItemSet(int Id, IReadOnlySet<int> Pieces, int Needed, int Reward, int? Effect);

/// <summary>An armour set aura with the owner's values (server/content/set-bonuses.json), matched on the reward item's name.</summary>
public sealed record SetAura(string Name, string Reward, int Health, int Energy, int Divine);

/// <summary>
/// Set bonuses. Which pieces make a set comes from Master's Grimoire (item-sets.json beside the balance JSON, written
/// by scripts/import-grimoire.py); the reward is an unequippable item whose bonuses the client data holds. Armour set
/// auras listed in server/content/set-bonuses.json use the owner's health, energy and Divine Strike values instead.
/// </summary>
public sealed class SetBonusCatalog
{
    /// <summary>Divine Strike (skill 2200): "Proc additional damage against bosses that take Divine damage."</summary>
    public const int DivineStrikeSkill = 2200;

    public static SetBonusCatalog Empty { get; } = new([], [], 0);

    public IReadOnlyList<ItemSet> Sets { get; }
    public IReadOnlyList<SetAura> Auras { get; }
    public double DivineStrikeChance { get; }

    private SetBonusCatalog(IReadOnlyList<ItemSet> sets, IReadOnlyList<SetAura> auras, double divineStrikeChance)
    {
        Sets = sets;
        Auras = auras;
        DivineStrikeChance = divineStrikeChance;
    }

    public static SetBonusCatalog Load(string? setsPath, string? aurasPath)
    {
        var sets = new List<ItemSet>();
        if (setsPath is not null && File.Exists(setsPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(setsPath));
            foreach (var s in doc.RootElement.GetProperty("sets").EnumerateArray())
                sets.Add(new ItemSet(s.GetProperty("id").GetInt32(), s.GetProperty("pieces").EnumerateArray().Select(p => p.GetInt32()).ToHashSet(),
                    s.GetProperty("needed").GetInt32(), s.GetProperty("reward").GetInt32(),
                    s.TryGetProperty("effect", out var fx) && fx.ValueKind == JsonValueKind.Number ? fx.GetInt32() : null));
        }
        var auras = new List<SetAura>();
        var chance = 0.05;
        if (aurasPath is not null && File.Exists(aurasPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(aurasPath));
            if (doc.RootElement.TryGetProperty("divineStrikeChance", out var c)) chance = c.GetDouble();
            foreach (var a in doc.RootElement.GetProperty("auras").EnumerateArray())
                auras.Add(new SetAura(a.GetProperty("name").GetString() ?? "", a.GetProperty("reward").GetString() ?? "",
                    a.GetProperty("health").GetInt32(), a.GetProperty("energy").GetInt32(), a.GetProperty("divine").GetInt32()));
        }
        // Longest reward name first, so "Exalted Dragon Armour…" never matches "Dragon Armour…".
        return new SetBonusCatalog(sets, auras.OrderByDescending(a => a.Reward.Length).ToList(), chance);
    }

    /// <summary>Every set the equipped items complete, one per reward.</summary>
    public IEnumerable<ItemSet> ActiveSets(IReadOnlyCollection<int> equipped) =>
        Sets.Where(s => equipped.Distinct().Count(s.Pieces.Contains) >= s.Needed).DistinctBy(s => s.Reward);

    /// <summary>The owner's aura for a reward item, by name ("Warden Armour Set Bonus Ranger" → Warden's Aura).</summary>
    public SetAura? AuraFor(ItemInfo reward) => Auras.FirstOrDefault(a => reward.Name.StartsWith(a.Reward, StringComparison.OrdinalIgnoreCase));
}
