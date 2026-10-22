using System.Text.Json;

namespace CH.Server.Game;

/// <summary>
/// The owner's spawn odds and respawn times (server/content/spawn-tuning.json): a weight multiplier and respawn time per
/// monster, a respawn time per spawn point, and whether spawn points without one use their live respawn time (Master's
/// Grimoire's spawnpoints.txt, written as spawn-respawns.json beside the balance JSON) or the default.
/// </summary>
public sealed class SpawnTuning
{
    private readonly Dictionary<int, double> _weights = new();
    private readonly Dictionary<int, double> _monsterRespawn = new();
    private readonly Dictionary<int, double> _pointRespawn = new();
    private readonly Dictionary<int, (double Min, double Max)> _live = new();
    private readonly Dictionary<int, List<(int Item, double Chance, int Quantity)>> _extraDrops = new();
    private readonly Dictionary<int, double> _noDropChance = new();
    private readonly Dictionary<int, HashSet<int>> _removeDrops = new();
    private readonly Dictionary<int, double> _chestChance = new();
    private readonly Dictionary<int, List<string>> _chests = new();
    private List<string> _eventChests = [];
    private readonly HashSet<int> _divine = new();
    private readonly Dictionary<int, float> _pointY = new();
    private readonly Dictionary<int, float> _pointScale = new();
    private readonly HashSet<int> _startHighTier = new();
    private readonly HashSet<int> _startAnyTier = new();
    private bool _useLive;
    private double _default = 45;

    public static SpawnTuning None { get; } = new();

