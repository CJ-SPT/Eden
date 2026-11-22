using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
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
        TravelerRoutine Routine,
        int? OperatingStation = null
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

    private JourneyInput JourneyInputs(SavedCapsuleerAgent agent, long activeTicks, int? operatingStation = null) =>
        new(
            agent.Slot,
            agent.CommunitySystemId,
            Pilot.StationId,
            Pilot.SystemId,
            Pilot.ShipGroupId,
            _route.SourceStationId,
            activeTicks,
            Work.Journey,
            Work.Routine ?? TravelerRoutineRules.Create(pilotId),
            operatingStation
        );

    internal static PreparedJourney PlanJourney(CatalogDbContext catalog, JourneyInput input, CapsuleerPopulationPolicy? policy = null)
    {
        var previous = input.Previous;
        var routine = input.Routine;
        var avoidedSystems = routine
            .Threats.Where(threat => threat.ExpiresAt > input.ActiveTicks)
            .Select(threat => threat.SystemId)
            .ToHashSet();
        bool homeRoster = input.OperatingStation.HasValue || input.Slot < CapsuleerPopulationRules.MaximumHomeRosterSlots;
        int home =
            input.OperatingStation
            ?? (
                homeRoster
                    ? catalog
                        .Definitions.StationsInSystem(input.CommunityId)
                        .OrderByDescending(station => CapsuleerEncounterRules.IsTradeHubStation(station.Id))
                        .Select(station => station.Id)
                        .FirstOrDefault(previous?.HomeStationId ?? input.StationId)
                    : previous?.HomeStationId ?? input.StationId
            );
        int homeSystem = input.OperatingStation.HasValue ? catalog.RequireStation(home).SolarSystemId : input.CommunityId;
        var plan = CapsuleerTrafficRoutes.Geography(catalog, homeRoster ? homeSystem : input.SystemId);
        bool busy = plan.GateCount >= 4 || plan.StationCount >= 4;
        var kind =
            homeRoster ? TrafficJourneyRules.Kind(input.Slot, busy)
            : previous is { Kind: not TrafficJourneyKind.PvpShopping } ? previous.Kind
            : TrafficJourneyKind.Regional;
        if (kind == TrafficJourneyKind.Local && plan.StationCount == 0)
        {
            kind = TrafficJourneyKind.Regional;
        }
        long sequence = checked((previous?.Sequence ?? 0) + 1);
        ulong pathSeed = TravelerRoutineRules.Draw(routine, sequence);
        var hull = (HullClass)input.GroupId;
        CapsuleerTrafficRoute? route;
        bool allowedHome = policy?.CanWorkAtStation(catalog.Definitions, home) != false;
        if (kind == TrafficJourneyKind.Local && input.StationId != home && avoidedSystems.Count == 0 && allowedHome)
        {
            route =
                previous is not null && previous.Kind == TrafficJourneyKind.Local && previous.SourceStationId == home
                    ? new(input.StationId, home, previous.Systems.Reverse().ToArray())
                    : CapsuleerTrafficRoutes.JourneyHome(catalog, input.StationId, home, hull, pathSeed);
        }
        else
        {
            bool returnHome =
                kind != TrafficJourneyKind.Local
                && (input.OperatingStation.HasValue || previous is not null)
                && previous?.SourceStationId != home
                && input.StationId != home
                && allowedHome;
            route = returnHome ? CapsuleerTrafficRoutes.JourneyHome(catalog, input.StationId, home, hull, pathSeed, avoidedSystems) : null;
            route ??= CapsuleerTrafficRoutes.Journey(
                catalog,
                input.StationId,
                kind,
                sequence,
                previous?.SourceStationId ?? input.SourceStationId,
                hull,
                routine,
                input.ActiveTicks,
                policy
            );
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
        int? operatingStation = _operation.World is EconomyTransaction world
            ? CapsuleerCorporations.Organization(world, pilotId).OperatingStationId
            : null;
        var input = JourneyInputs(agent, Work.ActiveTicks, operatingStation);
        var prepared =
            _preparedJourney is { } candidate && candidate.Input == input
                ? candidate
                : PlanJourney(_operation.Catalog, input, database.Settings.Capsuleers);
        _route = prepared.Route;
        _work = Work with { Journey = prepared.Journey, TrafficLane = null, Routine = input.Routine };
    }
}
