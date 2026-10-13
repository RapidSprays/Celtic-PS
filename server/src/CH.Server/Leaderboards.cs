using System.Globalization;
using CH.Persistence;

namespace CH.Server;

/// <summary>
/// The leaderboard screen's web service (docs/content-systems-notes.md). The client fetches JSON from the stats URL
/// in the world list (/stats/) and parses it with Unity's JsonUtility, so field names are exact and every container is
/// an object. Rankings come from character columns and the running totals in character_stats; boss boards are empty
/// (no fight records). Each request uses its own database connection.
/// </summary>
public static class Leaderboards
{
    private static readonly Dictionary<int, string> Classes = new() { [1] = "Warrior", [2] = "Druid", [3] = "Mage", [4] = "Ranger", [5] = "Rogue" };

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["ranking_deaths"] = "Deaths", ["ranking_gold"] = "Gold", ["ranking_heal"] = "Healing", ["ranking_kills"] = "Kills",
        ["ranking_melee_hit"] = "Melee Damage", ["ranking_player_level"] = "Level", ["ranking_pve_kills_vs_deaths"] = "Kills per Death",
        ["ranking_pvp_deaths"] = "PvP Deaths", ["ranking_pvp_kills"] = "PvP Kills", ["ranking_pvp_kills_vs_deaths"] = "PvP Kills per Death",
        ["ranking_pvp_ranking"] = "PvP Rank", ["ranking_quests"] = "Quests", ["ranking_skills_hit"] = "Skill Damage",
        ["ranking_player_level_fishing"] = "Fishing Level", ["ranking_player_level_cooking"] = "Cooking Level", ["ranking_competition"] = "Points",
    };

    public static object Global(ServerOptions o) => new
    {
        WorldDictionary = new[] { new { Key = o.WorldId, Value = o.WorldName } },
        ClassDictionary = Classes.Select(c => new { c.Key, c.Value }).ToArray(),
    };

    public static object ClanDict(GameStore store) => new { ClanDictionary = store.ListClans().Select(c => new { Key = (int)c.Id, Value = c.Name }).ToArray() };

    private static IReadOnlyList<(long Id, string Name, int Class, int Level, double Value)> Values(GameStore store, string type)
    {
        IReadOnlyList<(long Id, string Name, int Class, int Level, long Value)> Stat(string stat) => store.StatBoard(stat);
        IEnumerable<(long, string, int, int, double)> Ratio(string kills, string deaths)
        {
            var d = store.StatBoard(deaths).ToDictionary(x => x.Id, x => x.Value);
            return store.StatBoard(kills).Select(k => (k.Id, k.Name, k.Class, k.Level, Math.Round(k.Value / (double)Math.Max(1, d.GetValueOrDefault(k.Id)), 2)));
        }
        var rows = type switch
        {
            "ranking_gold" => store.ColumnBoard("coins").Select(r => (r.Id, r.Name, r.Class, r.Level, (double)r.Value)),
            "ranking_player_level" => store.ColumnBoard("level").Select(r => (r.Id, r.Name, r.Class, r.Level, (double)r.Value)),
            "ranking_player_level_fishing" => store.ColumnBoard("fishing_level").Select(r => (r.Id, r.Name, r.Class, r.Level, (double)r.Value)),
            "ranking_player_level_cooking" => store.ColumnBoard("cooking_level").Select(r => (r.Id, r.Name, r.Class, r.Level, (double)r.Value)),
            "ranking_pvp_ranking" => store.ColumnBoard("pvp_level").Select(r => (r.Id, r.Name, r.Class, r.Level, (double)r.Value)),
            "ranking_pve_kills_vs_deaths" => Ratio("kills", "deaths"),
            "ranking_pvp_kills_vs_deaths" => Ratio("pvp_kills", "pvp_deaths"),
            "ranking_competition" => [],
            _ when type.StartsWith("ranking_", StringComparison.Ordinal) => Stat(type["ranking_".Length..]).Select(r => (r.Id, r.Name, r.Class, r.Level, (double)r.Value)),
            _ => [],
        };
        return rows.Where(r => r.Item5 > 0).ToList();
    }

    public static object Ranking(GameStore store, ServerOptions o, IReadOnlyDictionary<string, string> q)
    {
        var type = q.GetValueOrDefault("type") ?? "ranking_player_level";
        var clans = store.ListClans().ToDictionary(c => c.Id, c => c.Name);
        var classFilter = Int(q, "classId");
        var clanFilter = Int(q, "clanId");
        var rows = Values(store, type)
            .Select(r => (Row: r, Clan: store.GetMembership(r.Id)?.ClanId))
            .Where(r => classFilter is null or <= 0 || r.Row.Class == classFilter)
            .Where(r => clanFilter is null or <= 0 || r.Clan == clanFilter)
            .OrderByDescending(r => r.Row.Value).ThenBy(r => r.Row.Name, StringComparer.OrdinalIgnoreCase).ToList();
        object Entry((long Id, string Name, int Class, int Level, double Value) r, long? clan, int rank) => new
        {
            Index = rank, Rank = rank, WorldId = o.WorldId, WorldName = o.WorldName, CharacterId = (int)r.Id,
            ClanId = (int)(clan ?? -1), ClanName = clan is { } c ? clans.GetValueOrDefault(c, "") : "", Name = r.Name, Level = r.Level,
            ClassId = r.Class, ClassName = Classes.GetValueOrDefault(r.Class, ""), RankingValue = r.Value,
        };
        var ranked = rows.Select((r, i) => (r.Row, r.Clan, Rank: i + 1)).ToList();
        var (sortCol, dir) = (q.GetValueOrDefault("sortCol") ?? "ranking_value", q.GetValueOrDefault("sortDir") ?? "NONE");
        var byRank = sortCol is "ranking_value" or "rank";
        var sorted = sortCol switch
        {
            "name" => ranked.OrderBy(r => r.Row.Name, StringComparer.OrdinalIgnoreCase),
            "level" => ranked.OrderBy(r => r.Row.Level),
            "class_name" => ranked.OrderBy(r => r.Row.Class),
            "clan_name" => ranked.OrderBy(r => r.Clan is { } c ? clans.GetValueOrDefault(c, "") : ""),
            _ => ranked.OrderBy(r => r.Rank),
        };
        var ordered = (byRank ? dir == "ASC" : dir == "DESC") ? sorted.Reverse() : sorted;
        var (page, size) = (Math.Max(1, Int(q, "page") ?? 1), Math.Clamp(Int(q, "numRecs") ?? 20, 1, 100));
        var me = ranked.FirstOrDefault(r => r.Row.Id == (Int(q, "charId") ?? -1));
        return new
        {
            RankingDataList = ordered.Skip((page - 1) * size).Take(size).Select(r => Entry(r.Row, r.Clan, r.Rank)).ToArray(),
            PlayerRankingData = me.Rank > 0 ? Entry(me.Row, me.Clan, me.Rank) : Entry((0, "", 0, 0, 0), null, 0),
            TotalResults = ranked.Count,
            ExtraColumn = Labels.GetValueOrDefault(type, type.StartsWith("ranking_star_", StringComparison.Ordinal) ? $"{type[^1]}-Star Kills" : "Value"),
            IsDataFromCache = false,
        };
    }

    public static object Clans(GameStore store, ServerOptions o, IReadOnlyDictionary<string, string> q)
    {
        var type = q.GetValueOrDefault("type") ?? "total_levels_30d";
        var rows = store.ListClans().Select(c =>
        {
            var members = store.ListClanMembers(c.Id);
            var levels = members.Sum(m => store.GetProgress(m.CharacterId).Level);
            return (c.Id, c.Name, Members: members.Count, Levels: levels);
        }).OrderByDescending(c => type.StartsWith("total_members", StringComparison.Ordinal) ? c.Members : c.Levels).ToList();
        object Entry((long Id, string Name, int Members, int Levels) c, int rank) => new
        {
            Index = rank, Rank = rank, WorldId = o.WorldId, WorldName = o.WorldName, ClanId = (int)c.Id, ClanName = c.Name,
            c.Members, TotalLevels = c.Levels, TotalLevels30D = c.Levels, TotalMembers7D = c.Members, TotalMembers30D = c.Members,
            RankingValue = type.StartsWith("total_members", StringComparison.Ordinal) ? c.Members : c.Levels,
        };
        var mine = Int(q, "charId") is { } id ? store.GetMembership(id)?.ClanId : null;
        var mineIndex = rows.FindIndex(r => r.Id == mine);
        var (page, size) = (Math.Max(1, Int(q, "page") ?? 1), Math.Clamp(Int(q, "numRecs") ?? 20, 1, 100));
        return new
        {
            TheClanDataList = rows.Select((c, i) => Entry(c, i + 1)).Skip((page - 1) * size).Take(size).ToArray(),
            PlayerTheClanData = mineIndex >= 0 ? Entry(rows[mineIndex], mineIndex + 1) : Entry((0, "", 0, 0), 0),
            TotalResults = rows.Count,
            ExtraColumn = type.StartsWith("total_members", StringComparison.Ordinal) ? "Members" : "Total Levels",
            IsDataFromCache = false,
        };
    }

    /// <summary>Boss boards: no fight records are kept, so every board is empty (with a "no rank" player row).</summary>
    public static object Boss(IReadOnlyDictionary<string, string> q) => (q.GetValueOrDefault("board") ?? "general") switch
    {
        "fight" => new { DetailsPerPlayer = Array.Empty<object>(), TotalResults = 0, DropItemsIds = Array.Empty<int>(), ExtraColumn = "", IsDataFromCache = false },
        _ => new { Data = Array.Empty<object>(), PlayerData = new { Rank = 0, Name = "" }, TotalRecords = 0, ExtraColumn = "", IsDataFromCache = false },
    };

    private static int? Int(IReadOnlyDictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