    public static SpawnTuning Load(string? path, string? liveRespawnsPath)
    {
        var t = new SpawnTuning();
        if (path is not null && File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.TryGetProperty("useLiveRespawnTimes", out var live)) t._useLive = live.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("defaultRespawnSeconds", out var d) && d.ValueKind == JsonValueKind.Number) t._default = Math.Max(1, d.GetDouble());
            if (root.TryGetProperty("eventChests", out var ec)) t._eventChests = Names(ec);
            if (root.TryGetProperty("monsters", out var monsters))
                foreach (var m in monsters.EnumerateArray())
                {
                    if (!m.TryGetProperty("id", out var id)) continue;
                    if (m.TryGetProperty("weightMultiplier", out var w) && w.ValueKind == JsonValueKind.Number) t._weights[id.GetInt32()] = Math.Max(0, w.GetDouble());
                    if (m.TryGetProperty("respawnSeconds", out var r) && r.ValueKind == JsonValueKind.Number) t._monsterRespawn[id.GetInt32()] = Math.Max(1, r.GetDouble());
                    if (m.TryGetProperty("divine", out var dv) && dv.ValueKind == JsonValueKind.True) t._divine.Add(id.GetInt32());
                    if (m.TryGetProperty("noDropChance", out var nd) && nd.ValueKind == JsonValueKind.Number) t._noDropChance[id.GetInt32()] = Math.Clamp(nd.GetDouble(), 0, 100);
                    if (m.TryGetProperty("removeDrops", out var rd) && rd.ValueKind == JsonValueKind.Array)
                        t._removeDrops[id.GetInt32()] = rd.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) ? i : e)
                            .Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToHashSet();
                    if (m.TryGetProperty("chestChance", out var cc) && cc.ValueKind == JsonValueKind.Number) t._chestChance[id.GetInt32()] = Math.Clamp(cc.GetDouble(), 0, 100);
                    if (m.TryGetProperty("chests", out var ch) && Names(ch) is { Count: > 0 } own) t._chests[id.GetInt32()] = own;
                    if (m.TryGetProperty("extraDrops", out var x) && x.ValueKind == JsonValueKind.Array)
                        foreach (var drop in x.EnumerateArray())
                            if (drop.TryGetProperty("id", out var item) && drop.TryGetProperty("chance", out var chance) && chance.ValueKind == JsonValueKind.Number)
                                (t._extraDrops.TryGetValue(id.GetInt32(), out var list) ? list : t._extraDrops[id.GetInt32()] = [])
                                    .Add((item.GetInt32(), Math.Clamp(chance.GetDouble(), 0, 100),
                                        drop.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? Math.Max(1, q.GetInt32()) : 1));
                }
            if (root.TryGetProperty("spawnPoints", out var points))
                foreach (var p in points.EnumerateArray())
                {
                    if (!p.TryGetProperty("spawnId", out var id)) continue;
                    if (p.TryGetProperty("respawnSeconds", out var r) && r.ValueKind == JsonValueKind.Number) t._pointRespawn[id.GetInt32()] = Math.Max(1, r.GetDouble());
                    if (p.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number) t._pointY[id.GetInt32()] = y.GetSingle();
                    if (p.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number) t._pointScale[id.GetInt32()] = Math.Max(0.01f, sc.GetSingle());
                    if (p.TryGetProperty("startHighTier", out var hi) && hi.ValueKind == JsonValueKind.True) t._startHighTier.Add(id.GetInt32());
                    if (p.TryGetProperty("startAnyTier", out var any) && any.ValueKind == JsonValueKind.True) t._startAnyTier.Add(id.GetInt32());
                }
        }
        if (liveRespawnsPath is not null && File.Exists(liveRespawnsPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(liveRespawnsPath));
            foreach (var p in doc.RootElement.GetProperty("respawns").EnumerateObject())
                if (int.TryParse(p.Name, out var id)) t._live[id] = (p.Value[0].GetDouble(), p.Value[1].GetDouble());
        }
        return t;
    }

    /// <summary>
    /// The owner's extra drops for a kill of this monster. Each is rolled at its percent chance; or, when the monster has a
    /// noDropChance, at most one: that percent gives nothing, otherwise one of the list picked with the chances as weights.
    /// </summary>
    public IEnumerable<(int Item, int Quantity)> RollExtraDrops(int templateId, Random rng)
    {
        if (!_extraDrops.TryGetValue(templateId, out var list)) yield break;
        if (_noDropChance.TryGetValue(templateId, out var none))
        {
            if (rng.NextDouble() * 100 < none) yield break;
            var total = list.Sum(d => d.Chance);
            if (total <= 0) yield break;
            var roll = rng.NextDouble() * total;
            foreach (var (item, chance, quantity) in list)
            {
                roll -= chance;
                if (roll < 0)
                {
                    yield return (item, quantity);
                    yield break;
                }
            }
            yield break;
        }
        foreach (var (item, chance, quantity) in list)
            if (rng.NextDouble() * 100 < chance) yield return (item, quantity);
    }

    /// <summary>Item ids or names in a JSON list (numbers and strings both allowed).</summary>
    private static List<string> Names(JsonElement e) => e.ValueKind != JsonValueKind.Array ? []
        : e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32().ToString() : x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "")
            .Where(x => x.Length > 0).ToList();

    /// <summary>
    /// The chests a monster's chestChance picks from (ids or names): its own "chests", else the file's "eventChests";
    /// empty → every event Mystery Chest.
    /// </summary>
    public IReadOnlyList<string> ChestChoices(int templateId) => _chests.TryGetValue(templateId, out var own) ? own : _eventChests;

    /// <summary>Whether the owner took this item out of the monster's official loot (spawn-tuning.json "removeDrops").</summary>
    public bool Removed(int templateId, int item) => _removeDrops.TryGetValue(templateId, out var set) && set.Contains(item);

    /// <summary>Whether this kill drops a random event chest (spawn-tuning.json "chestChance", a percent).</summary>
    public bool RollChest(int templateId, Random rng) => _chestChance.TryGetValue(templateId, out var c) && rng.NextDouble() * 100 < c;

    /// <summary>
    /// Whether a monster takes Divine Strike, the set weapons' proc and the set auras' strike (owner's rule, 2026-10-17:
    /// only the monsters spawn-tuning.json marks "divine": true).
    /// </summary>
    public bool TakesDivine(int templateId) => _divine.Contains(templateId);

    /// <summary>Whether a spawn point starts with a random 4, 5 or 6 star at every server start (spawnPoints "startHighTier").</summary>
    public bool StartHighTier(int? spawnPointId) => spawnPointId is { } id && _startHighTier.Contains(id);

    /// <summary>Whether a spawn point starts with any of its stars at an equal chance, placeholders included ("startAnyTier").</summary>
    public bool StartAnyTier(int? spawnPointId) => spawnPointId is { } id && _startAnyTier.Contains(id);

    /// <summary>The owner's height for a spawn point (a monster stuck in the floor or the ceiling), else the data's.</summary>
    public float Y(int? spawnPointId, float y) => spawnPointId is { } id && _pointY.TryGetValue(id, out var own) ? own : y;

    /// <summary>The owner's model size for whatever spawns at a spawn point; null → the appearance's (mob-scales.json).</summary>
    public float? Scale(int? spawnPointId) => spawnPointId is { } id && _pointScale.TryGetValue(id, out var s) ? s : null;

    /// <summary>A monster's weight at a spawn point, with the owner's multiplier.</summary>
    public double Weight(int templateId, double weight) => _weights.TryGetValue(templateId, out var m) ? weight * m : weight;

    /// <summary>
    /// Seconds until a generated spawn point comes back after <paramref name="templateId"/> died there: the spawn point's
    /// own value, else the monster's, else its live time (a random point in the live range) when that's switched on, else
    /// the default.
    /// </summary>
    public double RespawnSeconds(int templateId, int? spawnPointId, Random rng)
    {
        if (spawnPointId is { } sp && _pointRespawn.TryGetValue(sp, out var point)) return point;
        if (_monsterRespawn.TryGetValue(templateId, out var monster)) return monster;
        if (_useLive && spawnPointId is { } id && _live.TryGetValue(id, out var live)) return live.Min + rng.NextDouble() * (live.Max - live.Min);
        return _default;
    }
}
