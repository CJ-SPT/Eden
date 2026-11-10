using System.Security.Cryptography;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerProvisioner
{
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
            int gateSlot = checked((int)Math.Min(gate.ArrivalGateId, gate.DepartureGateId));
            int slot = visitor == 0 ? gateSlot : checked(gateSlot + visitor * TrafficJourneyRules.VisitorSlotStride);
            int community = gate.ArrivalGateId == gateSlot ? gate.ObserverSystemId : gate.SourceSystemId;
            using WorldDbContext world = new(database.Path);
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
