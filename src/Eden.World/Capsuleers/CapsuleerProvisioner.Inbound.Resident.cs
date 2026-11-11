using System.Security.Cryptography;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerProvisioner
{
    private sealed record InboundSnapshot(long? Existing, long Seed, int[] Roster, TrafficProvisioning.Reservations? Reservations);

    private long? EnsureInbound(
        WorldAction world,
        CatalogDbContext catalog,
        WatchedTrafficGate gate,
        CapsuleerTrafficRoute route,
        bool sourceObserved,
        int visitor
    )
    {
        int gateSlot = checked((int)Math.Min(gate.ArrivalGateId, gate.DepartureGateId));
        int slot = visitor == 0 ? gateSlot : checked(gateSlot + visitor * TrafficJourneyRules.VisitorSlotStride);
        int community = gate.ArrivalGateId == gateSlot ? gate.ObserverSystemId : gate.SourceSystemId;
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
        return pilot;
    }
}
