using CH.Persistence;
using CH.Server;

namespace CH.Tests;

/// <summary><c>CH.Server backup</c>: the background service's daily database copy (no sqlite3 command on Windows).</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ch-backup-").FullName;

    [Fact]
    public void Backup_copies_the_live_database_while_it_is_open()
    {
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        using (var store = new GameStore(Path.Combine(data, "private-server.db")))
        {
            store.GetOrCreateAccount("alice");
            var output = Path.Combine(_dir, "backups", "copy.db");
            Assert.Equal(0, Backup.Run(["--data", data, "--out", output]));

            using var copy = new GameStore(output);
            Assert.Equal(store.GetOrCreateAccount("alice").Id, copy.GetOrCreateAccount("alice").Id);
        }
    }

    [Fact]
    public void Backup_without_a_database_fails()
    {
        Assert.Equal(1, Backup.Run(["--data", _dir, "--out", Path.Combine(_dir, "x.db")]));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
