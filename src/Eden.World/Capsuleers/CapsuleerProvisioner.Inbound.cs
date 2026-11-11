using System.Security.Cryptography;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Operations;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerProvisioner
{
    internal long? PrepareBackgroundInbound(
        CapsuleerScheduler.InboundPreparation request,
        IWorldSynchronization synchronization,
        CancellationToken cancellationToken
    )
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        _startupBlueprints?.BeginPreparation();
        var gate = request.Gate;
        using (synchronization.Enter("inbound observer snapshot"))
        {
            request = request with
            {
                BothObserved =
                    LiveWorld.Find(database.Path)?.CapsuleerActivity.Read().ActiveSystems.Contains(gate.SourceSystemId)
                    ?? request.BothObserved,
            };
        }
        int gateSlot = checked((int)Math.Min(gate.ArrivalGateId, gate.DepartureGateId));
        int slot = checked(gateSlot + request.Visitor * TrafficJourneyRules.VisitorSlotStride);
        int community = gate.ArrivalGateId == gateSlot ? gate.ObserverSystemId : gate.SourceSystemId;
        var route = CapsuleerTrafficRoutes.Inbound(catalog, gate, request.BothObserved, request.Visitor);
        if (route is null)
        {
            return null;
        }
        long seed;
        int[] roster;
        TrafficProvisioning.Reservations reservations;
        using (synchronization.Enter("inbound preparation snapshot"))
        {
            var snapshot = WorldActionScope.Inspect(
                database,
                world =>
                {
                    var existing = world
                        .Set<SavedCapsuleerAgent>()
                        .Where(agent =>
                            agent.CommunitySystemId == community && agent.Career == CapsuleerCareer.Hauling && agent.Slot == slot
                        )
                        .Select(agent => (long?)agent.PilotId)
                        .SingleOrDefault();
                    return existing is not null
                        ? new InboundSnapshot(existing, 0, [], null)
                        : new InboundSnapshot(
                            null,
                            world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed,
                            BackgroundSlots(world, community)
                                .Where(value => value.Career == CapsuleerCareer.Hauling)
                                .Select(value => value.Hull)
                                .ToArray(),
                            BackgroundReservations(world)
                        );
                }
            );
            if (snapshot.Existing is not null)
            {
                return snapshot.Existing;
            }
            seed = snapshot.Seed;
            reservations = snapshot.Reservations!;
            roster = snapshot.Roster;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var origins = CapsuleerProvisioningCatalog.Origins(catalog);
        if (origins.Length == 0)
        {
            return null;
        }
        PrepareMovementCatalog(catalog, route.Systems);
        {
            var profile = TrafficProvisioning.SelectPrepared(
                database,
                catalog,
                seed,
                community,
                slot,
                route,
                reservations: reservations,
                roster: roster
            );
            int hull = profile?.HullTypeId ?? HaulingRules.BadgerType;
            if (profile is null && !TrafficProvisioning.HasFallback(catalog))
            {
                return null;
            }
            ulong roleSeed = CapsuleerProvisioningRules.Seed(seed, community, CapsuleerCareer.Hauling, slot);
            int race = origins[(int)(roleSeed % (ulong)origins.Length)].RaceId;
            (_startupBlueprints ??= new(catalog)).Prepare(catalog, race, hull, profile);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var owner = synchronization.Enter("inbound prepared founder");
        try
        {
            _startupReservations = WorldActionScope.Inspect(database, BackgroundReservations);
            _requirePreparedBlueprints = true;
            long? pilot = EnsureInbound(gate, request.BothObserved, request.Visitor);
            _backgroundIdentityRevision = LiveWorld.Find(database.Path)?.PopulationIdentityRevision ?? -1;
            return pilot;
        }
        catch
        {
            _backgroundReservations = null;
            throw;
        }
        finally
        {
            _startupReservations = null;
            _requirePreparedBlueprints = false;
        }
    }

    internal long? EnsureInbound(WatchedTrafficGate gate, bool sourceObserved = false, int visitor = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(visitor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(visitor, CapsuleerPopulationRules.MaximumGateVisitors);
        using (database.EnterMovement())
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            sourceObserved =
                sourceObserved
                || LiveWorld.Find(database.Path)?.CapsuleerActivity.Read().ActiveSystems.Contains(gate.SourceSystemId) == true;
            var route = CapsuleerTrafficRoutes.Inbound(catalog, gate, sourceObserved, visitor);
            if (route is null)
            {
                return null;
            }
            // Reciprocal observers share one durable lane identity. High gate
            // IDs occupy slots disjoint from the small ordinary home roster.
            if (LiveWorld.Find(database.Path) is not null)
            {
                using WorldActionScope action = new(database, new(systems: [gate.SourceSystemId, gate.ObserverSystemId]));
                var result = EnsureInbound(action.World, catalog, gate, route, sourceObserved, visitor);
                action.Commit();
                return result;
            }
            int gateSlot = checked((int)Math.Min(gate.ArrivalGateId, gate.DepartureGateId));
            int slot = visitor == 0 ? gateSlot : checked(gateSlot + visitor * TrafficJourneyRules.VisitorSlotStride);
            int community = gate.ArrivalGateId == gateSlot ? gate.ObserverSystemId : gate.SourceSystemId;
            using WorldDbContext world = new(database.Path)
            {
                Participants = TransactionParticipants.None,
                CommittedSystems = System.Collections.Immutable.ImmutableHashSet.Create(gate.SourceSystemId),
            };
            using var transaction = world.Database.BeginTransaction();
            long? existing = world
                .Set<SavedCapsuleerAgent>()
                .Where(agent => agent.CommunitySystemId == community && agent.Career == CapsuleerCareer.Hauling && agent.Slot == slot)
                .Select(agent => (long?)agent.PilotId)
                .SingleOrDefault();
            if (existing is not null)
            {
                return existing;
            }
            var policy = world.Settings.Capsuleers;
            if (!policy.Enabled || visitor >= policy.GateVisitors)
            {
                return null;
            }
            var state = world.Set<SavedCapsuleerProvisioning>().SingleOrDefault();
            if (state is null)
            {
                state = new() { Id = 1, WorldSeed = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & long.MaxValue };
                world.Add(state);
            }
            var observer = CapsuleerTrafficRoutes.Read(catalog, gate.ObserverSystemId);
            var source = CapsuleerTrafficRoutes.Read(catalog, gate.SourceSystemId);
            int observerCount = CapsuleerEncounterRules.Haulers(
                state.WorldSeed,
                gate.ObserverSystemId,
                observer.GateCount,
                observer.StationCount
            );
            int sourceCount = CapsuleerEncounterRules.Haulers(state.WorldSeed, gate.SourceSystemId, source.GateCount, source.StationCount);
            var origins = CapsuleerProvisioningCatalog.Origins(catalog);
            if (
                Math.Max(observerCount, sourceCount) == 0
                || origins.Length == 0
                || database.Traffic.Profiles.Count == 0 && !TrafficProvisioning.HasFallback(catalog)
            )
            {
                world.SaveChanges();
                transaction.Commit();
                return null;
            }
            if (world.Set<SavedCapsuleerPopulation>().Find(community) is null)
            {
                world.Add(
                    new SavedCapsuleerPopulation
                    {
                        SystemId = community,
                        HomeStationId = route.SourceStationId,
                        Seed = (long)(CapsuleerProvisioningRules.Seed(state.WorldSeed, community, CapsuleerCareer.Hauling) & long.MaxValue),
                        Version = CapsuleerPopulationStore.PopulationVersion,
                    }
                );
            }
            EnsureAccount(world);
            world.SaveChanges();
            CapsuleerSite site = new(community, CapsuleerEligibility.Eligible, route.SourceStationId, route.DestinationStationId);
            long? pilot = CreateFounder(
                world,
                catalog,
                site,
                origins,
                CapsuleerCareer.Hauling,
                state.WorldSeed,
                slot,
                route,
                HaulerEncounterOpening.Inbound,
                gate,
                sourceObserved,
                visitor
            );
            world.SaveChanges();
            transaction.Commit();
            return pilot;
        }
    }
}
