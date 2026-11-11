using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal static partial class TrafficTemplatePreparation
{
    internal static TrafficTemplateInput? Capture(
        WorldDatabase database,
        WorldAction world,
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
            var work =
                LiveWorld.Find(database.Path)?.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work ?? HaulerCheckpoint.Read(saved.Progress);
            route = work?.Journey is { } journey
                ? new(journey.SourceStationId, journey.DestinationStationId, journey.Systems)
                : CapsuleerTrafficFlight.Route(saved, catalog);
        }
        double cargo = InventoryPersistence.UsedVolume(world, catalog, pilot, shipId, (int)InventoryFlag.Cargo);
        double drones = InventoryPersistence.UsedVolume(world, catalog, pilot, shipId, (int)InventoryFlag.DroneBay);
        var owners = world
            .Set<SavedCapsuleerAgent>()
            .Where(agent => agent.CommunitySystemId == community && agent.Career == CapsuleerCareer.Hauling)
            .Select(agent => agent.PilotId)
            .ToArray();
        var hulls = world.Pilots.Where(owner => Enumerable.Contains(owners, owner.Id)).Select(owner => owner.ActiveShipId).ToArray();
        var roster = world.Ships.Where(hull => Enumerable.Contains(hulls, hull.Id)).Select(hull => hull.TypeId).ToArray();
        return new(community, route, cargo, drones, roster, correction)
        {
            Revision = LiveWorld.Find(database.Path)?.TrafficTemplateRevision ?? 0,
        };
    }
}
