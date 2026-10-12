using CH.Persistence;

namespace CH.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ch-store-").FullName;

    private string Db => Path.Combine(_dir, "test.db");

    private static NewCharacter Brigid => new("Brigid", 1, 1, 1, 1, 1, 1, 0, 0, 1f, 1, 2, 2, -109.51f, -3.7f, -16.86f, 0f);

    [Fact]
    public void Migrations_apply_once_and_record_schema_version()
    {
        using (var store = new GameStore(Db)) Assert.Equal(1, store.SchemaVersion);
        using (var again = new GameStore(Db)) Assert.Equal(1, again.SchemaVersion);
    }

    [Fact]
    public void Accounts_are_keyed_by_username_case_insensitively()
    {
        using var store = new GameStore(Db);
        var a = store.GetOrCreateAccount("Alice");
        var b = store.GetOrCreateAccount("alice");
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void Character_names_are_unique()
    {
        using var store = new GameStore(Db);
        var account = store.GetOrCreateAccount("alice");
        store.CreateCharacter(account.Id, Brigid);
        Assert.True(store.CharacterNameTaken("brigid"));
        Assert.ThrowsAny<Exception>(() => store.CreateCharacter(account.Id, Brigid));
    }

    [Fact]
    public void Position_survives_reopening_the_database()
    {
        long id;
        using (var store = new GameStore(Db))
        {
            id = store.CreateCharacter(store.GetOrCreateAccount("alice").Id, Brigid).Id;
            store.SavePosition(id, 2, 10f, 1f, 20f, 0.5f);
        }
        using (var reopened = new GameStore(Db))
        {
            var c = reopened.GetCharacter(id)!;
            Assert.Equal((2, 10f, 1f, 20f, 0.5f), (c.Zone, c.X, c.Y, c.Z, c.Direction));
        }
    }

    [Fact]
    public void Schema_has_no_credential_columns()
    {
        using var store = new GameStore(Db);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_concat(name) FROM pragma_table_info('accounts')";
        var columns = (string)cmd.ExecuteScalar()!;
        Assert.DoesNotContain("pass", columns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("steam", columns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("device", columns, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
