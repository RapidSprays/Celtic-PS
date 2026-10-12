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
        using (var store = new GameStore(Db)) Assert.Equal(6, store.SchemaVersion);
        using (var again = new GameStore(Db)) Assert.Equal(6, again.SchemaVersion);
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
    public void Kit_is_granted_once_and_items_and_skills_persist()
    {
        using var store = new GameStore(Db);
        var id = store.CreateCharacter(store.GetOrCreateAccount("alice").Id, Brigid).Id;
        Assert.True(store.GrantKit(id, [(10000, 1, 0), (10040, 5, -1)], [1, 47], 50, "hud"));
        Assert.False(store.GrantKit(id, [(10000, 1, 0)], [1], 50, "hud"));
        Assert.Equal(2, store.ListItems(id).Count);
        Assert.Equal([(1, 0), (47, 0)], store.ListSkills(id));
        Assert.Equal(50, store.GetProgress(id).Coins);
        Assert.True(store.LearnSkill(id, 2));
        Assert.False(store.LearnSkill(id, 2));
        Assert.Equal(2, store.ListSkills(id)[^1].SkillId);
        var potion = store.ListItems(id).Single(i => i.TemplateId == 10040);
        store.SetItemQuantity(potion.Id, 0);
        Assert.Single(store.ListItems(id));
    }

    [Fact]
    public void Spawns_are_stored_per_zone_and_deletable()
    {
        using var store = new GameStore(Db);
        var npc = store.AddSpawn(new SpawnRecord(0, 2, 9, -100f, -3f, -10f, 90f, 100, 0, 11, 60, 0, "alice"));
        store.AddSpawn(new SpawnRecord(0, 3, 70003, 0, 0, 0, 0, 0, 2, -1, 60, 5, "alice"));
        Assert.Equal(npc, Assert.Single(store.ListSpawns(2)));
        Assert.True(store.DeleteSpawn(npc.Id));
        Assert.Empty(store.ListSpawns(2));
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
