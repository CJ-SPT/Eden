using Eden.Data.World.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class CapsuleerProvisioningSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCapsuleerProvisioning>().ToTable("WorldCapsuleerProvisioning").HasKey(p => p.Id);
        model.Entity<SavedCapsuleerSystem>().ToTable("WorldCapsuleerSystem").HasKey(s => s.SystemId);
        model.Entity<SavedCapsuleerSimulation>().ToTable("WorldCapsuleerSimulation").HasKey(s => s.SystemId);
        model.Entity<SavedCapsuleerCoarseWork>().ToTable("WorldCapsuleerCoarseWork").HasKey(s => s.SystemId);
        model.Entity<SavedCapsuleerAdmission>().ToTable("WorldCapsuleerAdmission").HasKey(a => a.PilotId);
        model.Entity<SavedCapsuleerCoarsePilot>().ToTable("WorldCapsuleerCoarsePilot").HasKey(p => p.PilotId);
        model.Entity<SavedCapsuleerCommunity>().HasIndex(c => c.PilotId).IsUnique();
        model.Entity<SavedCapsuleerHauler>().HasIndex(h => h.PilotId).IsUnique();
        model.Entity<SavedCapsuleerHauler>().PrimitiveCollection(h => h.RouteSystems);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        string? community = world
            .Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE name='WorldCapsuleerCommunity'")
            .SingleOrDefault();
        if (community?.Contains("CHECK(Id=1)", StringComparison.OrdinalIgnoreCase) == true)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldCapsuleerCommunityGlobal(
                    Id INTEGER PRIMARY KEY, Seed INTEGER NOT NULL, SchoolId INTEGER NOT NULL,
                    Career INTEGER NOT NULL, PilotId INTEGER REFERENCES WorldPilot(Id), Progress TEXT,
                    HomeStationId INTEGER NOT NULL, FounderShipId INTEGER NOT NULL, FounderModuleId INTEGER NOT NULL,
                    StartingCents INTEGER NOT NULL);
                INSERT INTO WorldCapsuleerCommunityGlobal SELECT * FROM WorldCapsuleerCommunity;
                DROP TABLE WorldCapsuleerCommunity;
                ALTER TABLE WorldCapsuleerCommunityGlobal RENAME TO WorldCapsuleerCommunity;
                """
            );
        }
        string? hauler = world
            .Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE name='WorldCapsuleerHauler'")
            .SingleOrDefault();
        if (hauler?.Contains("CHECK(Id=1)", StringComparison.OrdinalIgnoreCase) == true)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldCapsuleerHaulerGlobal(
                    Id INTEGER PRIMARY KEY, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id),
                    SourceStationId INTEGER NOT NULL, DestinationStationId INTEGER NOT NULL,
                    FounderShipId INTEGER NOT NULL, StartingCents INTEGER NOT NULL, Progress TEXT);
                INSERT INTO WorldCapsuleerHaulerGlobal SELECT * FROM WorldCapsuleerHauler;
                DROP TABLE WorldCapsuleerHauler;
                ALTER TABLE WorldCapsuleerHaulerGlobal RENAME TO WorldCapsuleerHauler;
                """
            );
        }
        world.Database.ExecuteSqlRaw(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCapsuleerCommunity_PilotId ON WorldCapsuleerCommunity(PilotId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCapsuleerHauler_PilotId ON WorldCapsuleerHauler(PilotId);
            CREATE TABLE IF NOT EXISTS WorldCapsuleerProvisioning(
                Id INTEGER PRIMARY KEY CHECK(Id=1), WorldSeed INTEGER NOT NULL CHECK(WorldSeed>=0));
            CREATE TABLE IF NOT EXISTS WorldCapsuleerSimulation(
                SystemId INTEGER PRIMARY KEY, StartedTicks INTEGER NOT NULL CHECK(StartedTicks>=0));
            CREATE TABLE IF NOT EXISTS WorldCapsuleerCoarseWork(
                SystemId INTEGER PRIMARY KEY, Generation INTEGER NOT NULL CHECK(Generation>=0),
                Cursor INTEGER NOT NULL CHECK(Cursor>=0), SettledTicks INTEGER NOT NULL CHECK(SettledTicks>=0),
                NextTicks INTEGER NOT NULL CHECK(NextTicks>=0));
            CREATE TABLE IF NOT EXISTS WorldCapsuleerAdmission(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS WorldCapsuleerCoarsePilot(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SettledTicks INTEGER NOT NULL CHECK(SettledTicks>=0));
            CREATE TABLE IF NOT EXISTS WorldCapsuleerSystem(
                SystemId INTEGER PRIMARY KEY, Eligibility INTEGER NOT NULL CHECK(Eligibility BETWEEN 0 AND 8),
                HomeStationId INTEGER, DestinationStationId INTEGER, CatalogFingerprint TEXT NOT NULL,
                PolicyVersion INTEGER NOT NULL CHECK(PolicyVersion>0));
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCapsuleerHauler')")
                .Contains("RouteSystems")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCapsuleerHauler ADD COLUMN RouteSystems TEXT");
        }
    }
}
