using Eden.Protocol.Docking;
using Microsoft.Data.Sqlite;

namespace Eden.Server.Persistence;

public sealed class SqliteWorld : IDockedWorld
{
    private readonly string _connectionString;

    public SqliteWorld(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        long version = (long)command.ExecuteScalar()!;
        if (version == 0)
        {
            using var source = typeof(SqliteWorld).Assembly.GetManifestResourceStream(typeof(SqliteWorld), "DockedWorld.sql")!;
            using StreamReader reader = new(source);
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
        }
        else if (version == 1)
        {
            command.CommandText = "ALTER TABLE ship ADD COLUMN name TEXT NOT NULL DEFAULT 'Ibis'";
            command.ExecuteNonQuery();
        }
        if (version is 1 or 2)
        {
            command.CommandText = """
                ALTER TABLE ship_type ADD COLUMN capacity REAL NOT NULL DEFAULT 125;
                ALTER TABLE ship_type ADD COLUMN turrets INTEGER NOT NULL DEFAULT 2;
                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }
        else if (version is not (0 or 3))
        {
            throw new InvalidDataException($"Unsupported Eden world version {version}.");
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        return connection;
    }

    public DockedPilot? FindPilot(long accountId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id,p.name,p.corporation,s.id,t.id,t.group_id,t.category,
                   l.id,l.type,l.owner,l.system,l.constellation,l.region,l.operation,l.orbit,l.x,l.y,l.z,s.name,t.capacity,t.turrets
            FROM pilot p JOIN ship s ON s.id=p.active_ship AND s.owner=p.id
            JOIN ship_type t ON t.id=s.type JOIN station l ON l.id=s.station WHERE p.account=$account
            """;
        command.Parameters.AddWithValue("$account", accountId);
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt64(0), row.GetString(1), row.GetInt32(2), row.GetInt64(3),
            row.GetInt32(4), row.GetInt32(5), row.GetInt32(6), row.GetInt32(7), row.GetInt32(8),
            row.GetInt32(9), row.GetInt32(10), row.GetInt32(11), row.GetInt32(12), row.GetInt32(13),
            row.GetInt32(14), row.GetDouble(15), row.GetDouble(16), row.GetDouble(17), row.GetString(18), row.GetDouble(19), row.GetInt32(20)) : null;
    }
}
