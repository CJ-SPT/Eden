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
            || !InActiveGang && !PatrolRules.Choose(pilotId, gate.Id ^ (Work.Journey?.Sequence ?? Work.DwellSequence), policy.RoamPercent)
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
        if (Work.ActiveTicks < patrol.NextLook)
        {
            return activity.TargetId == 0 ? null : defense.PatrolTarget(Pilot, activity.TargetId, _target.Position, Now);
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
        var gang = InActiveGang ? Work.Gang : null;
        bool Member(long other) => gang?.Members.Any(member => member.PilotId == other) == true;
        var candidates = PatrolRules
            .Candidates(Pilot, _target.Position, seen)
            .Where(contact => !Member(contact.PilotId))
            .Where(contact => defense.PatrolTarget(Pilot, contact.ShipId, _target.Position, Now) is not null)
            .ToArray();
        var profile = defense.Profile(pilotId);
        double friendly =
            PvpTactics.Threat((Eden.Gameplay.Ships.HullClass)Pilot.ShipGroupId)
            + seen.Where(contact =>
                    (Member(contact.PilotId) || PatrolRules.Friendly(Pilot, contact))
                    && contact.Position.DistanceTo(_target.Position) <= PatrolRules.PursuitMeters
                )
                .Sum(contact => PvpTactics.Threat(contact.Hull));
        long reported = gang is null ? 0 : RoamingGangRules.Reported(gang, Pilot.SystemId, _target.Id, Work.ActiveTicks);
        var decision = PvpTactics.Select(
            Pilot.Position!,
            _navigation.Velocity,
            profile.RattingWeapons,
            candidates,
            friendly,
            activity.TargetId,
            patrol.CommittedAt,
            Work.ActiveTicks,
            reported,
            PilotAwareness.Cautious(pilotId),
            Pilot.Flight.MaxSpeed
        );
        bool scout = gang is not null && GangMember?.Role == PvpRole.Scout;
        if (scout && candidates.Length > 0)
        {
            decision = new(
                candidates
                    .OrderByDescending(contact => contact.ShipId == reported)
                    .ThenByDescending(contact => PvpTactics.Threat(contact.Hull))
                    .ThenBy(contact => contact.ShipId)
                    .First()
                    .ShipId,
                0,
                "Scouting a visible contact for the gang."
            );
        }
        var target = decision.TargetId == 0 ? null : defense.PatrolTarget(Pilot, decision.TargetId, _target.Position, Now);
        patrol = patrol with
        {
            CommittedAt = decision.TargetId != activity.TargetId ? Work.ActiveTicks : patrol.CommittedAt,
            DecisionReason = decision.Reason,
        };
        if (gang is not null && target is not null)
        {
            if (scout || gang.LeaderId == pilotId || reported == 0)
            {
                _work = Work with { Gang = RoamingGangRules.Report(gang, target.ShipId, Pilot.SystemId, _target.Id, Work.ActiveTicks) };
            }
            if (scout)
            {
                target = null;
            }
        }
        double gangStrength =
            gang?.Members.Where(member => member.Status == RoamingGangMemberStatus.Active).Sum(member => PvpTactics.Threat(member.Hull))
            ?? friendly;
        if (gang is not null && candidates.Sum(contact => PvpTactics.Threat(contact.Hull)) > gangStrength * 2)
        {
            _work = Work with { Gang = ReturnGang(Work.Gang!, decision.Reason) };
        }
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
