using Eden.Protocol;
using Eden.Protocol.Docking;
using Microsoft.Data.Sqlite;

namespace Eden.Server.Persistence;

public sealed record WorldDatabase(string Path);

[Injectable(InjectionType.Singleton)]
public sealed class SqliteWorld : IDockedWorld
{
    private readonly string _connectionString;

    public SqliteWorld(WorldDatabase database) : this(database.Path)
    {
    }

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
        else if (version is not (0 or 3 or 4 or 5))
        {
            throw new InvalidDataException($"Unsupported Eden world version {version}.");
        }
        if (version < 4)
        {
            // A ship without a flight row is docked at its station. Existing hangars migrate intact.
            command.CommandText = """
                CREATE TABLE flight (ship INTEGER PRIMARY KEY REFERENCES ship(id),
                    x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL);
                PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
        }
        if (version < 5)
        {
            command.CommandText = """
                INSERT INTO account VALUES (1000002);
                INSERT INTO pilot VALUES (90000002,1000002,'Eden Wing',1000044,1000000000002);
                INSERT INTO ship VALUES (1000000000002,90000002,601,60003760,'Wing Ibis');
                PRAGMA user_version = 5;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        return connection;
    }

    public void SetPosition(long shipId, SpacePosition? position)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$ship", shipId);
        if (position is null)
        {
            command.CommandText = "DELETE FROM flight WHERE ship=$ship";
        }
        else
        {
            command.CommandText = """
                INSERT INTO flight VALUES ($ship,$x,$y,$z)
                ON CONFLICT(ship) DO UPDATE SET x=excluded.x,y=excluded.y,z=excluded.z
                """;
            command.Parameters.AddWithValue("$x", position.X);
            command.Parameters.AddWithValue("$y", position.Y);
            command.Parameters.AddWithValue("$z", position.Z);
        }
        command.ExecuteNonQuery();
    }

    public DockedPilot? FindPilot(long accountId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id,p.name,p.corporation,s.id,t.id,t.group_id,t.category,
                   l.id,l.type,l.owner,l.system,l.constellation,l.region,l.operation,l.orbit,l.x,l.y,l.z,s.name,t.capacity,t.turrets,f.x,f.y,f.z
            FROM pilot p JOIN ship s ON s.id=p.active_ship AND s.owner=p.id
            JOIN ship_type t ON t.id=s.type JOIN station l ON l.id=s.station LEFT JOIN flight f ON f.ship=s.id WHERE p.account=$account
            """;
        command.Parameters.AddWithValue("$account", accountId);
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetInt64(0), row.GetString(1), row.GetInt32(2), row.GetInt64(3),
            row.GetInt32(4), row.GetInt32(5), row.GetInt32(6), row.GetInt32(7), row.GetInt32(8),
            row.GetInt32(9), row.GetInt32(10), row.GetInt32(11), row.GetInt32(12), row.GetInt32(13),
            row.GetInt32(14), row.GetDouble(15), row.GetDouble(16), row.GetDouble(17), row.GetString(18), row.GetDouble(19), row.GetInt32(20),
            row.IsDBNull(21) ? null : new(row.GetDouble(21), row.GetDouble(22), row.GetDouble(23))) : null;
    }
}
