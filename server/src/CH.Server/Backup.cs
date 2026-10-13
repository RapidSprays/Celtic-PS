using Microsoft.Data.Sqlite;

namespace CH.Server;

/// <summary><c>backup --data DIR --out FILE</c>: SQLite's online backup of <c>private-server.db</c>, consistent even while the server writes.</summary>
public static class Backup
{
    public static int Run(string[] args)
    {
        string? data = null, output = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data": data = args[++i]; break;
                case "--out": output = args[++i]; break;
                default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
            }
        }
        if (data is null || output is null)
        {
            Console.Error.WriteLine("usage: CH.Server backup --data DIR --out FILE");
            return 2;
        }
        var source = Path.Combine(Path.GetFullPath(data), "private-server.db");
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"no database at {source}");
            return 1;
        }
        Copy(source, Path.GetFullPath(output));
        return 0;
    }

    public static void Copy(string source, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = output, Pooling = false }.ToString());
        from.Open();
        to.Open();
        from.BackupDatabase(to);
    }
}
