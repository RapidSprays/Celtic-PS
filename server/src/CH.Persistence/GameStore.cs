using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CH.Persistence;

public sealed record Account(long Id, string Username, bool IsGm = false);

/// <summary>One stage of a quest in progress: done, active, and its counter (kills, items, ...).</summary>
public sealed record QuestStageRecord(bool Done, bool Active, int Sum);

/// <summary>A character's quest: in progress, or completed (stages then don't matter).</summary>
public sealed record QuestRecord(int QuestId, bool Completed, IReadOnlyList<QuestStageRecord> Stages, bool Tracked);

/// <summary>Outcome of checking a login secret against an account.</summary>
public enum PasswordCheck
{
    /// <summary>The account had no password: this one now claims it.</summary>
    Claimed,
    Matched,
    Wrong,
}

public sealed record CharacterRecord(
    long Id,
    long AccountId,
    string Name,
    int Race,
    int Face,
    int Skin,
    int SkinColour,
    int Hair,
    int HairColour,
    int FaceAccessory,
    int FaceAccessoryColour,
    float Scale,
    byte Gender,
    int Class,
    int Level,
    int Zone,
    float X,
    float Y,
    float Z,
    float Direction);

/// <summary>An item owned by a character. <see cref="Slot"/> is the equipment slot (0-24), or -1 for the backpack.</summary>
/// <summary>One entry on a character's daily bounty board (BountyBoard.Bounty.tStatus values in Status).</summary>
public sealed record BountyRecord(int Day, int Slot, int QuestId, int Cost, int Status);

/// <summary>An auction house listing; the item is held here (taken from the seller) until it sells, expires or is cancelled.</summary>
public sealed record AuctionRecord(long Id, long SellerId, string SellerName, int TemplateId, int Quantity, int Charges, int StartBid, int Buyout,
    int Bid, long? BidderId, string? BidderName, int Deposit, long ExpiresAt);

public sealed record ItemRecord(long Id, long CharacterId, int TemplateId, int Quantity, int Slot, bool Bound, int Charges, bool Favourite)
{
    public const int Backpack = -1;

    /// <summary>Personal bank (vault) storage.</summary>
    public const int Bank = -2;
}

/// <summary>Progress and vitals. Health/energy of -1 mean "full"; attributes of -1 mean "race base, nothing spent".</summary>
public sealed record CharacterProgress(int Level, int Xp, int Coins, int Health, int Energy, string Hud, bool KitGranted,
    int Strength = -1, int Dexterity = -1, int Focus = -1, int Vitality = -1, int Platinum = 0,
    bool ShowHeadgear = true, bool ShowFashion = true);

/// <summary>A hand-placed (GM) spawn point. Opinion below 50 is hostile; NPCs use 100 with level 0.</summary>
public sealed record SpawnRecord(
    long Id,
    int Zone,
    int TemplateId,
    float X,
    float Y,
    float Z,
    float Direction,
    int Opinion,
    int Level,
    int ConversationId,
    int RespawnSeconds,
    float RoamRadius,
    string CreatedBy);

public sealed record NewCharacter(
    string Name,
    int Race,
    int Face,
    int Skin,
    int SkinColour,
    int Hair,
    int HairColour,
    int FaceAccessory,
    int FaceAccessoryColour,
    float Scale,
    byte Gender,
    int Class,
    int Zone,
    float X,
    float Y,
    float Z,
    float Direction);

