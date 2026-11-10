using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    internal sealed record JourneyInput(
        int Slot,
        int CommunityId,
        int StationId,
        int SystemId,
        int GroupId,
        int SourceStationId,
        long ActiveTicks,
        TrafficJourney? Previous,
        TravelerRoutine Routine
    );

    internal sealed record PreparedJourney(JourneyInput Input, CapsuleerTrafficRoute Route, TrafficJourney Journey);

    private PreparedJourney? _preparedJourney;

    internal JourneyInput? CaptureJourney(WorldDbContext world, CapsuleerJob job)
    {
        if (!_loaded || _pilot is null || _work is null || job.Action != CapsuleerAction.Trade)
        {
            return null;
        }
        var agent = world.Set<SavedCapsuleerAgent>().Find(pilotId)!;
        return JourneyInputs(agent, job.DueTicks);
    }

    private JourneyInput JourneyInputs(SavedCapsuleerAgent agent, long activeTicks) =>
        new(
            agent.Slot,
            agent.CommunitySystemId,
            Pilot.StationId,
            Pilot.SystemId,
            Pilot.ShipGroupId,
            _route.SourceStationId,
            activeTicks,
            Work.Journey,
            Work.Routine ?? TravelerRoutineRules.Create(pilotId)
        );

    internal static PreparedJourney PlanJourney(CatalogDbContext catalog, JourneyInput input)
    {
        var previous = input.Previous;
        var routine = input.Routine;
        var avoidedSystems = routine
            .Threats.Where(threat => threat.ExpiresAt > input.ActiveTicks)
            .Select(threat => threat.SystemId)
            .ToHashSet();
        bool homeRoster = input.Slot < CapsuleerPopulationRules.MaximumHomeRosterSlots;
        int home = homeRoster
            ? catalog
                .Definitions.StationsInSystem(input.CommunityId)
                .OrderByDescending(station => CapsuleerEncounterRules.IsTradeHubStation(station.Id))
                .Select(station => station.Id)
                .FirstOrDefault(previous?.HomeStationId ?? input.StationId)
            : previous?.HomeStationId ?? input.StationId;
        var plan = CapsuleerTrafficRoutes.Read(catalog, homeRoster ? input.CommunityId : input.SystemId);
        bool busy = plan.GateCount >= 4 || plan.StationCount >= 4;
        var kind = homeRoster ? TrafficJourneyRules.Kind(input.Slot, busy) : previous?.Kind ?? TrafficJourneyKind.Regional;
        if (kind == TrafficJourneyKind.Local && plan.StationCount == 0)
        {
            kind = TrafficJourneyKind.Regional;
        }
        long sequence = checked((previous?.Sequence ?? 0) + 1);
        int avoided = previous?.SourceStationId ?? input.SourceStationId;
        var route = CapsuleerTrafficRoutes.Journey(
            catalog,
            input.StationId,
            kind,
            sequence,
            avoided,
            (HullClass)input.GroupId,
            routine,
            input.ActiveTicks
        );
        if (kind == TrafficJourneyKind.Local && input.StationId != home && avoidedSystems.Count == 0)
        {
            route =
                previous is not null && previous.Kind == TrafficJourneyKind.Local && previous.SourceStationId == home
                    ? new(input.StationId, home, previous.Systems.Reverse().ToArray())
                    : CapsuleerTrafficRoutes.ToStation(catalog, input.StationId, home, (HullClass)input.GroupId);
        }
        if (kind != TrafficJourneyKind.Local && previous is not null && previous.SourceStationId != home && input.StationId != home)
        {
            route = CapsuleerTrafficRoutes.ToStation(catalog, input.StationId, home, (HullClass)input.GroupId, avoidedSystems) ?? route;
        }
        if (route is null)
        {
            throw new InvalidDataException("No compatible station destination is available.");
        }
        return new(input, route, new(kind, home, route.SourceStationId, route.DestinationStationId, route.Systems, sequence));
    }

    private void AssignJourney()
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.assignJourney");
        var agent = _operation.World.Set<SavedCapsuleerAgent>().Find(pilotId)!;
        var input = JourneyInputs(agent, Work.ActiveTicks);
        var prepared = _preparedJourney is { } candidate && candidate.Input == input ? candidate : PlanJourney(_operation.Catalog, input);
        _route = prepared.Route;
        _work = Work with { Journey = prepared.Journey, TrafficLane = null, Routine = input.Routine };
    }
}
