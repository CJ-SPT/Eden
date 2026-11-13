using Eden.Gameplay.Flight;
using Eden.Server;
using Eden.World.Docking;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fixtures;

internal sealed class TestWorld : IDisposable
{
    // Historical migrations reconstruct the original WorldNpc schema first.
    internal const string DropNpcNavigationColumns = """
        ALTER TABLE WorldNpc DROP COLUMN VelocityX;
        ALTER TABLE WorldNpc DROP COLUMN VelocityY;
        ALTER TABLE WorldNpc DROP COLUMN VelocityZ;
        ALTER TABLE WorldNpc DROP COLUMN MotionAt;
        ALTER TABLE WorldNpc DROP COLUMN Navigation;
        """;
    private readonly string _directory = TestDirectory.Create("eden-world-").FullName;
    public string Path => System.IO.Path.Combine(_directory, "world.sqlite");
    public SqliteWorld Store { get; }
    public ServiceProvider Services { get; }
    public IReadOnlyList<StaticSpaceObject> Objects => Services.GetRequiredService<ISystemObjects>().Load(30000142);

    // Remove newer fields before replaying a historical schema version.
    internal const string DropShipStorageSchema =
        DropRemoteAssistanceSchema
        + """
            DROP INDEX IX_WorldShip_OwnerId_StoredInId;
            ALTER TABLE WorldShip DROP COLUMN StoredInId;
            ALTER TABLE WorldShip DROP COLUMN StorageFlagId;
            ALTER TABLE WorldShipLoss DROP COLUMN StoredShipsJson;
            """;
    internal const string DropCloneGradeSchema = DropShipStorageSchema + "ALTER TABLE WorldAccount DROP COLUMN CloneGrade; ";
    internal const string DropSafetySchema = DropCloneGradeSchema + "ALTER TABLE WorldPilot DROP COLUMN SafetyLevel; ";

    internal const string DropRemoteAssistanceSchema = """
        DROP TRIGGER IF EXISTS Clear_CommandBurst_Position;
        DROP TRIGGER IF EXISTS Clear_CommandBurst_System;
        DROP TABLE IF EXISTS WorldCommandBurst;
        DROP TRIGGER IF EXISTS Clear_RemoteAssistance_Item;
        DROP TRIGGER IF EXISTS Clear_RemoteAssistance_Active;
        DROP TRIGGER IF EXISTS Clear_RemoteAssistance_Marker;
        DROP TRIGGER IF EXISTS Clear_RemoteAssistance_Position;
        DROP TRIGGER IF EXISTS Clear_RemoteAssistance_Selection;
        DROP TRIGGER IF EXISTS Clear_RemoteAssistance_Owner;
        DROP TRIGGER IF EXISTS Clear_RemoteAssistance_System;
        DROP TABLE IF EXISTS WorldRemoteAssistance;
        """;

    // Remove the newer live-module table so version 31 can rename the historical table.
    internal const string DropTestingMarketSchema =
        DropSafetySchema
        + """
            DROP TABLE WorldMissile;
            DROP TABLE WorldTackle;
            ALTER TABLE WorldArmorRepair DROP COLUMN Layer;
            ALTER TABLE WorldItem DROP COLUMN CrystalDamage;
            DROP TABLE WorldActiveModule;
            DROP TABLE WorldTestingGrant;
            ALTER TABLE WorldMarketStation DROP COLUMN SeedVersion;
            DROP INDEX IX_WorldMarketOrder_RegionId_TypeId_Bid_PriceCents;
            """;

    public TestWorld(
        bool emptyWallets = true,
        TimeProvider? clock = null,
        bool regionalNpcs = false,
        bool liveWorld = false,
        bool simulationObjects = true,
        bool freshInitialization = false
    )
    {
        if (emptyWallets && clock is null && !freshInitialization)
        {
            TestWorldSeed.CopyTo(new WorldDatabase(Path));
        }
        else
        {
            TestCatalog.Create(new WorldDatabase(Path).CatalogPath);
            WorldInitialization.Initialize(new WorldDatabase(Path));
        }
        if (emptyWallets && (clock is not null || freshInitialization))
        {
            // Feature tests supply their own budgets. Funding/migration tests use
            // emptyWallets: false to retain the real starting grant and journal.
            using WorldDbContext world = new(Path);
            world.Database.ExecuteSqlRaw("DELETE FROM WorldTestingGrant; DELETE FROM WorldWallet");
        }
        Services = ServerServices.Build(
            new WorldDatabase(Path),
            clock,
            population: regionalNpcs ? null : new TestNpcPopulationSeeder(),
            liveWorld: liveWorld,
            simulationObjects: simulationObjects
        );
        Store = Services.GetRequiredService<SqliteWorld>();
    }

    public void Dispose()
    {
        Services.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
