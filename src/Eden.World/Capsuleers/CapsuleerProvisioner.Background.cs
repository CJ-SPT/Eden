using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Operations;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerProvisioner
{
    private sealed record BackgroundSlot(CapsuleerCareer Career, int Slot, int Hull);

    internal CapsuleerProvisioningTurn PrepareBackgroundSystem(
        int system,
        IWorldSynchronization synchronization,
        CancellationToken cancellationToken
    )
    {
        using var scope = new CatalogReadScope().Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        long seed;
        BackgroundSlot[] slots;
        using (synchronization.Enter("population preparation snapshot"))
        using (WorldDbContext world = new(database.Path))
        {
            seed = world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed;
            string fingerprint = Fingerprint(database, catalog, seed, world.Settings.Capsuleers);
            var saved = world.Set<SavedCapsuleerSystem>().AsNoTracking().SingleOrDefault(row => row.SystemId == system);
            if (saved?.PolicyVersion == CapsuleerProvisioningRules.Version && saved.CatalogFingerprint == fingerprint)
            {
                return new(1, 0, 0);
            }
            slots = (
                from agent in world.Set<SavedCapsuleerAgent>()
                join pilot in world.Pilots on agent.PilotId equals pilot.Id
                where agent.CommunitySystemId == system
                select new BackgroundSlot(agent.Career, agent.Slot, pilot.ActiveShip.TypeId)
            ).ToArray();
        }
        cancellationToken.ThrowIfCancellationRequested();
        var traffic = CapsuleerTrafficRoutes.Read(catalog, system);
        var origins = CapsuleerProvisioningCatalog.Origins(catalog);
        int count =
            traffic.Routes.Length == 0
                ? 0
                : CapsuleerEncounterRules.Haulers(seed, system, traffic.GateCount, traffic.StationCount, database.Settings.Capsuleers);
        bool needsShips =
            origins.Length > 0
            && Enumerable.Range(0, count).Any(slot => !slots.Any(saved => saved.Career == CapsuleerCareer.Hauling && saved.Slot == slot));
        TrafficProvisioning.Reservations? planning = null;
        if (needsShips)
        {
            using var owner = synchronization.Enter("population identity snapshot");
            using WorldDbContext world = new(database.Path);
            planning = TrafficProvisioning.Reservations.Load(world);
        }
        var roster = slots.Where(slot => slot.Career == CapsuleerCareer.Hauling).Select(slot => slot.Hull).ToList();
        int offset = (int)(
            CapsuleerProvisioningRules.Seed(seed, system, CapsuleerCareer.Hauling) % (ulong)Math.Max(1, traffic.Routes.Length)
        );
        using (WorldDbContext world = new(database.Path))
        {
            for (int slot = 0; slot < count && origins.Length > 0; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (slots.Any(saved => saved.Career == CapsuleerCareer.Hauling && saved.Slot == slot))
                {
                    continue;
                }
                var opening = CapsuleerEncounterRules.TrafficOpening(seed, system, slot, count);
                var route = SelectRoute(catalog, traffic, system, slot, opening, offset);
                var profile = TrafficProvisioning.Select(
                    database,
                    world,
                    catalog,
                    seed,
                    system,
                    slot,
                    route,
                    reservations: planning,
                    rosterTypes: roster
                );
                if (profile is null && !TrafficProvisioning.HasFallback(catalog))
                {
                    continue;
                }
                int hull = profile?.HullTypeId ?? HaulingRules.BadgerType;
                ulong roleSeed = CapsuleerProvisioningRules.Seed(seed, system, CapsuleerCareer.Hauling, slot);
                int race = origins[(int)(roleSeed % (ulong)origins.Length)].RaceId;
                (_startupBlueprints ??= new(catalog)).Prepare(catalog, race, hull, profile);
                roster.Add(hull);
                if (profile is not null)
                {
                    planning!.Characters.Add(profile.CharacterId);
                    planning.Names.Add(profile.Name);
                }
            }
        }
        _ = CapsuleerProvisioningCatalog.Mining(catalog, system);
        cancellationToken.ThrowIfCancellationRequested();
        using (synchronization.Enter("population prepared batch"))
        {
            try
            {
                // Re-read reservations after validation: sessions and gate visitors may have committed meanwhile.
                if (needsShips)
                {
                    using WorldDbContext world = new(database.Path);
                    _startupReservations = TrafficProvisioning.Reservations.Load(world);
                }
                _requirePreparedBlueprints = true;
                lock (database.MotionLock)
                {
                    using WorldDbContext world = new(database.Path);
                    return Provision(system, catalog, world, globalStartup: true);
                }
            }
            finally
            {
                _startupReservations = null;
                _requirePreparedBlueprints = false;
            }
        }
    }
}
