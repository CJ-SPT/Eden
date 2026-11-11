using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Docking;

internal sealed partial class PilotGateTravelStore
{
    internal static long Transfer(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long sourceGateId,
        long exitGateId,
        long now
    ) => TransferDetailed(world, catalog, pilotId, shipId, sourceGateId, exitGateId, now).AccountId;

    internal static GateTransferResult TransferDetailed(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long sourceGateId,
        long exitGateId,
        long now
    )
    {
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var position = pilot.ActiveShip.Position;
        int origin = position?.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        var gate = catalog.GatesInSystem(origin).SingleOrDefault(g => g.Id == sourceGateId && g.DestinationId == exitGateId);
        var body = catalog.FindCelestial(sourceGateId);
        var exit = catalog.FindCelestial(exitGateId);
        double radius = catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0;
        if (
            pilot.ActiveShipId != shipId
            || position is null
            || gate is null
            || body is null
            || exit is null
            || !catalog
                .GatesInSystem(gate.DestinationSystemId)
                .Any(g => g.Id == exitGateId && g.DestinationId == sourceGateId && g.DestinationSystemId == origin)
            || new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(body.X, body.Y, body.Z))
                > (body.Radius ?? catalog.RequireItem(body.TypeId).Radius ?? 0) + radius + GateTravelRules.ActivationRange
        )
        {
            throw new InvalidDataException("Use the active ship at a connected stargate within activation range.");
        }
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.Jump, now, gate.DestinationSystemId);
        var arrival = GateTravelRules.Arrival(
            shipId,
            new(exit.X, exit.Y, exit.Z),
            exit.Radius ?? catalog.RequireItem(exit.TypeId).Radius ?? 0,
            radius
        );
        SqliteWorld.SetPosition(world, catalog, shipId, arrival, gate.DestinationSystemId, now);
        return new(pilot.AccountId, arrival, SystemObjects.FindSystem(catalog, gate.DestinationSystemId));
    }
}
