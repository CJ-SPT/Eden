using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class InventoryOwner
{
    public long Id { get; set; }
}

internal static class InventoryOwnershipSchema
{
    internal static void Register(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldInventoryOwner(Id INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO WorldInventoryOwner SELECT Id FROM WorldPilot;
            INSERT OR IGNORE INTO WorldInventoryOwner SELECT Id FROM WorldCorporation;
            CREATE TRIGGER IF NOT EXISTS RegisterPilotInventoryOwner AFTER INSERT ON WorldPilot
            BEGIN INSERT OR IGNORE INTO WorldInventoryOwner(Id) VALUES(NEW.Id); END;
            CREATE TRIGGER IF NOT EXISTS RegisterCorporationInventoryOwner AFTER INSERT ON WorldCorporation
            BEGIN INSERT OR IGNORE INTO WorldInventoryOwner(Id) VALUES(NEW.Id); END;
            """
        );
    }

    internal static void Upgrade(WorldDbContext world)
    {
        // SQLite cannot change a foreign-key target in place. Keep enforcement off
        // only on this held startup connection while both tables rebuild atomically.
        world.Database.OpenConnection();
        try
        {
            world.Database.ExecuteSqlRaw("PRAGMA foreign_keys=OFF");
            // Other tables have fitting triggers referencing these inventories.
            // They remain intact while each replacement takes the original name.
            world.Database.ExecuteSqlRaw("PRAGMA legacy_alter_table=ON");
            using var transaction = world.Database.BeginTransaction();
            Register(world);
            var ownerTables = world
                .Database.SqlQueryRaw<string>(
                    "SELECT \"table\" AS Value FROM pragma_foreign_key_list('WorldItem') WHERE \"from\"='OwnerId'"
                )
                .ToArray();
            var shipOwnerTables = world
                .Database.SqlQueryRaw<string>(
                    "SELECT \"table\" AS Value FROM pragma_foreign_key_list('WorldShip') WHERE \"from\"='OwnerId'"
                )
                .ToArray();
            if (
                ownerTables is not (["WorldPilot"] or ["WorldInventoryOwner"])
                || shipOwnerTables is not (["WorldPilot"] or ["WorldInventoryOwner"])
            )
            {
                throw new InvalidDataException("Unknown saved inventory owner constraint.");
            }
            // Historical migrations can already have replaced one table from the
            // current EF schema. Rebuild both whenever either retains pilot-only ownership.
            if (ownerTables is ["WorldPilot"] || shipOwnerTables is ["WorldPilot"])
            {
                string script = world.Database.GenerateCreateScript();
                Rebuild(world, script, "WorldItem");
                Rebuild(world, script, "WorldShip");
            }
            if (world.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single() != 0)
            {
                throw new InvalidDataException("Inventory ownership upgrade found an invalid saved reference.");
            }
            world.Database.ExecuteSqlRaw("PRAGMA user_version=78");
            transaction.Commit();
        }
        finally
        {
            world.Database.ExecuteSqlRaw("PRAGMA legacy_alter_table=OFF");
            world.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON");
            world.Database.CloseConnection();
        }
    }

    private static void Rebuild(WorldDbContext world, string script, string table)
    {
        string prefix = $"CREATE TABLE \"{table}\"";
        int start = script.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidDataException("Missing current inventory table schema.");
        }
        int end = script.IndexOf(';', start);
        if (end < 0)
        {
            throw new InvalidDataException("Incomplete current inventory table schema.");
        }
        string temporary = table + "OwnershipUpgrade";
        string create = script[start..(end + 1)].Replace(prefix, $"CREATE TABLE \"{temporary}\"", StringComparison.Ordinal);
        var objects = world
            .Database.SqlQuery<string>(
                $"SELECT sql AS Value FROM sqlite_schema WHERE tbl_name={table} AND type IN ('index','trigger') AND sql IS NOT NULL"
            )
            .ToArray();
        var columns = world.Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table})").ToArray();
        long sequence = world
            .Database.SqlQuery<long>($"SELECT coalesce(max(seq),0) AS Value FROM sqlite_sequence WHERE name={table}")
            .Single();
        string names = string.Join(',', columns.Select(Quote));
        world.Database.ExecuteSqlRaw(create);
        // Table/column identifiers are quoted separately from values; table names
        // are the two fixed authored names and columns come from SQLite metadata.
        string copy = $"INSERT INTO {Quote(temporary)}({names}) SELECT {names} FROM {Quote(table)}";
        string drop = $"DROP TABLE {Quote(table)}";
        string rename = $"ALTER TABLE {Quote(temporary)} RENAME TO {Quote(table)}";
        world.Database.ExecuteSqlRaw(copy);
        world.Database.ExecuteSqlRaw(drop);
        world.Database.ExecuteSqlRaw(rename);
        foreach (string statement in objects)
        {
            world.Database.ExecuteSqlRaw(statement);
        }
        world.Database.ExecuteSql($"UPDATE sqlite_sequence SET seq=max(seq,{sequence}) WHERE name={table}");
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
