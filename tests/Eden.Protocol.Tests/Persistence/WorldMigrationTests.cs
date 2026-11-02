using Eden.Protocol.Docking;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldMigrationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void PreservesEachLegacyWorldVersion(int version)
    {
        var directory = Directory.CreateTempSubdirectory("eden-migrate-");
        try
        {
            var file = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
            TestCatalog.Create(file.CatalogPath);
            CreateLegacy(file.Path, version);
            var world = new SqliteWorld(file);
            var pilot = Assert.IsType<DockedPilot>(world.FindPilot(1000001));
            Assert.Equal("Original Pilot", pilot.Name);
            Assert.Equal(version == 1 ? "Ibis" : "Original Ship", pilot.ShipName);
            Assert.Equal(1000000000001, pilot.ShipId);
            Assert.Equal(version >= 4 ? new SpacePosition(1, 2, 3) : null, pilot.Position);
            var wing = Assert.IsType<DockedPilot>(world.FindPilot(1000002));
            Assert.Equal(version == 5 ? "Original Wing" : "Eden Wing", wing.Name);
            Assert.Equal(pilot, new SqliteWorld(file).FindPilot(1000001));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void InvalidCatalogReferenceRollsBackLegacyConversion()
    {
        var directory = Directory.CreateTempSubdirectory("eden-migrate-");
        try
        {
            var file = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
            TestCatalog.Create(file.CatalogPath);
            CreateLegacy(file.Path, 3);
            using var connection = new SqliteConnection($"Data Source={file.Path};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pilot SET corporation=999 WHERE account=1000001";
            command.ExecuteNonQuery();
            Assert.Throws<InvalidDataException>(() => new SqliteWorld(file));
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(3L, command.ExecuteScalar());
            command.CommandText = "SELECT name FROM ship WHERE owner=90000001";
            Assert.Equal("Original Ship", command.ExecuteScalar());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TwoPilotsCanSaveTheirPositionsConcurrently()
    {
        using TestWorld db = new();
        await Task.WhenAll(Enumerable.Range(1, 2).Select(offset => Task.Run(() =>
        {
            for (int tick = 1; tick <= 8; tick++)
            {
                db.Store.SetPosition(1000000000000 + offset, new(tick, offset, 0));
            }
        })));
        Assert.Equal(new SpacePosition(8, 1, 0), db.Store.FindPilot(1000001)!.Position);
        Assert.Equal(new SpacePosition(8, 2, 0), db.Store.FindPilot(1000002)!.Position);
    }

    private static void CreateLegacy(string path, int version)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        using var stream = typeof(TestWorld).Assembly.GetManifestResourceStream(typeof(TestWorld), "LegacyWorldV3.sql")!;
        using StreamReader reader = new(stream);
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE pilot SET name='Original Pilot'; UPDATE ship SET name='Original Ship'";
        command.ExecuteNonQuery();
        if (version <= 2)
        {
            command.CommandText = "ALTER TABLE ship_type DROP COLUMN capacity; ALTER TABLE ship_type DROP COLUMN turrets;";
            command.ExecuteNonQuery();
        }
        if (version == 1)
        {
            command.CommandText = "ALTER TABLE ship DROP COLUMN name";
            command.ExecuteNonQuery();
        }
        if (version >= 4)
        {
            command.CommandText = "CREATE TABLE flight(ship INTEGER PRIMARY KEY REFERENCES ship(id),x REAL,y REAL,z REAL); INSERT INTO flight VALUES(1000000000001,1,2,3)";
            command.ExecuteNonQuery();
        }
        if (version == 5)
        {
            command.CommandText = """
                INSERT INTO account VALUES(1000002);
                INSERT INTO pilot VALUES(90000002,1000002,'Original Wing',1000044,1000000000002);
                INSERT INTO ship VALUES(1000000000002,90000002,601,60003760,'Wing Ibis');
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = $"PRAGMA user_version={version}";
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
