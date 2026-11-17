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
        _work = Work with
        {
            Ratting = new(Pilot.StationId, grids, 0, TimeSpan.FromMinutes(RattingRules.MaximumMinutes).Ticks)
            {
                Patrol = patrol,
                LastObservedAt = Now,
            },
        };
        LaunchActivity();
        FlyToBelt();
        return true;
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
        if (activity.RemainingTicks <= 0 || !PatrolAllowed(patrol) || Work.Retreating)
        {
            _work = Work with { Ratting = activity with { Phase = RattingPhase.Returning, TargetId = 0 }, Retreating = true };
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
