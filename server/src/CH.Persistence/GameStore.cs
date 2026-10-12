using Microsoft.Data.Sqlite;

namespace CH.Persistence;

public sealed record Account(long Id, string Username);

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
    ];

    private readonly SqliteConnection _db;

    public GameStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
        Migrate();
    }

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
        using var cmd = Command("SELECT id, username FROM accounts WHERE username = $u", null, ("$u", username));
        using var r = cmd.ExecuteReader();
        r.Read();
        return new Account(r.GetInt64(0), r.GetString(1));
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

    public void Dispose() => _db.Dispose();
}
