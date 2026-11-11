using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerProvisioner
{
    private sealed record BackgroundSlot(CapsuleerCareer Career, int Slot, int Hull);

    private TrafficProvisioning.Reservations? _backgroundReservations;
    private long _backgroundIdentityRevision = -1;

    internal CapsuleerProvisioningTurn PrepareBackgroundSystem(
        int system,
        IWorldSynchronization synchronization,
        CancellationToken cancellationToken
    )
    {
        using var scope = new CatalogReadScope().Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        _startupBlueprints?.BeginPreparation();
        long seed;
        BackgroundSlot[] slots;
        using (synchronization.Enter("population preparation snapshot"))
        {
            var snapshot = WorldActionScope.Inspect(
                database,
                world =>
                {
                    long worldSeed = world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed;
                    string fingerprint = Fingerprint(database, catalog, worldSeed, world.Settings.Capsuleers);
                    var saved = world.Set<SavedCapsuleerSystem>().Find(system);
                    bool current = saved?.PolicyVersion == CapsuleerProvisioningRules.Version && saved.CatalogFingerprint == fingerprint;
                    return new BackgroundPreparation(worldSeed, current ? [] : BackgroundSlots(world, system), current);
                }
            );
            if (snapshot.Current)
            {
                return new(1, 0, 0);
            }
            seed = snapshot.Seed;
            slots = snapshot.Slots;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var traffic = CapsuleerTrafficRoutes.Read(catalog, system);
        var origins = CapsuleerProvisioningCatalog.Origins(catalog);
        var mining = CapsuleerProvisioningCatalog.Mining(catalog, system);
        bool needsMiner =
            database.Settings.Capsuleers is { Enabled: true, Miners: true }
            && origins.Length > 0
            && mining.Eligibility == CapsuleerEligibility.Eligible
            && !slots.Any(slot => slot.Career == CapsuleerCareer.Mining);
        if (needsMiner)
        {
            ulong roleSeed = CapsuleerProvisioningRules.Seed(seed, system, CapsuleerCareer.Mining);
            int race = origins[(int)(roleSeed % (ulong)origins.Length)].RaceId;
            (_startupBlueprints ??= new(catalog)).Prepare(catalog, race, CapsuleerCommunityStore.VentureType, null, mining: true);
        }
        int count =
            traffic.Routes.Length == 0
                ? 0
                : CapsuleerEncounterRules.Haulers(seed, system, traffic.GateCount, traffic.StationCount, database.Settings.Capsuleers);
        bool needsShips =
            origins.Length > 0
            && Enumerable.Range(0, count).Any(slot => !slots.Any(saved => saved.Career == CapsuleerCareer.Hauling && saved.Slot == slot));
        TrafficProvisioning.Reservations? planning = null;
        if (needsShips || needsMiner)
        {
            PrepareMovementCatalog(catalog, traffic.Routes.SelectMany(route => route.Systems).Append(system));
            using var owner = synchronization.Enter("population identity snapshot");
            planning = WorldActionScope.Inspect(database, BackgroundReservations);
        }
        var roster = slots.Where(slot => slot.Career == CapsuleerCareer.Hauling).Select(slot => slot.Hull).ToList();
        int offset = (int)(
            CapsuleerProvisioningRules.Seed(seed, system, CapsuleerCareer.Hauling) % (ulong)Math.Max(1, traffic.Routes.Length)
        );
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
                var profile = TrafficProvisioning.SelectPrepared(
                    database,
                    catalog,
                    seed,
                    system,
                    slot,
                    route,
                    reservations: planning!,
                    roster: roster
                );
                if (profile is null && !TrafficProvisioning.HasFallback(catalog))
                {
                    continue;
                }
                int hull = profile?.HullTypeId ?? HaulingRules.BadgerType;
                ulong roleSeed = CapsuleerProvisioningRules.Seed(seed, system, CapsuleerCareer.Hauling, slot);
                int race = origins[(int)(roleSeed % (ulong)origins.Length)].RaceId;
                (_startupBlueprints ??= new(catalog)).Prepare(catalog, race, hull, profile);
                // The commit creates at most one founder. Preparing further fits here
                // would delay entry and could evict the blueprint needed by this batch.
                break;
            }
        }
        _ = CapsuleerProvisioningCatalog.Mining(catalog, system);
        cancellationToken.ThrowIfCancellationRequested();
        using (synchronization.Enter("population prepared batch"))
        {
            try
            {
                // Re-read reservations after validation: sessions and gate visitors may have committed meanwhile.
                if (needsShips || needsMiner)
                {
                    _startupReservations = WorldActionScope.Inspect(database, BackgroundReservations);
                }
                _requirePreparedBlueprints = true;
                CapsuleerProvisioningTurn result;
                using (database.EnterMovement())
                {
                    result = ProvisionPrepared(system, catalog);
                }
                // Commit has published the identity revision. The
                // reservation view already contains this batch's new identity/name.
                if (_startupReservations is null)
                {
                    _backgroundReservations = null;
                }
                _backgroundIdentityRevision = LiveWorld.Find(database.Path)?.PopulationIdentityRevision ?? -1;
                return result;
            }
            catch
            {
                // Founder construction stages reservations before commit. Never retain
                // those names or exhausted candidate pools after a rolled-back batch.
                _backgroundReservations = null;
                throw;
            }
            finally
            {
                _startupReservations = null;
                _requirePreparedBlueprints = false;
            }
        }
    }
}
