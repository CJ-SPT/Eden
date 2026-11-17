using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Sovereignty;

internal static class SovereigntyResourceSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedSovereigntyResourceHub>().ToTable("WorldSovereigntyResourceHub").HasKey(row => row.HubId);
        model.Entity<SavedSovereigntyResourceHub>().Property(row => row.HubId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyResourceHub>().HasIndex(row => row.SystemId);
        model.Entity<SavedSovereigntyUpgrade>().ToTable("WorldSovereigntyUpgrade").HasKey(row => row.Id);
        model.Entity<SavedSovereigntyUpgrade>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedSovereigntyUpgrade>().HasIndex(row => row.HubId);
        model.Entity<SavedSovereigntyUpgrade>().HasIndex(row => row.SystemId);
        model.Entity<SavedSovereigntyFuel>().ToTable("WorldSovereigntyFuel").HasKey(row => row.Id);
        model.Entity<SavedSovereigntyFuel>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedSovereigntyFuel>().HasIndex(row => row.HubId);
        model.Entity<SavedSovereigntyWorkforce>().ToTable("WorldSovereigntyWorkforce").HasKey(row => row.HubId);
        model.Entity<SavedSovereigntyWorkforce>().Property(row => row.HubId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyWorkforce>().HasIndex(row => row.SystemId);
        model.Entity<SavedSovereigntyWorkforceSource>().ToTable("WorldSovereigntyWorkforceSource").HasKey(row => row.Id);
        model.Entity<SavedSovereigntyWorkforceSource>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedSovereigntyWorkforceSource>().HasIndex(row => row.HubId);
        model.Entity<SavedSovereigntySkyhook>().ToTable("WorldSovereigntySkyhook").HasKey(row => row.Id);
        model.Entity<SavedSovereigntySkyhook>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedSovereigntySkyhook>().HasIndex(row => row.SystemId);
        model.Entity<SavedSovereigntySkyhook>().HasIndex(row => row.PlanetId);
        model.Entity<SavedSovereigntyMiningLink>().ToTable("WorldSovereigntyMiningLink").HasKey(row => row.SiteId);
        model.Entity<SavedSovereigntyMiningLink>().Property(row => row.SiteId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyMiningLink>().HasIndex(row => row.HubId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
                CREATE TABLE IF NOT EXISTS WorldSovereigntyResourceHub(
                    HubId INTEGER PRIMARY KEY,
                    SystemId INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    Power INTEGER NOT NULL,
                    Workforce INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyResourceHub_SystemId ON WorldSovereigntyResourceHub(SystemId);
                CREATE TABLE IF NOT EXISTS WorldSovereigntyUpgrade(
                    Id INTEGER PRIMARY KEY,
                    HubId INTEGER NOT NULL,
                    SystemId INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL,
                    Priority INTEGER NOT NULL,
                    ConfiguredOnline INTEGER NOT NULL,
                    PowerState INTEGER NOT NULL,
                    StartupPaid INTEGER NOT NULL,
                    RunningSince INTEGER);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyUpgrade_HubId ON WorldSovereigntyUpgrade(HubId);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyUpgrade_SystemId ON WorldSovereigntyUpgrade(SystemId);
                CREATE TABLE IF NOT EXISTS WorldSovereigntyFuel(
                    Id INTEGER PRIMARY KEY,
                    HubId INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL,
                    Amount INTEGER NOT NULL,
                    Remainder INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyFuel_HubId ON WorldSovereigntyFuel(HubId);
                CREATE TABLE IF NOT EXISTS WorldSovereigntyWorkforce(
                    HubId INTEGER PRIMARY KEY,
                    SystemId INTEGER NOT NULL,
                    Mode INTEGER NOT NULL,
                    Destination INTEGER,
                    Amount INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyWorkforce_SystemId ON WorldSovereigntyWorkforce(SystemId);
                CREATE TABLE IF NOT EXISTS WorldSovereigntyWorkforceSource(
                    Id INTEGER PRIMARY KEY,
                    HubId INTEGER NOT NULL,
                    SourceSystemId INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyWorkforceSource_HubId ON WorldSovereigntyWorkforceSource(HubId);
                CREATE TABLE IF NOT EXISTS WorldSovereigntySkyhook(
                    Id INTEGER PRIMARY KEY,
                    SystemId INTEGER NOT NULL,
                    PlanetId INTEGER NOT NULL,
                    CorporationId INTEGER NOT NULL,
                    Active INTEGER NOT NULL,
                    OnlineAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    Secured INTEGER NOT NULL,
                    Surplus INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntySkyhook_SystemId ON WorldSovereigntySkyhook(SystemId);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntySkyhook_PlanetId ON WorldSovereigntySkyhook(PlanetId);
                CREATE TABLE IF NOT EXISTS WorldSovereigntyMiningLink(
                    SiteId INTEGER PRIMARY KEY,
                    HubId INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyMiningLink_HubId ON WorldSovereigntyMiningLink(HubId);
            """
        );
}
