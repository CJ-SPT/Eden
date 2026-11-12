using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class PilotMissileStore
{
    private TransactionParticipants LaunchParticipants(WorldAction world, long pilotId, long moduleId, long? target, long now)
    {
        long ship = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.ActiveShipId).Single();
        HashSet<long> ships = [ship];
        HashSet<SimulationResource> resources = [];
        if (target is long id)
        {
            if (world.Set<WorldNpc>().Find(id) is not null)
            {
                resources.Add(new(SharedSimulationResource.Npc, id));
            }
            else
            {
                ships.Add(id);
            }
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (
            PilotAmmunitionStore.Contents(world, moduleId) is { } charge
            && FittingCatalog.DefaultEffect(catalog, charge.TypeId) == DogmaEffect.AutoTargetingMissile
        )
        {
            // Include dirty physical aggression as well as the saved membership before selecting a target.
            var npcs = world.Set<NpcAggression>().Where(row => row.TargetShipId == ship).Select(row => row.NpcId);
            var current = LiveWorld.Find(database.Path)?.CombatForShip(ship)?.Aggressors(ship) ?? [];
            resources.UnionWith(npcs.Concat(current).Select(npc => new SimulationResource(SharedSimulationResource.Npc, npc)));
            ships.UnionWith(
                world
                    .Set<SavedPilotHostility>()
                    .Where(row => row.TargetShipId == ship && row.ExpiresAt > now)
                    .Select(row => row.SourceShipId)
            );
        }
        return new(pilots: [pilotId], ships: ships, resources: resources);
    }

    private static IQueryable<WorldMissile> Flying(WorldAction world, int system) =>
        world.Filter<WorldMissile>(row =>
            row.SourceKind == MissileSourceKind.Pilot && row.SystemId == system && row.Status == MissileFlightStatus.Flying
        );

    private TransactionParticipants FlightParticipants(WorldAction world, int system)
    {
        var flights = Flying(world, system)
            .Select(row => new FlightEndpoint(row.PilotId, row.SourceShipId, row.TargetKind, row.TargetId))
            .ToArray();
        var targets = flights.Where(row => row.Kind == MissileTargetKind.Pilot).Select(row => row.Target).Distinct().ToArray();
        HashSet<long> npcs = targets.SelectMany(ship => ShipLossPersistence.LossNpcParticipants(world, database.Path, ship)).ToHashSet();
        npcs.UnionWith(
            flights
                .Where(row => row.Kind == MissileTargetKind.Npc)
                .Select(row => row.Target)
                .Distinct()
                .SelectMany(target => NpcHostility.Participants(world, target))
        );
        return new(
            systems: [system],
            pilots: flights.Select(row => row.Pilot),
            ships: flights.Select(row => row.Source).Concat(targets),
            resources: npcs.Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
        );
    }

    private sealed record FlightEndpoint(long Pilot, long Source, MissileTargetKind Kind, long Target);
}
