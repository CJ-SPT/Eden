using Eden.Data.World.Capsuleers;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private IEnumerable<(SavedCapsuleerHauler Row, GangRallyRoute? Rally)> GangRecruits(WorldAction world)
    {
        IEnumerable<(SavedCapsuleerHauler Row, GangRallyRoute? Rally)> Stations()
        {
            foreach (var row in At(Pilot.StationId))
            {
                yield return (row, null);
            }

            var catalog = _operation.Catalog;
            var policy = database.Settings.Capsuleers;
            var avoided = NullsecThreats();
            var nearby = CapsuleerTrafficRoutes.Nearby(
                catalog,
                Pilot.SystemId,
                (HullClass)Pilot.ShipGroupId,
                RoamingGangRules.MaximumRallyJumps,
                avoided
            );
            foreach (var (system, _) in nearby.Systems.OrderBy(stop => stop.Jumps).ThenBy(stop => stop.SystemId))
            {
                if (
                    !policy.CanWork(catalog.Definitions, system)
                    || !CapsuleerTrafficRoutes.IsNullsec(catalog.Definitions, system)
                    || nearby.PathTo(system, (ulong)pilotId) is not { } path
                    || path.Any(next => !CapsuleerTrafficRoutes.IsNullsec(catalog.Definitions, next))
                )
                {
                    continue;
                }
                Array.Reverse(path);
                foreach (var station in catalog.Definitions.StationsInSystem(system))
                {
                    if (station.Id == Pilot.StationId)
                    {
                        continue;
                    }
                    foreach (var row in At(station.Id))
                    {
                        yield return (row, new(station.Id, path));
                    }
                }
            }
        }

        IEnumerable<SavedCapsuleerHauler> At(int station) =>
            world
                .Filter<SavedCapsuleerHauler>(row => row.SourceStationId == station)
                .Where(row => row.PilotId != pilotId)
                .OrderBy(row => row.PilotId);
        return Stations().Take(MaximumFormationCandidates);
    }

    private bool TryGangRally(ref RoamingGang gang, RoamingGangMember member)
    {
        if (gang.Phase != RoamingGangPhase.Forming || gang.RallyUntil == 0)
        {
            return false;
        }
        if (member.Rally is { } rally && Pilot.StationId == rally.StationId)
        {
            _route = new(rally.StationId, gang.HomeStationId, rally.Systems);
            _work = Work with
            {
                Gang = gang,
                Journey = new(
                    TrafficJourneyKind.GangRally,
                    rally.StationId,
                    rally.StationId,
                    gang.HomeStationId,
                    rally.Systems,
                    gang.Sequence
                ),
                Ratting = null,
                RouteIndex = 0,
                RouteDirection = 1,
                TrafficLane = null,
                WaitingReason = null,
            };
            LaunchActivity();
            BeginTravel();
            return true;
        }
        int staging = gang.HomeStationId;
        bool assembled = gang
            .Members.Where(other => other.Status == RoamingGangMemberStatus.Active)
            .All(other =>
                Live!.Economy.Peek<Ship>(other.ShipId)?.StationId == staging && Live.Economy.Peek<FlightPosition>(other.ShipId) is null
            );
        if (!assembled)
        {
            _work = Work with
            {
                Activity = HaulerActivity.Docked,
                DueTicks = checked(Work.ActiveTicks + TimeSpan.TicksPerSecond),
                WaitingReason = "Waiting for the roaming gang to rally.",
            };
            return true;
        }
        gang = gang with { Revision = checked(gang.Revision + 1), RallyUntil = 0, DepartAt = Math.Max(gang.DepartAt, Work.ActiveTicks) };
        _work = Work with { Gang = gang };
        return false;
    }
}
