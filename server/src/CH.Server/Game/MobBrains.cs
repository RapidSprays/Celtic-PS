using System.Text.Json;

namespace CH.Server.Game;

/// <summary>One skill in a monster's skill set: weight within its kind (0 attack, 1 heal, 2 buff, 3 debuff) and its rank.</summary>
public sealed record MobSkillChoice(int SkillId, int Weight, int Kind, int Rank);

/// <summary>
/// A monster AI profile (ai.txt): how often it chooses each action, by weight. Kinds: 1 auto-attack, 2 attack skill,
/// 3 debuff skill, 4 heal itself (below <see cref="SelfHealBelow"/>% health), 5 buff, 6 heal another.
/// </summary>
public sealed record MobAi(int SelfHealBelow, IReadOnlyList<(int Kind, int Weight)> Decisions);

/// <summary>What a monster can cast: its AI, its skill set and its ability levels (the skill formula's ability).</summary>
public sealed record MobBrain(MobAi Ai, IReadOnlyList<MobSkillChoice> Skills, IReadOnlyDictionary<int, int> Abilities);

/// <summary>
/// Monsters' skills and AI from Master's Grimoire (scripts/import-grimoire.py writes mob-skills.json beside the balance
/// JSON). Monsters without an entry keep the old occasional themed special attack.
/// </summary>
public sealed class MobBrains
{
    public const int AutoAttack = 1, AttackSkill = 2, DebuffSkill = 3, HealSelf = 4;
    public const int KindAttack = 0, KindHeal = 1, KindDebuff = 3;

    private readonly Dictionary<int, MobBrain> _mobs;

    private MobBrains(Dictionary<int, MobBrain> mobs) => _mobs = mobs;

    public static MobBrains Empty { get; } = new([]);

    public int Count => _mobs.Count;

    public bool TryGet(int mobTemplate, out MobBrain brain) => _mobs.TryGetValue(mobTemplate, out brain!);

    public static MobBrains Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Empty;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var ais = doc.RootElement.GetProperty("ai").EnumerateObject().ToDictionary(e => e.Name, e => new MobAi(
            e.Value.GetProperty("selfHeal").GetInt32(),
            e.Value.GetProperty("decisions").EnumerateArray().Select(d => (d[0].GetInt32(), d[1].GetInt32())).ToList()));
        var mobs = new Dictionary<int, MobBrain>();
        foreach (var m in doc.RootElement.GetProperty("mobs").EnumerateObject())
        {
            if (!ais.TryGetValue(m.Value.GetProperty("ai").GetString() ?? "", out var ai)) continue;
            mobs[int.Parse(m.Name)] = new MobBrain(ai,
                m.Value.GetProperty("skills").EnumerateArray().Select(s => new MobSkillChoice(s[0].GetInt32(), s[1].GetInt32(), s[2].GetInt32(), s[3].GetInt32())).ToList(),
                m.Value.GetProperty("abilities").EnumerateObject().ToDictionary(a => int.Parse(a.Name), a => a.Value.GetInt32()));
        }
        return new MobBrains(mobs);
    }
}
