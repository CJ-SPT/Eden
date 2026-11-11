using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed record TrafficTemplateInput(
    int System,
    CapsuleerTrafficRoute Route,
    double Cargo,
    double Drones,
    IReadOnlyList<int> Roster,
    bool Correction
)
{
    internal long Revision { get; init; }
}

internal static class TrafficTemplatePreparation
{
    internal static TrafficTemplateInput? Capture(
        WorldDatabase database,
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilot,
        long shipId,
        int community,
        CapsuleerTrafficRoute? route
    )
    {
        if (database.Traffic.Profiles.Count == 0)
        {
            return null;
        }
        var ship = world.Ships.Find(shipId)!;
        var identity = world.Set<SavedTrafficIdentity>().Find(pilot);
        bool refresh = identity is null && ship.TypeId == HaulingRules.BadgerType;
        bool correction =
            identity is not null
            && catalog.RequireItem(ship.TypeId).GroupId == (int)HullClass.Corvette
            && world.Set<SavedTrafficCorrection>().Find(pilot) is null;
        if (!refresh && !correction)
        {
            return null;
        }
        if (route is null)
        {
            var saved = world.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == pilot);
            var work = HaulerCheckpoint.Read(saved.Progress);
            route = work?.Journey is { } journey
                ? new(journey.SourceStationId, journey.DestinationStationId, journey.Systems)
                : CapsuleerTrafficFlight.Route(saved, catalog);
        }
        double cargo = InventoryPersistence.UsedVolume(world, catalog, pilot, shipId, (int)InventoryFlag.Cargo);
        double drones = InventoryPersistence.UsedVolume(world, catalog, pilot, shipId, (int)InventoryFlag.DroneBay);
        var roster = (
            from agent in world.Set<SavedCapsuleerAgent>()
            join owner in world.Pilots on agent.PilotId equals owner.Id
            join hull in world.Ships on owner.ActiveShipId equals hull.Id
            where agent.CommunitySystemId == community && agent.Career == CapsuleerCareer.Hauling
            select hull.TypeId
        ).ToArray();
        return new(community, route, cargo, drones, roster, correction)
        {
            Revision = LiveWorld.Find(database.Path)?.TrafficTemplateRevision ?? 0,
        };
    }

    internal static void Prepare(WorldDatabase database, CatalogDbContext catalog, TrafficTemplateInput input)
    {
        if (input.Correction)
        {
            _ = TrafficCorrection.CandidatePools(database, catalog, input.System, input.Cargo, input.Drones);
        }
        else
        {
            _ = TrafficProvisioning.CandidatePools(database, catalog, input.System, input.Route, input.Cargo, input.Roster);
        }
    }
}
