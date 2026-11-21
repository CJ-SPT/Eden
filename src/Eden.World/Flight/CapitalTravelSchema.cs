using Eden.Data.World.Characters;
using Eden.Data.World.Flight;
using Eden.Data.World.Inventory;
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

    internal static void Upgrade(WorldDbContext world) =>
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
                Radius REAL NOT NULL, SpawnRadius REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_WorldCynosuralField_SystemId ON WorldCynosuralField(SystemId);
            """
        );
}
