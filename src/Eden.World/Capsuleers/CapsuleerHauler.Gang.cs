using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private const int MaximumFormationCandidates = 32;

    private sealed record GangCandidate(DockedPilot Pilot, HaulerProgress Work, CapsuleerDefenseProfile Fit);

    private RoamingGangMember? GangMember => Work.Gang?.Members.FirstOrDefault(member => member.PilotId == pilotId);
    private bool InActiveGang =>
        Work.Gang is { Phase: not RoamingGangPhase.Complete }
        && GangMember is { Status: RoamingGangMemberStatus.Active } member
        && member.ShipId == Pilot.ShipId;

    private void SynchronizeGang(bool reconcile = false)
    {
        if (
            Live?.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work.Gang is { } committed
            && (
                Work.Gang is null
                || committed.Id != Work.Gang.Id
                || committed.Sequence != Work.Gang.Sequence
                || committed.Revision > Work.Gang.Revision
            )
        )
        {
            _work = Work with { Gang = committed };
        }
        if (!reconcile || Live is not { } live || Work.Gang is not { } gang || gang.Phase == RoamingGangPhase.Complete)
        {
            return;
        }
        var surviving = gang
            .Members.Where(member => live.Economy.ActiveShip(member.PilotId, member.ShipId))
            .Select(member => member.ShipId)
            .ToHashSet();
        gang = RoamingGangRules.Reconcile(gang, surviving, Work.ActiveTicks);
        if (
            !database.Settings.Capsuleers.RoamingGangs
            || !database.Settings.Capsuleers.RoamingPvp
            || Work.Retreating
            || Work.Defense?.Retreating == true
        )
        {
            gang = ReturnGang(gang, "Withdrawing to preserve the surviving ships.");
        }
        _work = Work with { Gang = gang };
    }

    private static RoamingGang ReturnGang(RoamingGang gang, string reason) =>
        gang.Phase is RoamingGangPhase.Returning or RoamingGangPhase.Complete
            ? gang
            : gang with
            {
                Revision = checked(gang.Revision + 1),
                Phase = RoamingGangPhase.Returning,
                Reason = reason,
            };

    private bool TryGangDeparture()
    {
        if (Live is null)
        {
            return false;
        }
        SynchronizeGang(reconcile: true);
        if (Work.Gang is not { } gang || gang.Phase == RoamingGangPhase.Complete)
        {
            if (!TryFormGang())
            {
                return false;
            }
            gang = Work.Gang!;
        }
        var member = GangMember;
        if (member is null || member.ShipId != Pilot.ShipId || member.Status != RoamingGangMemberStatus.Active)
        {
            return false;
        }
        if (gang.Phase == RoamingGangPhase.Returning && Pilot.StationId == gang.HomeStationId)
        {
            var members = gang
                .Members.Select(other => other.PilotId == pilotId ? other with { Status = RoamingGangMemberStatus.Returned } : other)
                .ToArray();
            _work = Work with
            {
                Gang = gang with
                {
                    Revision = checked(gang.Revision + 1),
                    Members = members,
                    Phase = members.All(other => other.Status != RoamingGangMemberStatus.Active)
                        ? RoamingGangPhase.Complete
                        : RoamingGangPhase.Returning,
                },
                GangReturning = false,
                Retreating = false,
                Ratting = null,
                Activity = HaulerActivity.Docked,
                WaitingReason = "Roaming gang recovering at home.",
                DueTicks = checked(Work.ActiveTicks + RoamingGangRules.RecoveryTicks),
            };
            return true;
        }
        long departure = RoamingGangRules.Departure(gang, member.Role);
        if (Work.ActiveTicks < departure)
        {
            _work = Work with { Activity = HaulerActivity.Docked, DueTicks = departure, WaitingReason = "Forming roaming gang." };
            return true;
        }
        if (Pilot.StationId == gang.DestinationStationId)
        {
            bool assembled = gang
                .Members.Where(other => other.Status == RoamingGangMemberStatus.Active)
                .All(other =>
                    Live!.Economy.Peek<Eden.Data.World.Inventory.Ship>(other.ShipId)?.StationId == gang.DestinationStationId
                    && Live.Economy.Peek<Eden.Data.World.Docking.FlightPosition>(other.ShipId) is null
                );
            if (!assembled && gang.Phase != RoamingGangPhase.Returning)
            {
                _work = Work with
                {
                    Activity = HaulerActivity.Docked,
                    DueTicks = checked(Work.ActiveTicks + TimeSpan.TicksPerSecond),
                    WaitingReason = "Regrouping at the roaming endpoint.",
                };
                return true;
            }
            gang = ReturnGang(gang, "Patrol endpoint reached; returning home.");
        }
        else if (gang.Phase == RoamingGangPhase.Forming)
        {
            gang = gang with { Revision = checked(gang.Revision + 1), Phase = RoamingGangPhase.Hunting };
        }
        _route = new(gang.HomeStationId, gang.DestinationStationId, gang.Systems);
        _work = Work with
        {
            Gang = gang,
            Ratting = null,
            Retreating = gang.Phase == RoamingGangPhase.Returning,
            GangReturning = gang.Phase == RoamingGangPhase.Returning,
            Journey = new(
                TrafficJourneyKind.Regional,
                gang.HomeStationId,
                gang.HomeStationId,
                gang.DestinationStationId,
                gang.Systems,
                gang.Sequence
            ),
            RouteIndex = Array.IndexOf(gang.Systems, Pilot.SystemId),
            RouteDirection = gang.Phase == RoamingGangPhase.Returning ? -1 : 1,
            TrafficLane = null,
        };
        LaunchActivity();
        _work = Work with { Retreating = gang.Phase == RoamingGangPhase.Returning };
        BeginTravel();
        return true;
    }

    private bool TryFormGang()
    {
        var policy = database.Settings.Capsuleers;
        if (
            !policy.Enabled
            || !policy.RoamingGangs
            || !policy.RoamingPvp
            || _background
            || Work.Retreating
            || Work.Freight?.Stacks.Length > 0
            || Work.Resupply?.DestinationStationId is not null
            || database.Catalog.Definition(CapsuleerProvisioningCatalog.Security, Pilot.SystemId) > 0
            || !PatrolRules.Choose(pilotId, Work.ActivitySequence + 1, policy.RoamPercent)
        )
        {
            return false;
        }
        var own = defense.Profile(pilotId);
        if (
            !own.Combat
            || !own.RattingWeapons.Any(weapon => weapon.AvailableShots > 0)
            || CapsuleerDefenseRules.Retreat(health.Read(pilotId, Now), true)
        )
        {
            return false;
        }
        var route = CapsuleerTrafficRoutes.Roam(_operation.Catalog, Pilot.StationId, pilotId ^ Work.ActivitySequence);
        if (route is null)
        {
            return false;
        }
        List<GangCandidate> candidates = [new(Pilot, Work, own)];
        var world = _operation.World;
        var roster = world
            .Set<SavedCapsuleerHauler>()
            .Where(row => row.SourceStationId == Pilot.StationId && row.PilotId != pilotId)
            .OrderBy(row => row.PilotId)
            .Take(MaximumFormationCandidates)
            .ToArray();
        foreach (var row in roster)
        {
            if (candidates.Count >= policy.RoamingGangSize)
            {
                break;
            }
            var saved = world.Pilots.SingleOrDefault(other => other.Id == row.PilotId);
            if (
                saved is null
                || saved.ActiveShip.Position is not null
                || saved.ActiveShip.StationId != Pilot.StationId
                || world.Set<SavedCapsuleerSuspension>().Find(row.PilotId) is not null
            )
            {
                continue;
            }
            var work = Live!.HaulerCheckpoints.GetValueOrDefault(row.PilotId)?.Work ?? HaulerCheckpoint.Read(row.Progress);
            if (
                work is { Gang.Phase: not RoamingGangPhase.Complete }
                || work?.Freight?.Stacks.Length > 0
                || work?.Ratting is not null
                || work?.Resupply?.DestinationStationId is not null
            )
            {
                continue;
            }
            var fit = CapsuleerDefenseProfile.Read(world, _operation.Catalog, row.PilotId);
            if (!fit.Combat || !fit.RattingWeapons.Any(weapon => weapon.AvailableShots > 0))
            {
                continue;
            }
            var other = ships.FindPilot(world, saved.AccountId, saved.Id)!;
            if (
                CapsuleerDefenseRules.Retreat(health.Read(other.PilotId, Now), true)
                || (other.CorpId >= CorporationRules.FirstCorporationId || Pilot.CorpId >= CorporationRules.FirstCorporationId)
                    && other.CorpId != Pilot.CorpId
                    && (Pilot.Identity.AllianceId is null || Pilot.Identity.AllianceId != other.Identity.AllianceId)
            )
            {
                continue;
            }
            work ??= new(
                HaulerActivity.Docked,
                0,
                0,
                other.ShipId,
                row.DestinationStationId,
                0,
                new NavigationState().Capture(),
                ActiveTicks: Work.ActiveTicks,
                ClockOffsetTicks: Work.ActiveTicks
            );
            candidates.Add(new(other, work, fit));
        }
        if (candidates.Count < RoamingGangRules.MinimumMembers || !candidates.Any(candidate => candidate.Fit.TackleRange > 0))
        {
            return false;
        }
        var scout = candidates
            .OrderByDescending(candidate => candidate.Pilot.Flight.MaxSpeed)
            .ThenBy(candidate => candidate.Pilot.PilotId)
            .First();
        var tackler = candidates
            .Where(candidate => candidate != scout && candidate.Fit.TackleRange > 0)
            .OrderByDescending(candidate => candidate.Pilot.Flight.MaxSpeed)
            .FirstOrDefault();
        if (tackler is null)
        {
            return false;
        }
        var members = candidates
            .Select(candidate => new RoamingGangMember(
                candidate.Pilot.PilotId,
                candidate.Pilot.ShipId,
                candidate == scout ? PvpRole.Scout
                    : candidate == tackler ? PvpRole.Tackle
                    : candidate.Fit.PvpRole == PvpRole.Tackle ? PvpRole.Brawler
                    : candidate.Fit.PvpRole
            )
            {
                Hull = (HullClass)candidate.Pilot.ShipGroupId,
            })
            .ToArray();
        long sequence = checked(Work.ActivitySequence + 1);
        var gang = new RoamingGang(
            pilotId,
            sequence,
            1,
            Pilot.StationId,
            route.DestinationStationId,
            route.Systems,
            pilotId,
            members,
            checked(Work.ActiveTicks + RoamingGangRules.FormationTicks),
            checked(Work.ActiveTicks + RoamingGangRules.SortieTicks)
        );
        foreach (var candidate in candidates.Where(candidate => candidate.Pilot.PilotId != pilotId))
        {
            _operation.GangEnrollments.Add(new(candidate.Pilot.PilotId, candidate.Work with { Gang = gang, GangReturning = false }));
        }
        _work = Work with { Gang = gang, GangReturning = false, ActivitySequence = sequence };
        return true;
    }

    private void ApplyGangReturn()
    {
        if (
            Work.Gang is not { Phase: RoamingGangPhase.Returning } gang
            || Work.GangReturning
            || Pilot.Position is null
            || _navigation.Warp is not null
            || Work.Activity == HaulerActivity.Jumping
        )
        {
            return;
        }
        _route = new(gang.HomeStationId, gang.DestinationStationId, gang.Systems);
        int index = Array.IndexOf(gang.Systems, Pilot.SystemId);
        if (index < 0)
        {
            throw new InvalidDataException("The gang is outside its saved route.");
        }
        _work = Work with
        {
            Ratting = null,
            GangReturning = true,
            Retreating = true,
            RouteIndex = index,
            RouteDirection = -1,
            Journey = new(
                TrafficJourneyKind.Regional,
                gang.HomeStationId,
                gang.HomeStationId,
                gang.DestinationStationId,
                gang.Systems,
                gang.Sequence
            ),
        };
        BeginTravel();
    }

    private bool WaitForGangAtGate(StaticSpaceObject gate)
    {
        if (!InActiveGang || Work.GangReturning || GangMember?.Role == PvpRole.Scout)
        {
            return false;
        }
        var gang = Work.Gang!;
        var visible = defense.Observe(Pilot, Work.ActiveTicks).Select(contact => contact.PilotId).ToHashSet();
        bool ready = gang
            .Members.Where(member =>
                member.PilotId != pilotId && member.Role != PvpRole.Scout && member.Status == RoamingGangMemberStatus.Active
            )
            .All(member =>
                Live!.HaulerCheckpoints.GetValueOrDefault(member.PilotId)?.Work.RouteIndex > Work.RouteIndex
                || visible.Contains(member.PilotId) && Live.HaulerCheckpoints.GetValueOrDefault(member.PilotId)?.Work.TargetId == gate.Id
            );
        if (ready)
        {
            _work = Work with { GangRendezvousUntil = 0 };
            return false;
        }
        if (Work.GangRendezvousUntil == 0)
        {
            _work = Work with { GangRendezvousUntil = checked(Work.ActiveTicks + RoamingGangRules.RendezvousTicks) };
        }
        if (Work.ActiveTicks >= Work.GangRendezvousUntil)
        {
            _work = Work with { Gang = ReturnGang(gang, "Rendezvous timed out; regrouping at home.") };
        }
        _navigation.SetAtRest();
        return true;
    }
}
