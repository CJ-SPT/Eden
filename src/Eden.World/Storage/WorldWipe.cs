using Eden.World.Modding;
using Eden.World.Runtime;
using Microsoft.Data.Sqlite;

namespace Eden.World.Storage;

// This operates only on detached, final checkpoint images after the resident host
// has stopped successfully. Ordinary gameplay never enters this storage boundary.
public static class WorldWipe
{
    private sealed record CheckpointTrigger(string Name, string Sql);

    private static readonly HashSet<string> RetainedTables = new(StringComparer.Ordinal)
    {
        "WorldAccount",
        "LauncherCredential",
        "WorldConfigurationRevision",
        "WorldConfigurationState",
        "WorldEconomy",
        "WorldEconomicPolicy",
        "WorldAdjustedPrice",
        "WorldManufacturingIndex",
    };

    public static void Execute(string worldPath, string backupDirectory, bool reconfigure = false, long? configurationGeneration = null) =>
        Execute(worldPath, backupDirectory, LiveWorld.PersistSnapshot, reconfigure, configurationGeneration);

    internal static void Execute(
        string worldPath,
        string backupDirectory,
        Action<SqliteConnection, string> persist,
        bool reconfigure = false,
        long? configurationGeneration = null
    )
    {
        worldPath = Path.GetFullPath(worldPath);
        backupDirectory = Path.GetFullPath(backupDirectory);
        if (LiveWorld.Find(worldPath) is not null)
        {
            throw new InvalidOperationException("Stop the resident world before wiping its final checkpoint.");
        }
        using FileStream ownership = new(worldPath + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(backupDirectory) || File.Exists(backupDirectory))
        {
            throw new IOException("World wipe requires a new backup directory.");
        }
        using var world = ReadCheckpoint(worldPath);
        if (configurationGeneration is long expected)
        {
            using var command = world.CreateCommand();
            command.CommandText = "SELECT Generation FROM WorldConfigurationState WHERE Id=1";
            if (!Equals(command.ExecuteScalar(), expected))
            {
                throw new InvalidOperationException(
                    "The world settings changed after wipe confirmation. Review the revision and try again."
                );
            }
        }
        Directory.CreateDirectory(backupDirectory);
        string modsPath = ModDbContext.FilePath(worldPath);
        using var mods = File.Exists(modsPath) ? ReadCheckpoint(modsPath) : null;
        string worldBackup = Path.Combine(backupDirectory, "world.sqlite");
        string modsBackup = Path.Combine(backupDirectory, "world.sqlite.mods.sqlite");
        // Both complete, flushed, verified backups must exist before modifying either image.
        Backup(world, worldBackup, persist);
        if (mods is not null)
        {
            Backup(mods, modsBackup, persist);
        }
        File.WriteAllText(
            Path.Combine(backupDirectory, "restore.txt"),
            $"Stop Eden before restoring. Copy world.sqlite to {worldPath}.{Environment.NewLine}"
                + $"If present, copy world.sqlite.mods.sqlite to {modsPath}.{Environment.NewLine}"
                + "Keep the matching catalog and installed mod packages. The backup contains account password hashes."
                + Environment.NewLine
        );

        ClearWorld(world);
        if (reconfigure)
        {
            using WorldDbContext replacement = new(world);
            WorldSetupStore.RequireReconfiguration(replacement);
        }
        if (mods is not null)
        {
            // Empty mod IDs hold deployment identity; named mods own disposable world state.
            Run(mods, "DELETE FROM ModState WHERE ModId <> ''; VACUUM;");
            Verify(mods);
        }
        bool modsReplaced = false;
        try
        {
            if (mods is not null)
            {
                persist(mods, modsPath);
                modsReplaced = true;
            }
            persist(world, worldPath);
        }
        catch
        {
            if (modsReplaced)
            {
                using var originalMods = ReadCheckpoint(modsBackup);
                LiveWorld.PersistSnapshot(originalMods, modsPath);
            }
            throw;
        }
    }

    private static SqliteConnection ReadCheckpoint(string path)
    {
        using SqliteConnection source = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        source.Open();
        SqliteConnection image = new("Data Source=:memory:;Pooling=False;Foreign Keys=False");
        try
        {
            image.Open();
            source.BackupDatabase(image);
            Verify(image);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static void Backup(SqliteConnection image, string path, Action<SqliteConnection, string> persist)
    {
        persist(image, path);
        using var verified = ReadCheckpoint(path);
    }

    private static void ClearWorld(SqliteConnection image)
    {
        List<string> tables = [];
        List<CheckpointTrigger> triggers = [];
        using (var query = image.CreateCommand())
        {
            query.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                tables.Add(rows.GetString(0));
            }
        }
        if (!RetainedTables.IsSubsetOf(tables))
        {
            throw new InvalidDataException("The world checkpoint is missing required account or configuration tables.");
        }
        using (var query = image.CreateCommand())
        {
            query.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='trigger'";
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                triggers.Add(new(rows.GetString(0), rows.GetString(1)));
            }
        }
        // Row-level cleanup triggers can insert invalidation records into tables
        // already cleared. Suspend them in the detached image and restore unchanged.
        foreach (var trigger in triggers)
        {
            Run(image, "DROP TRIGGER " + Identifier(trigger.Name));
        }
        // Foreign keys are off only in this disposable image to handle ownership cycles.
        // Retain sequence high-water marks for tables using allocated identities.
        foreach (string table in tables.Where(table => !RetainedTables.Contains(table)))
        {
            Run(image, "DELETE FROM " + Identifier(table));
        }
        foreach (var trigger in triggers)
        {
            Run(image, trigger.Sql);
        }
        Run(image, "PRAGMA foreign_keys=ON; VACUUM;");
        Verify(image);
    }

    private static string Identifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void Verify(SqliteConnection image)
    {
        using var check = image.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The world wipe checkpoint failed its integrity check.");
        }
        check.CommandText = "PRAGMA foreign_key_check";
        using var violations = check.ExecuteReader();
        if (violations.Read())
        {
            throw new InvalidDataException("The world wipe checkpoint contains broken references.");
        }
    }

    private static void Run(SqliteConnection image, string sql)
    {
        using var command = image.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
