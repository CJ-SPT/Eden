using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat;

internal static class MissileSourcePersistence
{
    internal static bool Available(WorldAction world, CatalogDbContext catalog, WorldMissile row)
    {
        if (row.SourceKind == MissileSourceKind.Structure)
        {
            // Paid projectiles survive release, disconnect and a replacement controller.
            return world is EconomyTransaction
                && world.Structure(row.SourceShipId) is { } structure
                && structure.SystemId == row.SystemId
                && structure.State is not (StructureState.Destroyed or StructureState.Unanchored);
        }
        var pilot = world.Pilots.Find(row.PilotId);
        return pilot?.ActiveShipId == row.SourceShipId
            && pilot.ActiveShip.Position is { } position
            && (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) == row.SystemId
            && !world.Set<ShipLoss>().Any(loss => loss.ShipId == row.SourceShipId);
    }
}
