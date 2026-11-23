using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Maps;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Maps;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Docking;

internal sealed partial class PilotGateTravelStore
{
    public void CheckJump(long pilotId, long shipId)
    {
        if (LiveWorld.Find(database.Path) is not { Economy: var economy })
        {
            return;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        economy.Inspect(
            world =>
            {
                if (world.Pilots.Find(pilotId) is { } pilot && pilot.ActiveShipId == shipId && pilot.ActiveShip.Position is { } position)
                {
                    int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
                    int hullGroup = catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId;
                    RequireFreeOfFocusedTackle(world, shipId, hullGroup, system, now);
                }
                return true;
            },
            new(pilots: [pilotId], ships: [shipId])
        );
    }

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
        Eden.World.Sovereignty.EntosisPersistence.RequireTravel(world, shipId, now);
        Eden.World.Flight.PilotCapitalTravelStore.RequireMobile(world, pilotId, shipId, now);
        Eden.World.Combat.LanceDisruption.RequireTravel(world, shipId, now);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var position = pilot.ActiveShip.Position;
        int origin = position?.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        var gate = catalog.GatesInSystem(origin).SingleOrDefault(g => g.Id == sourceGateId && g.DestinationId == exitGateId);
        var body = catalog.FindGateBody(sourceGateId);
        var exit = catalog.FindGateBody(exitGateId);
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        double radius = hull.Radius ?? 0;
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
        if (
            CapitalTravelRules.RestrictedCapital(hull.GroupId)
            && catalog.Definitions.SystemSecurity.GetValueOrDefault(gate.DestinationSystemId) >= StructureRules.HighSecurityBoundary
        )
        {
            throw new InvalidDataException("Combat capitals cannot enter high security space through stargates.");
        }
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.Jump, now, gate.DestinationSystemId);
        RequireFreeOfFocusedTackle(world, shipId, hull.GroupId, origin, now);
        var arrival = GateTravelRules.Arrival(
            shipId,
            new(exit.X, exit.Y, exit.Z),
            exit.Radius ?? catalog.RequireItem(exit.TypeId).Radius ?? 0,
            radius
        );
        SqliteWorld.SetPosition(world, catalog, shipId, arrival, gate.DestinationSystemId, now);
        Eden.World.Propulsion.GateCloakPersistence.Start(world, shipId, now);
        MapStatisticsStore.Record(world, gate.DestinationSystemId, now, MapActivityKind.Jump);
        return new(pilot.AccountId, arrival, SystemObjects.FindSystem(catalog, gate.DestinationSystemId));
    }

    private static void RequireFreeOfFocusedTackle(WorldAction world, long shipId, int hullGroup, int systemId, long now)
    {
        if (
            CapitalTravelRules.RestrictedCapital(hullGroup)
            && world
                .Filter<WorldTackle>(row =>
                    row.TargetId == shipId
                    && row.TargetKind == TackleTargetKind.Ship
                    && row.SystemId == systemId
                    && row.ExpiresAt > now
                    && row.Strength > 0
                )
                .AsEnumerable()
                .Any(row => InterdictionRules.Focused(row.Effect))
        )
        {
            throw new InvalidDataException("Capital ships cannot use stargates while under focused Heavy Interdiction Cruiser tackle.");
        }
    }
}