/// <summary>
/// SQLite persistence for the private server. Accounts are keyed by username only: the private server never
/// stores passwords, password hashes, device ids or Steam ids.
/// </summary>
public sealed class GameStore : IDisposable
{
    // Numbered migrations. Append only; never edit an applied entry.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE accounts (
            id         INTEGER PRIMARY KEY,
            username   TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE characters (
            id            INTEGER PRIMARY KEY,
            account_id    INTEGER NOT NULL REFERENCES accounts(id),
            name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
            race          INTEGER NOT NULL,
            face          INTEGER NOT NULL,
            skin          INTEGER NOT NULL,
            skin_colour   INTEGER NOT NULL,
            hair          INTEGER NOT NULL,
            hair_colour   INTEGER NOT NULL,
            face_acc      INTEGER NOT NULL,
            face_acc_col  INTEGER NOT NULL,
            scale         REAL NOT NULL,
            gender        INTEGER NOT NULL,
            class         INTEGER NOT NULL,
            level         INTEGER NOT NULL DEFAULT 1,
            zone          INTEGER NOT NULL,
            x             REAL NOT NULL,
            y             REAL NOT NULL,
            z             REAL NOT NULL,
            direction     REAL NOT NULL,
            created_at    TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX characters_account ON characters(account_id);
        """,
        """
        ALTER TABLE characters ADD COLUMN xp INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE characters ADD COLUMN coins INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE characters ADD COLUMN health INTEGER NOT NULL DEFAULT -1;
        ALTER TABLE characters ADD COLUMN energy INTEGER NOT NULL DEFAULT -1;
        ALTER TABLE characters ADD COLUMN hud TEXT NOT NULL DEFAULT '';
        ALTER TABLE characters ADD COLUMN kit_granted INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE items (
            id           INTEGER PRIMARY KEY,
            character_id INTEGER NOT NULL REFERENCES characters(id),
            template_id  INTEGER NOT NULL,
            quantity     INTEGER NOT NULL DEFAULT 1,
            slot         INTEGER NOT NULL DEFAULT -1,
            bound        INTEGER NOT NULL DEFAULT 0,
            charges      INTEGER NOT NULL DEFAULT 0,
            favourite    INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX items_character ON items(character_id);
        CREATE TABLE skills (
            character_id INTEGER NOT NULL REFERENCES characters(id),
            skill_id     INTEGER NOT NULL,
            level        INTEGER NOT NULL DEFAULT 0,
            position     INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (character_id, skill_id)
        );
        """,
        """
        CREATE TABLE spawns (
            id              INTEGER PRIMARY KEY,
            zone            INTEGER NOT NULL,
            template_id     INTEGER NOT NULL,
            x               REAL NOT NULL,
            y               REAL NOT NULL,
            z               REAL NOT NULL,
            direction       REAL NOT NULL DEFAULT 0,
            opinion         INTEGER NOT NULL,
            level           INTEGER NOT NULL,
            conversation_id INTEGER NOT NULL DEFAULT -1,
            respawn_seconds INTEGER NOT NULL DEFAULT 60,
            roam_radius     REAL NOT NULL DEFAULT 0,
            created_by      TEXT NOT NULL DEFAULT '',
            created_at      TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX spawns_zone ON spawns(zone);
        """,
        """
        ALTER TABLE characters ADD COLUMN strength INTEGER NOT NULL DEFAULT -1;
        ALTER TABLE characters ADD COLUMN dexterity INTEGER NOT NULL DEFAULT -1;
        ALTER TABLE characters ADD COLUMN focus INTEGER NOT NULL DEFAULT -1;
        ALTER TABLE characters ADD COLUMN vitality INTEGER NOT NULL DEFAULT -1;
        """,
        """
        ALTER TABLE characters ADD COLUMN platinum INTEGER NOT NULL DEFAULT 0;
        """,
        """
        ALTER TABLE characters ADD COLUMN show_headgear INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE characters ADD COLUMN show_fashion INTEGER NOT NULL DEFAULT 1;
        """,
        """
        CREATE TABLE teleports (
            character_id    INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            spawn_point_id  INTEGER NOT NULL,
            PRIMARY KEY (character_id, spawn_point_id)
        );
        """,
        """
        ALTER TABLE accounts ADD COLUMN password_hash BLOB;
        ALTER TABLE accounts ADD COLUMN password_salt BLOB;
        ALTER TABLE accounts ADD COLUMN is_gm INTEGER NOT NULL DEFAULT 0;
        """,
        """
        CREATE TABLE quests (
            character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            quest_id     INTEGER NOT NULL,
            completed    INTEGER NOT NULL DEFAULT 0,
            stages       TEXT NOT NULL DEFAULT '[]',
            tracked      INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (character_id, quest_id)
        );
        """,
        """
        CREATE TABLE abilities (
            character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            ability_id   INTEGER NOT NULL,
            PRIMARY KEY (character_id, ability_id)
        );
        """,
        """
        ALTER TABLE characters ADD COLUMN pet_fed_effect INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE characters ADD COLUMN pet_fed_level INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE characters ADD COLUMN pet_fed_seconds INTEGER NOT NULL DEFAULT 0;
        """,
        """
        CREATE TABLE recipes (
            character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            recipe_id    INTEGER NOT NULL,
            PRIMARY KEY (character_id, recipe_id)
        );
        """,
        """
        CREATE TABLE tree_nodes (
            character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            node_id      INTEGER NOT NULL,
            PRIMARY KEY (character_id, node_id)
        );
        """,
        """
        ALTER TABLE characters ADD COLUMN sigils_health INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE characters ADD COLUMN sigils_energy INTEGER NOT NULL DEFAULT 0;
        """,
        """
        CREATE TABLE ability_levels (
            character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            ability_id   INTEGER NOT NULL,
            level        INTEGER NOT NULL,
            PRIMARY KEY (character_id, ability_id)
        );
        """,
        """
        ALTER TABLE characters ADD COLUMN backpack_expansions INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE characters ADD COLUMN bank_expansions INTEGER NOT NULL DEFAULT 0;
        """,
        """
        ALTER TABLE characters ADD COLUMN auction_slots INTEGER NOT NULL DEFAULT 0;
        """,
        """
        CREATE TABLE faction_points (
            character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            faction_id   INTEGER NOT NULL,
            points       INTEGER NOT NULL,
            PRIMARY KEY (character_id, faction_id)
        );
        CREATE TABLE bounties (
            character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            ancient      INTEGER NOT NULL,
            day          INTEGER NOT NULL,
            slot         INTEGER NOT NULL,
            quest_id     INTEGER NOT NULL,
            cost         INTEGER NOT NULL,
            status       INTEGER NOT NULL,
            PRIMARY KEY (character_id, ancient, slot)
        );
        CREATE TABLE auctions (
            id          INTEGER PRIMARY KEY,
            seller_id   INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
            template_id INTEGER NOT NULL,
            quantity    INTEGER NOT NULL,
            charges     INTEGER NOT NULL,
            start_bid   INTEGER NOT NULL,
            buyout      INTEGER NOT NULL,
            bid         INTEGER NOT NULL DEFAULT 0,
            bidder_id   INTEGER,
            deposit     INTEGER NOT NULL,
            expires_at  INTEGER NOT NULL
        );
        """,
    ];

    private const int PasswordIterations = 100_000;

    private readonly SqliteConnection _db;

    public GameStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
        Migrate();
    }

    /// <summary>The schema version a fully migrated database has.</summary>
    public static int LatestSchemaVersion => Migrations.Length;

    public int SchemaVersion => Convert.ToInt32(Scalar("PRAGMA user_version"));

    private void Migrate()
    {
        for (var v = SchemaVersion; v < Migrations.Length; v++)
        {
            using var tx = _db.BeginTransaction();
            Exec(Migrations[v], tx);
            Exec($"PRAGMA user_version = {v + 1}", tx);
            tx.Commit();
        }
    }

    public Account GetOrCreateAccount(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("username is required", nameof(username));
        Exec("INSERT INTO accounts(username) VALUES ($u) ON CONFLICT(username) DO NOTHING", null, ("$u", username));
        using var cmd = Command("SELECT id, username, is_gm FROM accounts WHERE username = $u", null, ("$u", username));
        using var r = cmd.ExecuteReader();
        r.Read();
        return new Account(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0);
    }

    /// <summary>
    /// Checks the login secret the client sends (already a hash on the client side) against the account's stored
    /// PBKDF2-SHA256 hash of it. An account without a password is claimed by the first secret it sees.
    /// </summary>
    public PasswordCheck CheckPassword(long accountId, string secret)
    {
        using (var cmd = Command("SELECT password_hash, password_salt FROM accounts WHERE id = $id", null, ("$id", accountId)))
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read() && !r.IsDBNull(0))
            {
                var stored = (byte[])r[0];
                var hash = Rfc2898DeriveBytes.Pbkdf2(secret, (byte[])r[1], PasswordIterations, HashAlgorithmName.SHA256, stored.Length);
                return CryptographicOperations.FixedTimeEquals(hash, stored) ? PasswordCheck.Matched : PasswordCheck.Wrong;
            }
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        var claimed = Rfc2898DeriveBytes.Pbkdf2(secret, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        Exec("UPDATE accounts SET password_hash = $h, password_salt = $s WHERE id = $id", null, ("$h", claimed), ("$s", salt), ("$id", accountId));
        return PasswordCheck.Claimed;
    }

    /// <summary>Forgets an account's password, so its next login sets a new one. False if there is no such account.</summary>
    public bool ResetPassword(string username) =>
        ExecCount("UPDATE accounts SET password_hash = NULL, password_salt = NULL WHERE username = $u", ("$u", username)) == 1;

    public bool SetGm(string username, bool isGm) =>
        ExecCount("UPDATE accounts SET is_gm = $g WHERE username = $u", ("$g", isGm ? 1 : 0), ("$u", username)) == 1;

    private int ExecCount(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = Command(sql, null, args);
        return cmd.ExecuteNonQuery();
    }

    public bool CharacterNameTaken(string name) =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM characters WHERE name = $n", ("$n", name))) > 0;

    public CharacterRecord CreateCharacter(long accountId, NewCharacter c)
    {
        using var cmd = Command(
            """
            INSERT INTO characters(account_id, name, race, face, skin, skin_colour, hair, hair_colour, face_acc, face_acc_col,
                                   scale, gender, class, zone, x, y, z, direction)
            VALUES ($a, $n, $race, $face, $skin, $skinc, $hair, $hairc, $facc, $faccc, $scale, $g, $cls, $zone, $x, $y, $z, $d)
            RETURNING id
            """, null,
            ("$a", accountId), ("$n", c.Name), ("$race", c.Race), ("$face", c.Face), ("$skin", c.Skin), ("$skinc", c.SkinColour),
            ("$hair", c.Hair), ("$hairc", c.HairColour), ("$facc", c.FaceAccessory), ("$faccc", c.FaceAccessoryColour),
            ("$scale", c.Scale), ("$g", c.Gender), ("$cls", c.Class), ("$zone", c.Zone), ("$x", c.X), ("$y", c.Y), ("$z", c.Z), ("$d", c.Direction));
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return GetCharacter(id)!;
    }

    public IReadOnlyList<CharacterRecord> ListCharacters(long accountId) =>
        QueryCharacters("WHERE account_id = $a ORDER BY id", ("$a", accountId));

    public CharacterRecord? GetCharacter(long id) =>
        QueryCharacters("WHERE id = $id", ("$id", id)).FirstOrDefault();

    public void SavePosition(long characterId, int zone, float x, float y, float z, float direction) =>
        Exec("UPDATE characters SET zone = $zone, x = $x, y = $y, z = $z, direction = $d WHERE id = $id", null,
            ("$zone", zone), ("$x", x), ("$y", y), ("$z", z), ("$d", direction), ("$id", characterId));

    public CharacterProgress GetProgress(long characterId)
    {
        using var cmd = Command("SELECT level, xp, coins, health, energy, hud, kit_granted, strength, dexterity, focus, vitality, platinum, show_headgear, show_fashion FROM characters WHERE id = $id", null, ("$id", characterId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"character {characterId} not found");
        return new CharacterProgress(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetInt32(6) == 1,
            r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12) == 1, r.GetInt32(13) == 1);
    }

    public void SaveProgress(long characterId, int level, int xp, int coins) =>
        Exec("UPDATE characters SET level = $l, xp = $x, coins = $c WHERE id = $id", null, ("$l", level), ("$x", xp), ("$c", coins), ("$id", characterId));

    public void SaveVitals(long characterId, int health, int energy) =>
        Exec("UPDATE characters SET health = $h, energy = $e WHERE id = $id", null, ("$h", health), ("$e", energy), ("$id", characterId));

    public void SaveDisplayOptions(long characterId, bool showHeadgear, bool showFashion) =>
        Exec("UPDATE characters SET show_headgear = $h, show_fashion = $f WHERE id = $id", null,
            ("$h", showHeadgear ? 1 : 0), ("$f", showFashion ? 1 : 0), ("$id", characterId));

    /// <summary>Adds coins and platinum (either may be zero).</summary>
    public void AddCurrency(long characterId, int coins, int platinum) =>
        Exec("UPDATE characters SET coins = coins + $c, platinum = platinum + $p WHERE id = $id", null, ("$c", coins), ("$p", platinum), ("$id", characterId));

    public void SaveAttributes(long characterId, int strength, int dexterity, int focus, int vitality) =>
        Exec("UPDATE characters SET strength = $s, dexterity = $d, focus = $f, vitality = $v WHERE id = $id", null,
            ("$s", strength), ("$d", dexterity), ("$f", focus), ("$v", vitality), ("$id", characterId));

    public void SetSkillLevel(long characterId, int skillId, int level) =>
        Exec("UPDATE skills SET level = $l WHERE character_id = $c AND skill_id = $s", null, ("$l", level), ("$c", characterId), ("$s", skillId));

    public void SaveHud(long characterId, string hud) =>
        Exec("UPDATE characters SET hud = $h WHERE id = $id", null, ("$h", hud), ("$id", characterId));

    /// <summary>Gives a character its starting kit once, in one transaction.</summary>
    public bool GrantKit(long characterId, IEnumerable<(int TemplateId, int Quantity, int Slot)> items, IEnumerable<int> skills, int coins, string hud)
    {
        using var tx = _db.BeginTransaction();
        using (var check = Command("SELECT kit_granted FROM characters WHERE id = $id", tx, ("$id", characterId)))
            if (Convert.ToInt32(check.ExecuteScalar()) == 1) return false;
        foreach (var (template, qty, slot) in items)
            Exec("INSERT INTO items(character_id, template_id, quantity, slot, bound) VALUES ($c, $t, $q, $s, 1)", tx,
                ("$c", characterId), ("$t", template), ("$q", qty), ("$s", slot));
        var position = 0;
        foreach (var skill in skills)
            Exec("INSERT OR IGNORE INTO skills(character_id, skill_id, level, position) VALUES ($c, $s, 0, $p)", tx,
                ("$c", characterId), ("$s", skill), ("$p", position++));
        Exec("UPDATE characters SET kit_granted = 1, coins = coins + $coins, hud = $hud WHERE id = $id", tx,
            ("$coins", coins), ("$hud", hud), ("$id", characterId));
        tx.Commit();
        return true;
    }

    public IReadOnlyList<ItemRecord> ListItems(long characterId)
    {
        using var cmd = Command("SELECT id, character_id, template_id, quantity, slot, bound, charges, favourite FROM items WHERE character_id = $c ORDER BY id", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<ItemRecord>();
        while (r.Read())
            list.Add(new ItemRecord(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5) == 1, r.GetInt32(6), r.GetInt32(7) == 1));
        return list;
    }

    public long AddItem(long characterId, int templateId, int quantity, int slot = ItemRecord.Backpack)
    {
        using var cmd = Command("INSERT INTO items(character_id, template_id, quantity, slot) VALUES ($c, $t, $q, $s) RETURNING id", null,
            ("$c", characterId), ("$t", templateId), ("$q", quantity), ("$s", slot));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void SetItemSlot(long itemId, int slot) => Exec("UPDATE items SET slot = $s WHERE id = $id", null, ("$s", slot), ("$id", itemId));

    public void SetItemQuantity(long itemId, int quantity)
    {
        if (quantity <= 0) Exec("DELETE FROM items WHERE id = $id", null, ("$id", itemId));
        else Exec("UPDATE items SET quantity = $q WHERE id = $id", null, ("$q", quantity), ("$id", itemId));
    }

    /// <summary>Gives a whole item row (keeping its charges and binding) to another character's backpack.</summary>
    public void GiveItem(long itemId, long toCharacterId) =>
        Exec("UPDATE items SET character_id = $c, slot = $s, favourite = 0 WHERE id = $id", null,
            ("$c", toCharacterId), ("$s", ItemRecord.Backpack), ("$id", itemId));

    /// <summary>Runs <paramref name="work"/> as one SQLite transaction: all of it or none of it.</summary>
    public void Atomically(Action work)
    {
        Exec("BEGIN IMMEDIATE");
        try
        {
            work();
            Exec("COMMIT");
        }
        catch
        {
            Exec("ROLLBACK");
            throw;
        }
    }

    public void SetItemFavourite(long itemId, bool favourite) =>
        Exec("UPDATE items SET favourite = $f WHERE id = $id", null, ("$f", favourite ? 1 : 0), ("$id", itemId));

    public IReadOnlyList<(int SkillId, int Level)> ListSkills(long characterId)
    {
        using var cmd = Command("SELECT skill_id, level FROM skills WHERE character_id = $c ORDER BY position, skill_id", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<(int, int)>();
        while (r.Read()) list.Add((r.GetInt32(0), r.GetInt32(1)));
        return list;
    }

    public bool LearnSkill(long characterId, int skillId)
    {
        using var cmd = Command(
            "INSERT OR IGNORE INTO skills(character_id, skill_id, level, position) " +
            "VALUES ($c, $s, 0, (SELECT COALESCE(MAX(position), -1) + 1 FROM skills WHERE character_id = $c))", null,
            ("$c", characterId), ("$s", skillId));
        return cmd.ExecuteNonQuery() == 1;
    }

    public SpawnRecord AddSpawn(SpawnRecord s)
    {
        using var cmd = Command(
            """
            INSERT INTO spawns(zone, template_id, x, y, z, direction, opinion, level, conversation_id, respawn_seconds, roam_radius, created_by)
            VALUES ($zone, $t, $x, $y, $z, $d, $o, $l, $c, $r, $roam, $by) RETURNING id
            """, null,
            ("$zone", s.Zone), ("$t", s.TemplateId), ("$x", s.X), ("$y", s.Y), ("$z", s.Z), ("$d", s.Direction), ("$o", s.Opinion),
            ("$l", s.Level), ("$c", s.ConversationId), ("$r", s.RespawnSeconds), ("$roam", s.RoamRadius), ("$by", s.CreatedBy));
        return s with { Id = Convert.ToInt64(cmd.ExecuteScalar()) };
    }

    public IReadOnlyList<QuestRecord> ListQuests(long characterId)
    {
        using var cmd = Command("SELECT quest_id, completed, stages, tracked FROM quests WHERE character_id = $c ORDER BY quest_id", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<QuestRecord>();
        while (r.Read())
        {
            var stages = System.Text.Json.JsonSerializer.Deserialize<int[][]>(r.GetString(2)) ?? [];
            list.Add(new QuestRecord(r.GetInt32(0), r.GetInt64(1) != 0,
                stages.Select(s => new QuestStageRecord(s[0] != 0, s[1] != 0, s[2])).ToList(), r.GetInt64(3) != 0));
        }
        return list;
    }

    public void SaveQuest(long characterId, QuestRecord quest)
    {
        var stages = System.Text.Json.JsonSerializer.Serialize(quest.Stages.Select(s => new[] { s.Done ? 1 : 0, s.Active ? 1 : 0, s.Sum }));
        Exec("""
            INSERT INTO quests(character_id, quest_id, completed, stages, tracked) VALUES ($c, $q, $done, $s, $t)
            ON CONFLICT(character_id, quest_id) DO UPDATE SET completed = $done, stages = $s, tracked = $t
            """, null, ("$c", characterId), ("$q", quest.QuestId), ("$done", quest.Completed ? 1 : 0), ("$s", stages), ("$t", quest.Tracked ? 1 : 0));
    }

    /// <summary>The pet's Well Fed effect and how much feeding time is left (it only runs while online).</summary>
    public void SavePetFeed(long characterId, int effect, int level, int seconds) =>
        Exec("UPDATE characters SET pet_fed_effect = $e, pet_fed_level = $l, pet_fed_seconds = $s WHERE id = $id", null,
            ("$e", effect), ("$l", level), ("$s", seconds), ("$id", characterId));

    public (int Effect, int Level, int Seconds) GetPetFeed(long characterId)
    {
        using var cmd = Command("SELECT pet_fed_effect, pet_fed_level, pet_fed_seconds FROM characters WHERE id = $id", null, ("$id", characterId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)) : (0, 0, 0);
    }

    /// <summary>Cooking recipes learnt from recipe books. True if it was new.</summary>
    public bool LearnRecipe(long characterId, int recipeId)
    {
        using var cmd = Command("INSERT OR IGNORE INTO recipes(character_id, recipe_id) VALUES ($c, $r)", null, ("$c", characterId), ("$r", recipeId));
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<int> ListRecipes(long characterId)
    {
        using var cmd = Command("SELECT recipe_id FROM recipes WHERE character_id = $c ORDER BY recipe_id", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<int>();
        while (r.Read()) list.Add(r.GetInt32(0));
        return list;
    }

    /// <summary>Abilities learnt from trainers (LEARN_ABILITY).</summary>
    /// <summary>Backpack and Bank Expansions applied to the character (they never sit in the inventory).</summary>
    public (int Backpack, int Bank) GetExpansions(long characterId)
    {
        using var cmd = Command("SELECT backpack_expansions, bank_expansions FROM characters WHERE id = $c", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
    }

    public int GetAuctionSlots(long characterId) =>
        Convert.ToInt32(Scalar("SELECT auction_slots FROM characters WHERE id = $c", ("$c", characterId)) ?? 0);

    public void AddAuctionSlots(long characterId, int slots) =>
        Exec("UPDATE characters SET auction_slots = auction_slots + $n WHERE id = $c", null, ("$c", characterId), ("$n", slots));

    public void AddExpansions(long characterId, int backpack, int bank) =>
        Exec("UPDATE characters SET backpack_expansions = backpack_expansions + $p, bank_expansions = bank_expansions + $b WHERE id = $c", null,
            ("$c", characterId), ("$p", backpack), ("$b", bank));

    /// <summary>Barbershop: face, skin colour, hair, hair colour, face accessory and its colour, height.</summary>
    public void UpdateAppearance(long characterId, int face, int skinColour, int hair, int hairColour, int faceAccessory, int faceAccessoryColour, float scale) =>
        Exec("UPDATE characters SET face = $f, skin_colour = $sc, hair = $h, hair_colour = $hc, face_acc = $fa, face_acc_col = $fac, scale = $s WHERE id = $c", null,
            ("$c", characterId), ("$f", face), ("$sc", skinColour), ("$h", hair), ("$hc", hairColour), ("$fa", faceAccessory), ("$fac", faceAccessoryColour), ("$s", scale));

    /// <summary>Deletes a character and, through the foreign keys, everything that belongs to it.</summary>
    public bool DeleteCharacter(long accountId, long characterId)
    {
        using var tx = _db.BeginTransaction();
        using (var check = Command("SELECT COUNT(*) FROM characters WHERE id = $c AND account_id = $a", tx, ("$c", characterId), ("$a", accountId)))
            if (Convert.ToInt32(check.ExecuteScalar()) != 1) return false;
        // items and skills predate ON DELETE CASCADE; the other tables cascade.
        Exec("DELETE FROM items WHERE character_id = $c", tx, ("$c", characterId));
        Exec("DELETE FROM skills WHERE character_id = $c", tx, ("$c", characterId));
        Exec("DELETE FROM characters WHERE id = $c", tx, ("$c", characterId));
        tx.Commit();
        return true;
    }

    /// <summary>Sigils of Health and Energy applied to the character (they never sit in the inventory).</summary>
    public (int Health, int Energy) GetSigils(long characterId)
    {
        using var cmd = Command("SELECT sigils_health, sigils_energy FROM characters WHERE id = $c", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
    }

    public void AddSigils(long characterId, int health, int energy) =>
        Exec("UPDATE characters SET sigils_health = sigils_health + $h, sigils_energy = sigils_energy + $e WHERE id = $c", null,
            ("$c", characterId), ("$h", health), ("$e", energy));

    /// <summary>Passive-tree nodes the character has taken (docs/passive-tree.md).</summary>
    public IReadOnlyList<int> ListTreeNodes(long characterId)
    {
        using var cmd = Command("SELECT node_id FROM tree_nodes WHERE character_id = $c ORDER BY node_id", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<int>();
        while (r.Read()) list.Add(r.GetInt32(0));
        return list;
    }

    public bool AddTreeNode(long characterId, int nodeId)
    {
        using var cmd = Command("INSERT OR IGNORE INTO tree_nodes(character_id, node_id) VALUES ($c, $n)", null, ("$c", characterId), ("$n", nodeId));
        return cmd.ExecuteNonQuery() == 1;
    }

    public void RemoveTreeNode(long characterId, int nodeId) =>
        Exec("DELETE FROM tree_nodes WHERE character_id = $c AND node_id = $n", null, ("$c", characterId), ("$n", nodeId));

    public void ResetTree(long characterId) => Exec("DELETE FROM tree_nodes WHERE character_id = $c", null, ("$c", characterId));

    public IReadOnlyList<int> ListAbilities(long characterId)
    {
        using var cmd = Command("SELECT ability_id FROM abilities WHERE character_id = $c ORDER BY ability_id", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<int>();
        while (r.Read()) list.Add(r.GetInt32(0));
        return list;
    }

    /// <summary>Ability levels raised by use (abilities not listed are at level 1).</summary>
    public IReadOnlyDictionary<int, int> ListAbilityLevels(long characterId)
    {
        using var cmd = Command("SELECT ability_id, level FROM ability_levels WHERE character_id = $c", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var levels = new Dictionary<int, int>();
        while (r.Read()) levels[r.GetInt32(0)] = r.GetInt32(1);
        return levels;
    }

    public void SetAbilityLevel(long characterId, int abilityId, int level) =>
        Exec("INSERT INTO ability_levels(character_id, ability_id, level) VALUES ($c, $a, $l) ON CONFLICT(character_id, ability_id) DO UPDATE SET level = $l",
            null, ("$c", characterId), ("$a", abilityId), ("$l", level));

    public void LearnAbility(long characterId, int abilityId) =>
        Exec("INSERT OR IGNORE INTO abilities(character_id, ability_id) VALUES ($c, $a)", null, ("$c", characterId), ("$a", abilityId));

    public void DeleteQuest(long characterId, int questId) =>
        Exec("DELETE FROM quests WHERE character_id = $c AND quest_id = $q", null, ("$c", characterId), ("$q", questId));

    /// <summary>Leystones (PlayerSpawnPoint ids) the character has discovered, for fast travel.</summary>
    public IReadOnlyList<int> ListTeleports(long characterId)
    {
        using var cmd = Command("SELECT spawn_point_id FROM teleports WHERE character_id = $c ORDER BY spawn_point_id", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<int>();
        while (r.Read()) list.Add(r.GetInt32(0));
        return list;
    }

    /// <summary>Records a discovered leystone; false when it was already known.</summary>
    public bool AddTeleport(long characterId, int spawnPointId)
    {
        using var cmd = Command("INSERT OR IGNORE INTO teleports (character_id, spawn_point_id) VALUES ($c, $p)", null,
            ("$c", characterId), ("$p", spawnPointId));
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<SpawnRecord> ListSpawns(int zone)
    {
        using var cmd = Command(
            "SELECT id, zone, template_id, x, y, z, direction, opinion, level, conversation_id, respawn_seconds, roam_radius, created_by " +
            "FROM spawns WHERE zone = $z ORDER BY id", null, ("$z", zone));
        using var r = cmd.ExecuteReader();
        var list = new List<SpawnRecord>();
        while (r.Read())
            list.Add(new SpawnRecord(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetFloat(3), r.GetFloat(4), r.GetFloat(5), r.GetFloat(6),
                r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetFloat(11), r.GetString(12)));
        return list;
    }

    public bool DeleteSpawn(long id)
    {
        using var cmd = Command("DELETE FROM spawns WHERE id = $id", null, ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    private List<CharacterRecord> QueryCharacters(string where, params (string, object)[] args)
    {
        using var cmd = Command(
            "SELECT id, account_id, name, race, face, skin, skin_colour, hair, hair_colour, face_acc, face_acc_col, " +
            "scale, gender, class, level, zone, x, y, z, direction FROM characters " + where, null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<CharacterRecord>();
        while (r.Read())
        {
            list.Add(new CharacterRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetFloat(11), (byte)r.GetInt32(12),
                r.GetInt32(13), r.GetInt32(14), r.GetInt32(15), r.GetFloat(16), r.GetFloat(17), r.GetFloat(18), r.GetFloat(19)));
        }
        return list;
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private void Exec(string sql, SqliteTransaction? tx = null, params (string, object)[] args)
    {
        using var cmd = Command(sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object)[] args)
    {
        using var cmd = Command(sql, null, args);
        return cmd.ExecuteScalar();
    }

    // ---- factions, bounty boards, auction house (GameServer.Factions/Bounties/Auctions) ----------------------------

    public IReadOnlyDictionary<int, int> GetFactions(long characterId)
    {
        using var cmd = Command("SELECT faction_id, points FROM faction_points WHERE character_id = $c", null, ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<int, int>();
        while (r.Read()) map[r.GetInt32(0)] = r.GetInt32(1);
        return map;
    }

    public void SetFaction(long characterId, int factionId, int points) =>
        Exec("INSERT INTO faction_points(character_id, faction_id, points) VALUES ($c, $f, $p) ON CONFLICT(character_id, faction_id) DO UPDATE SET points = $p",
            null, ("$c", characterId), ("$f", factionId), ("$p", points));

    public IReadOnlyList<BountyRecord> ListBounties(long characterId, bool ancient)
    {
        using var cmd = Command("SELECT day, slot, quest_id, cost, status FROM bounties WHERE character_id = $c AND ancient = $a ORDER BY slot", null,
            ("$c", characterId), ("$a", ancient ? 1 : 0));
        using var r = cmd.ExecuteReader();
        var list = new List<BountyRecord>();
        while (r.Read()) list.Add(new BountyRecord(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
        return list;
    }

    /// <summary>Replaces the character's board (one kind) with a new day's bounties.</summary>
    public void ReplaceBounties(long characterId, bool ancient, IEnumerable<BountyRecord> bounties)
    {
        using var tx = _db.BeginTransaction();
        Exec("DELETE FROM bounties WHERE character_id = $c AND ancient = $a", tx, ("$c", characterId), ("$a", ancient ? 1 : 0));
        foreach (var b in bounties)
            Exec("INSERT INTO bounties(character_id, ancient, day, slot, quest_id, cost, status) VALUES ($c, $a, $d, $s, $q, $k, $st)", tx,
                ("$c", characterId), ("$a", ancient ? 1 : 0), ("$d", b.Day), ("$s", b.Slot), ("$q", b.QuestId), ("$k", b.Cost), ("$st", b.Status));
        tx.Commit();
    }

    public void SetBountyStatus(long characterId, bool ancient, int slot, int status) =>
        Exec("UPDATE bounties SET status = $st WHERE character_id = $c AND ancient = $a AND slot = $s", null,
            ("$c", characterId), ("$a", ancient ? 1 : 0), ("$s", slot), ("$st", status));

    public long AddAuction(AuctionRecord a)
    {
        using var cmd = Command("""
            INSERT INTO auctions(seller_id, template_id, quantity, charges, start_bid, buyout, deposit, expires_at)
            VALUES ($s, $t, $q, $ch, $sb, $bo, $d, $e) RETURNING id
            """, null, ("$s", a.SellerId), ("$t", a.TemplateId), ("$q", a.Quantity), ("$ch", a.Charges), ("$sb", a.StartBid), ("$bo", a.Buyout),
            ("$d", a.Deposit), ("$e", a.ExpiresAt));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IReadOnlyList<AuctionRecord> ListAuctions()
    {
        using var cmd = Command("""
            SELECT a.id, a.seller_id, s.name, a.template_id, a.quantity, a.charges, a.start_bid, a.buyout, a.bid, a.bidder_id, b.name, a.deposit, a.expires_at
            FROM auctions a JOIN characters s ON s.id = a.seller_id LEFT JOIN characters b ON b.id = a.bidder_id ORDER BY a.id
            """, null);
        using var r = cmd.ExecuteReader();
        var list = new List<AuctionRecord>();
        while (r.Read())
            list.Add(new AuctionRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7),
                r.GetInt32(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt32(11), r.GetInt64(12)));
        return list;
    }

    public void SetAuctionBid(long auctionId, long bidderId, int bid) =>
        Exec("UPDATE auctions SET bidder_id = $b, bid = $v WHERE id = $id", null, ("$id", auctionId), ("$b", bidderId), ("$v", bid));

    public void DeleteAuction(long auctionId) => Exec("DELETE FROM auctions WHERE id = $id", null, ("$id", auctionId));

    public void Dispose() => _db.Dispose();
}
