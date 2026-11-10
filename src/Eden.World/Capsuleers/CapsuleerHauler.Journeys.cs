using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private void AssignJourney()
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.assignJourney");
        var previous = Work.Journey;
        var routine = Work.Routine ?? TravelerRoutineRules.Create(pilotId);
        var avoidedSystems = routine
            .Threats.Where(threat => threat.ExpiresAt > Work.ActiveTicks)
            .Select(threat => threat.SystemId)
            .ToHashSet();
        var agent = _operation.World.Set<SavedCapsuleerAgent>().Find(pilotId)!;
        bool homeRoster = agent.Slot < CapsuleerPopulationRules.MaximumHomeRosterSlots;
        int home = homeRoster
            ? _operation
                .Catalog.Definitions.StationsInSystem(agent.CommunitySystemId)
                .OrderByDescending(station => CapsuleerEncounterRules.IsTradeHubStation(station.Id))
                .Select(station => station.Id)
                .FirstOrDefault(previous?.HomeStationId ?? Pilot.StationId)
            : previous?.HomeStationId ?? Pilot.StationId;
        var plan = CapsuleerTrafficRoutes.Read(_operation.Catalog, homeRoster ? agent.CommunitySystemId : Pilot.SystemId);
        bool busy = plan.GateCount >= 4 || plan.StationCount >= 4;
        var kind = homeRoster ? TrafficJourneyRules.Kind(agent.Slot, busy) : previous?.Kind ?? TrafficJourneyKind.Regional;
        if (kind == TrafficJourneyKind.Local && plan.StationCount == 0)
        {
            kind = TrafficJourneyKind.Regional;
        }
        long sequence = checked((previous?.Sequence ?? 0) + 1);
        int avoided = previous?.SourceStationId ?? _route.SourceStationId;
        var route = CapsuleerTrafficRoutes.Journey(
            _operation.Catalog,
            Pilot.StationId,
            kind,
            sequence,
            avoided,
            (HullClass)Pilot.ShipGroupId,
            routine,
            Work.ActiveTicks
        );
        if (kind == TrafficJourneyKind.Local && Pilot.StationId != home && avoidedSystems.Count == 0)
        {
            route =
                previous is not null && previous.Kind == TrafficJourneyKind.Local && previous.SourceStationId == home
                    ? new(Pilot.StationId, home, previous.Systems.Reverse().ToArray())
                    : CapsuleerTrafficRoutes.ToStation(_operation.Catalog, Pilot.StationId, home, (HullClass)Pilot.ShipGroupId);
        }
        if (kind != TrafficJourneyKind.Local && previous is not null && previous.SourceStationId != home && Pilot.StationId != home)
        {
            route =
                CapsuleerTrafficRoutes.ToStation(_operation.Catalog, Pilot.StationId, home, (HullClass)Pilot.ShipGroupId, avoidedSystems)
                ?? route;
        }
        if (route is null)
        {
            throw new InvalidDataException("No compatible station destination is available.");
        }
        _route = route;
        _work = Work with
        {
            Journey = new(kind, home, route.SourceStationId, route.DestinationStationId, route.Systems, sequence),
            TrafficLane = null,
            Routine = routine,
        };
    }
}
