using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private const int MaximumFormationCandidates = 32;

    private static RoamingGangRecruit Recruit(DockedPilot pilot, CapsuleerDefenseProfile fit) =>
        new(
            new(pilot.PilotId, pilot.ShipId, fit.PvpRole) { Hull = (HullClass)pilot.ShipGroupId, DamageCapable = fit.HasDamage },
            pilot.Flight.MaxSpeed,
            fit.TackleRange > 0
        );

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
        SynchronizeRegional();
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
            gang.Phase is RoamingGangPhase.Forming or RoamingGangPhase.Hunting
            && (
                gang.Members.Any(member =>
                    member.Status == RoamingGangMemberStatus.Active
                    && !CapsuleerCorporations.CanFlyTogether(live, gang.LeaderId, member.PilotId)
                )
                || gang.Regional is { Objective: RegionalObjective.Escort } escort
                    && !CapsuleerCorporations.CanFlyTogether(live, gang.LeaderId, escort.TargetPilotId)
            )
        )
        {
            gang = ReturnGang(gang with { Sighting = null }, "Corporation affiliations changed. Returning home.");
            _work = Work with { Ratting = null };
        }
        if (
            !database.Settings.Capsuleers.RoamingGangs
            || !database.Settings.Capsuleers.RoamingPvp
            || !HasCommittedPvpShip
            || Work.Retreating
            || Work.Defense?.Retreating == true
        )
        {
            gang = ReturnGang(gang, "Withdrawing to preserve the surviving ships.");
        }
        if (gang.Phase == RoamingGangPhase.Forming && gang.RallyUntil > 0 && Work.ActiveTicks >= gang.RallyUntil)
        {
            gang = ReturnGang(gang, "The roaming gang could not assemble before its rally deadline.");
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

    private bool TryGangDeparture(bool authorizedHunt = false)
    {
        if (Live is null)
        {
            return false;
        }
        SynchronizeGang(reconcile: true);
        if (Work.Gang is not { } gang || gang.Phase == RoamingGangPhase.Complete)
        {
            if (!TryFormGang(authorizedHunt))
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
        if (TryGangRally(ref gang, member))
        {
            return true;
        }
        if (
            gang.Phase == RoamingGangPhase.Returning
            && (Pilot.StationId == gang.HomeStationId || Pilot.StationId == member.Rally?.StationId)
        )
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
                Journey = Work.Journey?.Kind == TrafficJourneyKind.GangRally ? null : Work.Journey,
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
        if (
            gang.Regional is not null
            && gang.HomeStationId == gang.DestinationStationId
            && !Work.RegionalObjectiveVisited
            && gang.Phase != RoamingGangPhase.Returning
        )
        {
            return LaunchLocalRegional(gang);
        }
        if (Pilot.StationId == gang.DestinationStationId)
        {
            if (WaitForRegionalShipment())
            {
                return true;
            }
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

    private bool TryFormGang(bool authorizedHunt)
    {
        var policy = database.Settings.Capsuleers;
        if (
            !policy.Enabled
            || !policy.RoamingGangs
            || !policy.RoamingPvp
            || !CanStartLocalPvp
            || !HasPvpShip
            || !authorizedHunt && _operation.World is Eden.World.Runtime.Economy.EconomyTransaction resident && DynamicWorkPilot(resident)
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
        if (!own.Combat || !own.CanContribute || CapsuleerDefenseRules.Retreat(health.Read(pilotId, Now), true))
        {
            return false;
        }
        var route =
            CapsuleerTrafficRoutes.Roam(_operation.Catalog, Pilot.StationId, pilotId ^ Work.ActivitySequence, policy)
            ?? new CapsuleerTrafficRoute(Pilot.StationId, Pilot.StationId, [Pilot.SystemId]);
        List<RoamingGangRecruit> candidates = [Recruit(Pilot, own)];
        Dictionary<long, HaulerProgress> schedules = [];
        RoamingGangMember[] members = [];
        int? regionalGroup = policy.RegionalConflicts ? RegionalConflictStore.GroupForPilot(Live!, database.Catalog, pilotId) : null;
        var world = _operation.World;
        foreach (var (row, rally) in GangRecruits(world))
        {
            var saved = world.Pilots.SingleOrDefault(other => other.Id == row.PilotId);
            if (
                saved is null
                || (CapsuleerCorporations.Organization(world, row.PilotId).ActiveWorker ?? CapsuleerCareer.Hauling)
                    != CapsuleerCareer.Hauling
                || saved.ActiveShip.Position is not null
                || saved.ActiveShip.StationId != (rally?.StationId ?? Pilot.StationId)
                || !CapsuleerIndustry.HasPvpShip(world, saved.Id, saved.ActiveShipId)
                || world.Set<SavedCapsuleerSuspension>().Find(row.PilotId) is not null
                || regionalGroup is not null
                    && PatrolRules.Unaffiliated(Pilot.CorpId, saved.CorporationId)
                    && RegionalConflictStore.GroupForPilot(Live!, database.Catalog, row.PilotId) != regionalGroup
            )
            {
                continue;
            }
            int? alliance = world.Set<SavedAllianceMember>().Find(saved.CorporationId)?.AllianceId;
            if (
                !PatrolRules.Unaffiliated(Pilot.CorpId, saved.CorporationId)
                && !PatrolRules.CorporateAllies(Pilot.CorpId, Pilot.Identity.AllianceId, saved.CorporationId, alliance)
            )
            {
                continue;
            }
            var work = Live!.HaulerCheckpoints.GetValueOrDefault(row.PilotId)?.Work ?? HaulerCheckpoint.Read(row.Progress);
            if (
                work is { Gang.Phase: not RoamingGangPhase.Complete }
                || work?.Freight?.Stacks.Length > 0
                || work?.Ratting is not null
                || work?.Exploration is not null
                || work?.MilitiaPatrol is not null
                || work?.RegionalEscort is not null
                || work?.Journey?.Kind is TrafficJourneyKind.PvpShopping or TrafficJourneyKind.CorporationRelocation
                || work?.Resupply?.DestinationStationId is not null
                || work?.CorporationSupply is not null
                || work?.SovereigntyExpedition is not null
                || work?.FactorySupply is not null
            )
            {
                continue;
            }
            var fit = CapsuleerDefenseProfile.Read(world, _operation.Catalog, row.PilotId);
            if (!fit.Combat || !fit.CanContribute)
            {
                continue;
            }
            var other = ships.FindPilot(world, saved.AccountId, saved.Id)!;
            if (
                world is Eden.World.Runtime.Economy.EconomyTransaction economy
                && CapsuleerIndustry.Employer(economy, saved.Id, saved.CorporationId) is not null
                && (
                    !CapsuleerIndustry.RequiresEarnedPvp(economy)
                        && CapsuleerIndustry.ReadyHuntReplacements(economy, _operation.Catalog, other)
                            < CapsuleerWorkRules.MinimumHuntingReplacements
                    || CapsuleerIndustry.HasStationFactoryJob(economy, other.PilotId, other.StationId)
                    || CapsuleerIndustry.HasWorkDemand(economy, _operation.Catalog, other, Now)
                )
            )
            {
                continue;
            }
            if (CapsuleerDefenseRules.Retreat(health.Read(other.PilotId, Now), true))
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
            var recruit = Recruit(other, fit);
            candidates.Add(recruit with { Member = recruit.Member with { Rally = rally } });
            schedules.Add(other.PilotId, work);
            if (candidates.Count >= policy.RoamingGangSize)
            {
                members = RoamingGangRules.SelectRoster(candidates, policy.RoamingGangSize);
                if (members.Length > 0)
                {
                    break;
                }
            }
        }
        if (candidates.Count < policy.RoamingGangSize)
        {
            members = RoamingGangRules.SelectRoster(candidates, policy.RoamingGangSize);
        }
        if (members.Length == 0)
        {
            return false;
        }
        var regional = PlanRegional(ref route);
        if (regional is null && (route.Systems.Length == 1 || RegionalLaunchBlocked()))
        {
            return false;
        }
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
        )
        {
            Regional = regional,
            Reason = regional?.Reason ?? "Hunting along a connected nullsec route.",
            RallyUntil = members.Any(member => member.Rally is not null) ? checked(Work.ActiveTicks + RoamingGangRules.RallyTicks) : 0,
        };
        LaunchRegional(gang);
        foreach (var member in members.Where(member => member.PilotId != pilotId))
        {
            _operation.GangEnrollments.Add(
                new(member.PilotId, schedules[member.PilotId] with { Gang = gang, GangReturning = false, RegionalObjectiveVisited = false })
            );
        }
        _work = Work with { Gang = gang, GangReturning = false, ActivitySequence = sequence, RegionalObjectiveVisited = false };
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
        if (Work.Journey is { Kind: TrafficJourneyKind.GangRally })
        {
            _work = Work with { GangReturning = true, Retreating = true, RouteDirection = -1 };
            BeginTravel();
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
        if (!InActiveGang || Work.GangReturning || Work.Journey?.Kind == TrafficJourneyKind.GangRally || GangMember?.Role == PvpRole.Scout)
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
