using Eden.Data.World.Characters;
using Eden.Data.World.Flight;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Flight;

internal static class CapitalTravelSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var timers = model.Entity<SavedJumpTimers>().ToTable("WorldJumpTimers");
        timers.HasKey(row => row.PilotId);
        timers.HasOne<Pilot>().WithMany().HasForeignKey(row => row.PilotId).OnDelete(DeleteBehavior.Cascade);
        var fields = model.Entity<SavedCynosuralField>().ToTable("WorldCynosuralField");
        fields.HasKey(row => row.PilotId);
        fields.HasOne<Pilot>().WithMany().HasForeignKey(row => row.PilotId).OnDelete(DeleteBehavior.Cascade);
        fields.HasOne<Ship>().WithMany().HasForeignKey(row => row.ShipId).OnDelete(DeleteBehavior.Cascade);
        fields.HasIndex(row => row.SystemId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldJumpTimers (
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ActivationUntil INTEGER NOT NULL, FatigueUntil INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS WorldCynosuralField (
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                ModuleId INTEGER NOT NULL, BeaconId INTEGER NOT NULL, FleetId INTEGER NOT NULL,
                SystemId INTEGER NOT NULL, EndsAt INTEGER NOT NULL, X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL,
                Radius REAL NOT NULL, SpawnRadius REAL NOT NULL, TypeId INTEGER NOT NULL, StartedAt INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_WorldCynosuralField_SystemId ON WorldCynosuralField(SystemId);
            """
        );
        var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCynosuralField')").ToArray();
        if (!columns.Contains("TypeId"))
        {
            // Schema 193 stored industrial fields exclusively.
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCynosuralField ADD COLUMN TypeId INTEGER NOT NULL DEFAULT 52696");
        }
        if (!columns.Contains("StartedAt"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCynosuralField ADD COLUMN StartedAt INTEGER NOT NULL DEFAULT 0");
            long duration = CapitalTravelRules.IndustrialCycleMilliseconds * TimeSpan.TicksPerMillisecond;
            world.Database.ExecuteSql($"UPDATE WorldCynosuralField SET StartedAt=EndsAt-{duration}");
        }
    }
}
