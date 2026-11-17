using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool TryLaunchPatrol(long sequence)
    {
        var policy = database.Settings.Capsuleers;
        var patrol = new PatrolProgress(PatrolKind.Roam, Pilot.SystemId);
        if (!PatrolAllowed(patrol) || !PatrolRules.Choose(pilotId, sequence, policy.RoamPercent))
        {
            return false;
        }
        var grids = Objects
            .Where(o => o.DestinationGateId is not null || o.GroupId == AsteroidDefinitions.BeltGroup)
            .OrderBy(o => TravelerRoutineRules.Mix((ulong)o.Id ^ (ulong)pilotId ^ (ulong)sequence))
            .Take(PatrolRules.MaximumGrids)
            .Select(o => o.Id)
            .ToArray();
        if (grids.Length == 0)
        {
            return false;
        }
        LaunchPatrol(patrol, grids, TimeSpan.FromMinutes(RattingRules.MaximumMinutes).Ticks);
        return true;
    }

    private bool TryPatrolTransitGate(StaticSpaceObject gate)
    {
        var policy = database.Settings.Capsuleers;
        if (
            _background
            || !policy.RoamingPvp
            || Work.Retreating
            || Work.Freight?.Stacks.Length > 0
            || Work.Resupply?.DestinationStationId is not null
            || Work.LastPatrolledGateId == gate.Id
            || !PatrolRules.Choose(pilotId, gate.Id ^ (Work.Journey?.Sequence ?? Work.DwellSequence), policy.RoamPercent)
        )
        {
            return false;
        }
        var patrol = new PatrolProgress(PatrolKind.Transit, Pilot.SystemId);
        if (!PatrolAllowed(patrol))
        {
            return false;
        }
        var fit = defense.Profile(pilotId);
        if (
            !fit.Combat
            || !fit.RattingWeapons.Any(weapon => weapon.AvailableShots > 0)
            || health.Read(pilotId, Now) is not { } current
            || CapsuleerDefenseRules.Retreat(current, true)
        )
        {
            return false;
        }
        _navigation.SetAtRest();
        _work = Work with
        {
            LastPatrolledGateId = gate.Id,
            Ratting = new(Work.DestinationStationId, [gate.Id], 0, TimeSpan.FromMinutes(RattingRules.MaximumMinutes).Ticks)
            {
                Phase = RattingPhase.Searching,
                Patrol = patrol,
                LastObservedAt = Now,
            },
        };
        return true;
    }

    private void LaunchPatrol(PatrolProgress patrol, long[] grids, long duration)
    {
        _work = Work with
        {
            Ratting = new(Pilot.StationId, grids, 0, duration) { Patrol = patrol, LastObservedAt = Now },
        };
        LaunchActivity();
        FlyToBelt();
    }

    private bool PatrolAllowed(PatrolProgress patrol) =>
        Pilot.SystemId == patrol.SystemId
        && PatrolRules.Allowed(
            patrol,
            database.Catalog.Definition(CapsuleerProvisioningCatalog.Security, Pilot.SystemId),
            database.Settings.Capsuleers
        );

    private CapsuleerAttacker? PatrolTarget(RattingProgress activity, PatrolProgress patrol)
    {
        if (patrol.Camp is { } camp && activity.Phase != RattingPhase.Returning)
        {
            return CampTarget(activity, patrol, camp);
        }
        if (activity.RemainingTicks <= 0 || !PatrolAllowed(patrol) || Work.Retreating)
        {
            EndSortie(activity);
            return null;
        }
        if (activity.Phase is not (RattingPhase.Searching or RattingPhase.Fighting) || _navigation.Warp is not null || _target is null)
        {
            return null;
        }
        if (activity.TargetId != 0)
        {
            var retained = defense.PatrolTarget(Pilot, activity.TargetId, _target.Position, Now);
            if (retained is not null)
            {
                return retained;
            }
            activity = activity with { TargetId = 0, Phase = RattingPhase.Searching };
            _work = Work with { Ratting = activity };
        }
        if (Work.ActiveTicks < patrol.NextLook)
        {
            return null;
        }
        patrol = patrol with
        {
            NextLook = Work.ActiveTicks + TimeSpan.FromSeconds(PilotAwareness.LookSeconds(pilotId)).Ticks,
            LeaveGridAt =
                patrol.LeaveGridAt > 0
                    ? patrol.LeaveGridAt
                    : Work.ActiveTicks + TimeSpan.FromSeconds(PatrolRules.DwellSeconds(pilotId, _target.Id)).Ticks,
        };
        var seen = defense.Observe(Pilot, Work.ActiveTicks);
        var candidates = PatrolRules.Candidates(Pilot, _target.Position, seen);
        var target = candidates
            .Where(c => c.Radius <= 2 * Pilot.Flight.ShipRadius || !PilotAwareness.ArmedHull(c.Hull))
            .Select(c => defense.PatrolTarget(Pilot, c.ShipId, _target.Position, Now))
            .FirstOrDefault(c => c is not null);
        _work = Work with
        {
            Ratting = activity with
            {
                Patrol = patrol,
                TargetId = target?.ShipId ?? 0,
                Phase = target is null ? RattingPhase.Searching : RattingPhase.Fighting,
            },
        };
        return target;
    }
}
