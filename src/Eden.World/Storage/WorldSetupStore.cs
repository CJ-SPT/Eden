using Eden.Gameplay.Configuration;
using Eden.World.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

public sealed record WorldSetupApproval(WorldConfigurationDraft Draft, string Author, bool NewWorld);

internal sealed record WorldSetupState(bool NewWorld, WorldSetupApproval? Approval = null);

// Only the stopped startup owner and the detached wipe checkpoint use this metadata.
internal static class WorldSetupStore
{
    private const string CreateTable = """
        CREATE TABLE IF NOT EXISTS WorldSetup(
            Id INTEGER PRIMARY KEY CHECK(Id=1), NewWorld INTEGER NOT NULL, Document TEXT, Author TEXT);
        """;

    public static WorldSetupState? Read(string worldPath)
    {
        if (!File.Exists(worldPath))
        {
            return new(NewWorld: true);
        }
        using var connection = WorldConnections.FileConnection(worldPath, SqliteOpenMode.ReadOnly);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE name='WorldSetup' AND type='table'";
        if (command.ExecuteScalar() is not null)
        {
            command.CommandText = "SELECT NewWorld, Document, Author FROM WorldSetup WHERE Id=1";
            using var row = command.ExecuteReader();
            if (row.Read())
            {
                bool newWorld = row.GetBoolean(0);
                return new(
                    newWorld,
                    row.IsDBNull(1) ? null : new(ConfigurationValidation.Decode(row.GetString(1), saved: true), row.GetString(2), newWorld)
                );
            }
        }
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()! == 0 ? new(NewWorld: true) : null;
    }

    public static WorldSetupApproval Approve(WorldDatabase database, WorldSetupState state, WorldConfigurationDraft draft, string author)
    {
        if (state.Approval is not null)
        {
            throw new InvalidOperationException("World setup has already been approved.");
        }
        if (!File.Exists(database.CatalogPath))
        {
            throw new InvalidDataException("Install the Eden catalog in the server data directory before creating a world.");
        }
        new WorldConfigurationStore(database, TimeProvider.System).Validate(draft);
        if (string.IsNullOrWhiteSpace(author) || author.Length > 128)
        {
            throw new InvalidDataException("A revision author of at most 128 characters is required.");
        }

        using var connection = WorldConnections.FileConnection(database.Path);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CreateTable;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO WorldSetup(Id, NewWorld, Document, Author) VALUES(1, $newWorld, $document, $author)
            ON CONFLICT(Id) DO UPDATE SET Document=$document, Author=$author WHERE WorldSetup.Document IS NULL;
            """;
        command.Parameters.AddWithValue("$newWorld", state.NewWorld);
        command.Parameters.AddWithValue("$document", ConfigurationValidation.Encode(draft));
        command.Parameters.AddWithValue("$author", author);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("World setup has already been approved.");
        }
        transaction.Commit();
        return new(draft, author, state.NewWorld);
    }

    internal static void RequireReconfiguration(WorldDbContext checkpoint) =>
        checkpoint.Database.ExecuteSqlRaw(CreateTable + " INSERT INTO WorldSetup(Id, NewWorld) VALUES(1, 0);");

    internal static void Complete(WorldDbContext world) => world.Database.ExecuteSqlRaw("DELETE FROM WorldSetup WHERE Id=1");

    public static string Export(WorldConfigurationDraft draft) => ConfigurationValidation.Encode(draft);

    public static WorldConfigurationDraft Parse(string document) => ConfigurationValidation.Decode(document);
}
