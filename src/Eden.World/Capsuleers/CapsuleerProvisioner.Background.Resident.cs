using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerProvisioner
{
    private static void PrepareMovementCatalog(CatalogDbContext catalog, IEnumerable<int> systems)
    {
        _ = catalog.Records(CatalogData.Client.Achievementevents);
        _ = catalog.Records(CatalogData.Client.Achievements);
        foreach (int system in systems.Distinct())
        {
            _ = CrimewatchPersistence.Security(catalog, system, required: false);
        }
    }

    private sealed record BackgroundPreparation(long Seed, BackgroundSlot[] Slots, bool Current);

    private BackgroundSlot[] BackgroundSlots(WorldAction world, int system) =>
        world
            .Set<SavedCapsuleerAgent>()
            .Where(agent => agent.CommunitySystemId == system)
            .AsEnumerable()
            .Select(agent =>
            {
                long ship = world.Pilots.Where(pilot => pilot.Id == agent.PilotId).Select(pilot => pilot.ActiveShipId).Single();
                int hull = world.Ships.Where(value => value.Id == ship).Select(value => value.TypeId).Single();
                return new BackgroundSlot(agent.Career, agent.Slot, hull);
            })
            .ToArray();

    private TrafficProvisioning.Reservations BackgroundReservations(WorldAction world)
    {
        long revision = LiveWorld.Find(database.Path)?.PopulationIdentityRevision ?? -1;
        if (_backgroundReservations is null || revision < 0 || revision != _backgroundIdentityRevision)
        {
            using var phase = SessionPerformance.Phase("population.identitySnapshot");
            _backgroundReservations = new(
                world.Set<SavedTrafficIdentity>().Select(value => value.CharacterId).ToHashSet(),
                world.Pilots.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            );
            _backgroundIdentityRevision = revision;
        }
        return _backgroundReservations;
    }

    private CapsuleerProvisioningTurn ProvisionPrepared(int system, CatalogDbContext catalog)
    {
        if (LiveWorld.Find(database.Path) is not null)
        {
            using WorldActionScope action = new(database, new(systems: [system]));
            var result = Provision(system, catalog, action.World, globalStartup: true, maximumFounders: 1);
            action.Commit();
            return result;
        }
        using WorldDbContext world = new(database.Path)
        {
            Participants = TransactionParticipants.None,
            CommittedSystems = System.Collections.Immutable.ImmutableHashSet.Create(system),
        };
        return Provision(system, catalog, world, globalStartup: true, maximumFounders: 1);
    }
}
