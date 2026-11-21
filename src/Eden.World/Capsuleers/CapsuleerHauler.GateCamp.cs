using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool TryLaunchCamp(long sequence, CapsuleerDefenseProfile profile)
    {
        var policy = database.Settings.Capsuleers;
        var patrol = new PatrolProgress(PatrolKind.GateCamp, Pilot.SystemId);
        if (!policy.GateCamps || !PatrolRules.Choose(pilotId, ~sequence, policy.CampPercent) || !PatrolAllowed(patrol))
        {
            return false;
        }
        long until = GateCampRules.End(Work.ActiveTicks, policy.CampSeconds);
        if (until <= Work.ActiveTicks)
        {
            return false;
        }
        var rendezvous = Objects
            .Where(o => o.DestinationGateId is not null)
            .MinBy(o => TravelerRoutineRules.Mix((ulong)o.Id ^ (ulong)until));
        if (rendezvous is null || defense.Camps.At(Pilot.SystemId, rendezvous.Id, until, Work.ActiveTicks) is not { } watch)
        {
            return false;
        }
        if (Live is { } live && !watch.Compatible(other => CapsuleerCorporations.CanFlyTogether(live, Pilot, other)))
        {
            return false;
        }
        bool tackleFit = profile.Modules.Any(m => m.Effect is DogmaEffect.WarpScrambler or DogmaEffect.WarpDisruptor);
        var role = watch.Position(pilotId, tackleFit);
        if (role is null)
        {
            return false;
        }
        var launcher = profile.Modules.FirstOrDefault(m => m.Item.GroupId == InterdictionRules.LauncherGroup);
        var camp = new GateCamp(watch.Gate, watch.Until, role.Value, launcher?.Item.Id ?? 0);
        LaunchPatrol(patrol with { Camp = camp, LeaveGridAt = until }, [watch.Gate], until - Work.ActiveTicks);
        return true;
    }

    private CapsuleerAttacker? CampTarget(RattingProgress activity, PatrolProgress patrol, GateCamp camp)
    {
        var watch = defense.Camps.At(Pilot.SystemId, camp.GateId, camp.Until, Work.ActiveTicks);
        if (
            watch is null
            || watch.Dispersing
            || Live is { } live && !watch.Compatible(other => CapsuleerCorporations.CanFlyTogether(live, Pilot, other))
            || Work.Retreating
            || !PatrolAllowed(patrol)
            || watch.Join(pilotId, false, camp.Role) is null
            || activity.RemainingTicks <= 0
        )
        {
            watch?.Leave(pilotId);
            _work = Work with { Ratting = activity with { Phase = RattingPhase.Returning, TargetId = 0 }, Retreating = true };
            return null;
        }
        if (activity.Phase is not (RattingPhase.Searching or RattingPhase.Fighting) || _target is null || _navigation.Warp is not null)
        {
            return null;
        }
        if (Work.ActiveTicks < patrol.NextLook)
        {
            return activity.TargetId > 0 && camp.Role != CampRole.Scout
                ? defense.PatrolTarget(Pilot, activity.TargetId, _target.Position, Now)
                : null;
        }
        var seen = defense.Observe(Pilot, Work.ActiveTicks);
        bool Ally(PilotSighting other) =>
            (PatrolRules.Unaffiliated(Pilot.CorpId, other.CorporationId) || PatrolRules.Friendly(Pilot, other))
            && (watch.Member(other.PilotId) || other.Camp is { } mate && mate.GateId == camp.GateId && mate.Until == camp.Until);
        var opponents = PatrolRules.Candidates(Pilot, _target.Position, seen).Where(c => !Ally(c)).ToArray();
        double friendlyStrength =
            PvpTactics.Threat((Eden.Gameplay.Ships.HullClass)Pilot.ShipGroupId)
            + seen.Where(Ally).Sum(contact => PvpTactics.Threat(contact.Hull));
        if (opponents.Sum(contact => PvpTactics.Threat(contact.Hull)) > friendlyStrength * 2)
        {
            watch.Dispersing = true;
            watch.Leave(pilotId);
            _work = Work with { Ratting = activity with { Phase = RattingPhase.Returning, TargetId = 0 }, Retreating = true };
            return null;
        }
        long reported = watch.Reported(Work.ActiveTicks);
        var contact = opponents
            .OrderByDescending(c => c.ShipId == reported)
            .ThenByDescending(c => c.ShipId == activity.TargetId)
            .Select(c => defense.PatrolTarget(Pilot, c.ShipId, _target.Position, Now))
            .FirstOrDefault(c => c is not null);
        if (contact is not null)
        {
            watch.Report(contact.ShipId, Work.ActiveTicks);
            if (
                database.Settings.Capsuleers.CampBubbles
                && camp.LauncherId != 0
                && Work.ActiveTicks >= camp.ProbeReadyAt
                && database.Catalog.Definition(CapsuleerProvisioningCatalog.Security, Pilot.SystemId) <= 0
            )
            {
                var wait = defense.LaunchProbe(Pilot, camp.LauncherId, Now);
                camp = camp with { ProbeReadyAt = Work.ActiveTicks + wait };
            }
        }
        var target = camp.Role == CampRole.Scout ? null : contact;
        _work = Work with
        {
            Ratting = activity with
            {
                Patrol = patrol with
                {
                    Camp = camp,
                    NextLook = Work.ActiveTicks + TimeSpan.FromSeconds(PilotAwareness.LookSeconds(pilotId)).Ticks,
                },
                TargetId = target?.ShipId ?? 0,
                Phase = target is null ? RattingPhase.Searching : RattingPhase.Fighting,
            },
        };
        return target;
    }
}
