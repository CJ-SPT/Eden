using Eden.Data.Combat;
using Eden.Data.Drones;
using Eden.Data.Mining;
using Eden.Server;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Drones;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldInitializationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(84)]
    [InlineData(WorldMigration.CurrentVersion)]
    public void LiveServicesCanStartBeforeMigratingMissingDroneAndMiningSchema(int version)
    {
        string directory = TestDirectory.Create("eden-live-init-").FullName;
        try
        {
            WorldDatabase database = new(System.IO.Path.Combine(directory, "world.sqlite"));
            TestCatalog.Create(database.CatalogPath);
            if (version > 0)
            {
                WorldInitialization.Initialize(database);
                using WorldDbContext world = new(database.Path);
                world.Database.ExecuteSqlRaw(
                    """
                    DROP TABLE WorldDrone;
                    DROP TABLE WorldDroneSettings;
                    ALTER TABLE WorldNpcAggression DROP COLUMN LastTargetId;
                    ALTER TABLE WorldNpcMiningOperation DROP COLUMN HaulGeneration;
                    """
                );
                string schemaVersion = $"PRAGMA user_version={version}";
                world.Database.ExecuteSqlRaw(schemaVersion);
            }
            using (var services = ServerServices.Build(database, liveWorld: true))
            {
                using (WorldDbContext before = new(database.Path))
                {
                    Assert.Equal(version, before.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
                    Assert.DoesNotContain("WorldDrone", before.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master"));
                }
                WorldInitialization.Initialize(database);
                WorldInitialization.Initialize(database);
                using WorldDbContext saved = new(database.Path);
                Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
                Assert.Empty(saved.Set<WorldDrone>());
                Assert.Empty(saved.Set<WorldDroneSettings>());
                Assert.Empty(saved.Set<WorldNpcMiningOperation>());
                Assert.Empty(saved.Set<NpcAggression>());
                Assert.Equal(new long[] { 90000001, 90000002 }, saved.Pilots.OrderBy(p => p.Id).Select(p => p.Id));
            }
            WorldInitialization.Initialize(database);
            using WorldDbContext reopened = new(database.Path);
            Assert.Empty(reopened.Set<WorldDrone>());
            Assert.Equal(2, reopened.Pilots.Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolvingWorldReaderDoesNotInitializeOrMigrateTheDatabase()
    {
        string directory = TestDirectory.Create("eden-init-").FullName;
        try
        {
            WorldDatabase database = new(System.IO.Path.Combine(directory, "world.sqlite"));
            TestCatalog.Create(database.CatalogPath);
            using var services = ServerServices.Build(database);
            _ = services.GetRequiredService<SqliteWorld>();
            Assert.False(File.Exists(database.Path));
            WorldInitialization.Initialize(database);
            WorldInitialization.Initialize(database);
            Assert.True(File.Exists(database.Path));
            Assert.Single(services.GetRequiredService<SqliteWorld>().ListPilots(1000001));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
