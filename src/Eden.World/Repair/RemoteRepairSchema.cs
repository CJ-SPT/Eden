using Eden.Data.World.Repair;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

internal static class RemoteRepairSchema
{
    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldRemoteRepair(
                ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SourceShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                TargetId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                SystemId INTEGER NOT NULL, CycleJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldRemoteRepair_TargetId ON WorldRemoteRepair(TargetId);
            """
        );
}
