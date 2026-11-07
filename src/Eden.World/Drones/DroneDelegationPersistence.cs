using Eden.Data.World.Drones;
using Eden.Gameplay.Drones;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal static class DroneDelegationPersistence
{
    internal static DroneDelegation? Read(WorldDrone drone) =>
        drone.DelegationMode == DroneDelegationMode.None
            ? null
            : new(
                drone.DelegationMode,
                drone.DelegatePilotId,
                drone.DelegateShipId,
                drone.DelegateFleetId,
                drone.OwnerMembership,
                drone.DelegateMembership,
                drone.ReactionRevision
            );

    internal static void Write(WorldDrone drone, DroneDelegation? delegation)
    {
        drone.DelegationMode = delegation?.Mode ?? DroneDelegationMode.None;
        drone.DelegatePilotId = delegation?.PilotId ?? 0;
        drone.DelegateShipId = delegation?.ShipId ?? 0;
        drone.DelegateFleetId = delegation?.FleetId ?? 0;
        drone.OwnerMembership = delegation?.OwnerMembership ?? 0;
        drone.DelegateMembership = delegation?.DelegateMembership ?? 0;
        drone.ReactionRevision = delegation?.ReactionRevision ?? 0;
    }

    internal static void Upgrade(WorldDbContext world)
    {
        if (
            world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldDrone')")
                .Contains(nameof(WorldDrone.DelegationMode))
        )
        {
            return;
        }
        world.Database.ExecuteSqlRaw(
            """
            ALTER TABLE WorldDrone ADD COLUMN DelegationMode INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE WorldDrone ADD COLUMN DelegatePilotId INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE WorldDrone ADD COLUMN DelegateShipId INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE WorldDrone ADD COLUMN DelegateFleetId INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE WorldDrone ADD COLUMN OwnerMembership INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE WorldDrone ADD COLUMN DelegateMembership INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE WorldDrone ADD COLUMN ReactionRevision INTEGER NOT NULL DEFAULT 0;
            CREATE INDEX IF NOT EXISTS IX_WorldDrone_DelegateShipId ON WorldDrone(DelegateShipId);
            """
        );
    }
}
