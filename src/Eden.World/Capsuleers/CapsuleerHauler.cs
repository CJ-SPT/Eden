using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Exploration;
using Eden.World.Fittings;
using Eden.World.Information;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerHaulerFactory(
    WorldDatabase database,
    SqliteWorld ships,
    CapsuleerGeometry systems,
    IPilotTackle tackle,
    INpcCombat combat,
    INpcSimulation encounters,
    INpcMissiles missiles,
    INpcElectronicWarfare warfare,
    IPilotHealth health,
    TimeProvider clock,
    MarketChangeSignal marketSignal,
    CapsuleerDefense defense,
    CapsuleerResupply resupply,
    CapsuleerRatting ratting,
    PilotInformationStore information,
    ICombatAnomalies anomalies,
    PirateExplorationStore hacking,
    CapsuleerIndustry ammunitionSupply,
    PilotEntosisStore entosis,
    CapsuleerCareerTraining careerTraining,
    PilotMissionStore missions,
    PilotFleetStore fleet
)
{
    internal CapsuleerHauler Create(long pilotId) =>
        new(
            pilotId,
            database,
            ships,
            systems,
            tackle,
            combat,
            encounters,
            missiles,
            warfare,
            health,
            clock,
            marketSignal,
            defense,
            resupply,
            ratting,
            information,
            anomalies,
            hacking,
            ammunitionSupply,
            entosis,
            careerTraining,
            missions,
            fleet
        );
}

internal sealed partial class CapsuleerHauler(
    long pilotId,
    WorldDatabase database,
    SqliteWorld ships,
    CapsuleerGeometry systems,
    IPilotTackle tackle,
    INpcCombat combat,
    INpcSimulation encounters,
    INpcMissiles missiles,
    INpcElectronicWarfare warfare,
    IPilotHealth health,
    TimeProvider clock,
    MarketChangeSignal marketSignal,
    CapsuleerDefense defense,
    CapsuleerResupply resupply,
    CapsuleerRatting ratting,
    PilotInformationStore information,
    ICombatAnomalies anomalies,
    PirateExplorationStore hacking,
    CapsuleerIndustry ammunitionSupply,
    PilotEntosisStore entosis,
    CapsuleerCareerTraining careerTraining,
    PilotMissionStore missions,
    PilotFleetStore fleet
)
{
    private SavedCapsuleerHauler? _founder;
    private DockedPilot? _pilot;
    internal DockedPilot? PreparationPilot => _pilot;
    internal CapsuleerTrafficRoute? PreparationRoute => _loaded ? _route : null;
    private HaulerProgress? _work;
    private NavigationState _navigation = new();
    private IReadOnlyList<StaticSpaceObject> _objects = [];
    private bool _resourceGeometry;
    private IReadOnlyList<StaticSpaceObject> Objects
    {
        get
        {
            if (_objects.Count == 0)
            {
                RefreshObjects();
            }
            return _objects;
        }
    }
    private StaticSpaceObject? _target;
    private IReadOnlyList<StaticSpaceObject> _obstacles = [];
    private TackleEffect[] _incoming = [];
    private long _tackleRevision = -1;
    private bool _warpBlocked;
    private CapsuleerTrafficRoute _route = null!;
    private long _threatenedShip;
    private bool _loaded;
    private bool _background;

    internal bool RequiresDetail =>
        (_work ?? Live?.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work)
            is { Defense: not null }
                or { Ratting: not null }
                or { Exploration: not null }
                or { Gang.Phase: RoamingGangPhase.Forming or RoamingGangPhase.Hunting or RoamingGangPhase.Returning }
                or { RegionalEscort: not null }
                or { CorporationSupply: not null }
                or { SovereigntyExpedition: not null }
        || _incoming.Length != 0
        || _warpBlocked
        || Volatile.Read(ref _threatenedShip) != 0;

    internal bool HasFlightInputs(int system, long catalog) =>
        _loaded
        && _pilot is { Position: not null } pilot
        && pilot.SystemId == system
        && _target is not null
        && _target.Id == _work?.TargetId
        && _work.Activity == HaulerActivity.Flying
        && !RequiresDetail
        && _flightCatalog == catalog;

    private long _flightCatalog;
    private bool _trafficRefreshChecked;
    private CapsuleerOperation _operation = null!;
    private long _hullRevision;
    private string? _snapshotFitRevision;
    private long _snapshotCatalogGeneration;

    private DockedPilot ReadPilot(WorldDbContext world, long account)
    {
        var result = ships.FindPilot(world, account, pilotId)!;
        _snapshotFitRevision = world.FitRevision(pilotId);
        _snapshotCatalogGeneration = database.Catalog.Identity.Generation;
        return result;
    }

    private LiveWorld? Live => LiveWorld.Find(database.Path);
    private DockedPilot Pilot => _pilot!;
    private HaulerProgress Work => _work!;
    private SavedCapsuleerHauler Founder => _founder!;
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public void ObserveAggression(long shipId)
    {
        if (_pilot?.ShipId == shipId)
        {
            Interlocked.Exchange(ref _threatenedShip, shipId);
        }
    }

    internal IReadOnlyList<int> RouteSystems => _route.Systems;

    internal bool IsRatting =>
        Live?.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work.Ratting is not null || _loaded && Work.Ratting is not null;

    internal bool HasJourney => _loaded && Work.Journey is not null;

    internal int SystemId
    {
        get
        {
            EnsureCurrentHull();
            return _pilot?.SystemId ?? throw new InvalidDataException("The scheduled capsuleer is missing.");
        }
    }

    internal HaulerObservation? Read(bool includeHealth = true)
    {
        EnsureCurrentHull();
        if (_work is not null)
        {
            SynchronizeGang();
        }
        RefreshCorporation();
        return _pilot is null ? null : Observation(includeHealth);
    }

    internal CapsuleerJob Resume(CapsuleerJob job, long elapsed, long? pausedAt = null)
    {
        EnsureCurrentHull();
        long delay = Math.Max(0, elapsed - Math.Max(Work.ActiveTicks, pausedAt ?? 0));
        // A visible flying ship needs its next motion pulse even when an older save paired its
        // alignment with a distant job deadline. Otherwise Destiny predicts a warp from a frozen grid.
        long latestFlightPulse = checked(elapsed + TimeSpan.TicksPerSecond);
        bool flying = Work.Activity == HaulerActivity.Flying && Pilot.Position is not null;
        if (
            pausedAt is null
            && delay == 0
            && Work.Ratting is null
            && (!flying || job.DueTicks <= latestFlightPulse)
            && (Work.WarpReadyAt > 0 || _navigation.WarpDestination is null || _navigation.Warp is not null)
        )
        {
            return job;
        }
        var resumed = CapsuleerPauseRules.Resume(Work, delay, Now) with { InboundPaused = false };
        if (
            Work.WarpReadyAt == 0
            && Pilot.Position is not null
            && _target is not null
            && _navigation.Warp is null
            && _navigation.WarpDestination is not null
        )
        {
            var navigation = NavigationState.Restore(resumed.Navigation, Pilot.Flight);
            long ready = CapsuleerWarpRules.Begin(navigation, WarpOrder(_target), elapsed);
            resumed = resumed with { WarpReadyAt = ready, Navigation = navigation.Capture() };
        }
        long due = Math.Max(checked(elapsed + (pausedAt is null ? TimeSpan.TicksPerSecond : 1)), checked(job.DueTicks + delay));
        var next = job.Next(flying ? Math.Min(latestFlightPulse, due) : due, job.Action);
        using CapsuleerOperation operation = new(database, job, marketSignal);
        operation.Commit(next, null, new HaulerCheckpoint(pilotId, resumed));
        _navigation = NavigationState.Restore(resumed.Navigation, Pilot.Flight);
        _work = resumed;
        return next;
    }

    internal CapsuleerJob Reappear(CapsuleerJob job, long elapsed, int systemId)
    {
        if (!_loaded || Live is { } live && _hullRevision != live.CapsuleerHullRevisions.GetValueOrDefault(pilotId))
        {
            Load();
            _hullRevision = Live?.CapsuleerHullRevisions.GetValueOrDefault(pilotId) ?? 0;
        }
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup || Pilot.SystemId == systemId)
        {
            return job;
        }
        var intent = Pilot.Position is null
            ? Work with
            {
                RouteIndex = Pilot.StationId == _route.SourceStationId ? 0 : _route.Systems.Length - 1,
                RouteDirection = Pilot.StationId == _route.SourceStationId ? 1 : -1,
            }
            : Work;
        var leg = CapsuleerTrafficFlight.Leg(_route, intent, Pilot.SystemId);
        bool completeEndpointVisit = Pilot.Position is not null && leg.NextSystemId is null;
        if (completeEndpointVisit)
        {
            // An inbound endpoint encounter can finish its dock visit during the
            // absence, then depart on the adjacent reverse leg.
            leg = CapsuleerTrafficFlight.Leg(
                _route,
                intent with
                {
                    RouteIndex = leg.Index,
                    RouteDirection = -leg.Direction,
                },
                Pilot.SystemId
            );
        }
        // A dormant encounter can account for one unwitnessed leg. It cannot
        // bypass intermediate systems simply because a distant observer appears.
        if (leg.NextSystemId != systemId)
        {
            return job;
        }
        RefreshTackle();
        if (_warpBlocked)
        {
            return job;
        }
        var resumed = Resume(job, elapsed);
        using CapsuleerOperation operation = new(
            database,
            resumed,
            marketSignal,
            declared: new(systems: [Pilot.SystemId, systemId], pilots: [pilotId], ships: [Pilot.ShipId])
        );
        try
        {
            var departure = Objects.Where(o => o.DestinationSystemId == systemId).OrderBy(o => o.Id).FirstOrDefault();
            if (departure?.DestinationGateId is not { } exit)
            {
                return resumed;
            }
            var saved = operation.World.Pilots.Single(p => p.Id == pilotId);
            if (saved.ActiveShipId != Work.ShipId)
            {
                operation.Dispose();
                Load();
                return resumed;
            }
            Eden.World.Combat.CrimewatchPersistence.CheckTravel(operation.World, operation.Catalog, saved, CrimewatchTravel.Warp, Now);
            if (completeEndpointVisit)
            {
                int station = leg.Direction > 0 ? _route.SourceStationId : _route.DestinationStationId;
                var endpoint = Objects.Single(o => o.Id == station);
                var approach =
                    endpoint.Position
                    + new SpacePosition(endpoint.Radius + Pilot.Flight.ShipRadius + CapsuleerTrafficFlight.ApproachRange, 0, 0);
                SqliteWorld.SetPosition(operation.World, operation.Catalog, Pilot.ShipId, approach, Pilot.SystemId, Now);
                ships.Dock(operation.World, operation.Catalog, pilotId, Pilot.ShipId, station);
            }
            Eden.World.Combat.CrimewatchPersistence.CheckTravel(
                operation.World,
                operation.Catalog,
                saved,
                CrimewatchTravel.Jump,
                Now,
                systemId
            );
            // The absence already exceeds a conservative travel allowance. Account
            // for that hidden flight at the departure gate, then use the same
            // reciprocal-gate validation and arrival clearance as witnessed travel.
            var departurePosition =
                departure.Position
                + new SpacePosition(departure.Radius + Pilot.Flight.ShipRadius + CapsuleerTrafficFlight.ApproachRange, 0, 0);
            SqliteWorld.SetPosition(operation.World, operation.Catalog, Pilot.ShipId, departurePosition, Pilot.SystemId, Now);
            var transfer = PilotGateTravelStore.TransferDetailed(
                operation.World,
                operation.Catalog,
                pilotId,
                Pilot.ShipId,
                departure.Id,
                exit,
                Now
            );
            _pilot = ReadPilot(operation.World, transfer.AccountId);
            _navigation.AdoptHull();
            _operation = operation;
            RefreshObjects();
            _work = Work with
            {
                Activity = HaulerActivity.Flying,
                RouteIndex = leg.Index + leg.Direction,
                RouteDirection = leg.Direction,
                Navigation = _navigation.Capture(),
                ArrivalUntil = Work.Stamp + GateTravelRules.ArrivalTicks,
                ArrivalGateId = null,
                ArrivalAt = null,
                DepartureAt = null,
            };
            BeginTravel();
            _work = Work with { Navigation = _navigation.Capture() };
            var next = resumed.Next(resumed.DueTicks, CapsuleerAction.Arrival);
            operation.TravelChanged = true;
            operation.Commit(next, null, new HaulerCheckpoint(pilotId, Work));
            return next;
        }
        catch (InvalidDataException)
        {
            operation.Dispose();
            Load();
            return resumed;
        }
        catch
        {
            operation.Dispose();
            Load();
            throw;
        }
    }

    internal CapsuleerJob Execute(CapsuleerJob job, int destinyStamp, bool background = false, PreparedJourney? preparedJourney = null)
    {
        _regionalReports.Clear();
        _preparedJourney = preparedJourney;
        if (!_loaded)
        {
            Load();
        }
        if (_pilot is null)
        {
            throw new InvalidDataException("The scheduled capsuleer is missing.");
        }
        if (Live is { } live && _hullRevision != live.CapsuleerHullRevisions.GetValueOrDefault(pilotId))
        {
            _hullRevision = live.CapsuleerHullRevisions.GetValueOrDefault(pilotId);
            if (!live.Economy.ActiveShip(pilotId, Pilot.ShipId))
            {
                Load();
            }
        }
        bool canWork = CanWorkHere;
        if (!canWork && !CanContinueTransit())
        {
            using CapsuleerOperation hold = new(database, job, marketSignal);
            var next = CapsuleerWorkRegions.Retry(job, Live?.CapsuleerActivity.Read().ElapsedTicks ?? job.DueTicks);
            _work = CapsuleerPauseRules.Resume(Work, Math.Max(0, job.DueTicks - Work.ActiveTicks), Now) with
            {
                WaitingReason = CapsuleerWorkRegions.TransitOnlyReason,
            };
            hold.Commit(next, null, new HaulerCheckpoint(pilotId, Work));
            return next;
        }
        if (Work.WaitingReason == CapsuleerWorkRegions.TransitOnlyReason)
        {
            _work = Work with { WaitingReason = null };
        }
        RefreshCorporation();
        if (canWork)
        {
            SynchronizeGang(reconcile: true);
        }
        _background = background && !RequiresDetail;
        if (canWork && !_background && Work.Activity != HaulerActivity.Inbound)
        {
            ObserveDanger(destinyStamp);
        }
        if (Pilot.Position is not null)
        {
            RefreshTackle();
            _background = _background && !RequiresDetail;
        }
        if (!_background && !_resourceGeometry)
        {
            RefreshObjects();
        }
        AdvanceRattingClock(job.DueTicks);
        if (
            Work.Ratting is { } sortie
            && !(
                sortie.Patrol is { } patrol ? PatrolAllowed(patrol)
                : sortie.AnomalyPosition is null ? database.Settings.Capsuleers.BeltRatting
                : database.Settings.Capsuleers.AnomalyRatting
            )
        )
        {
            EndSortie(sortie);
        }
        bool capsule = Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup;
        var proactive = capsule || !canWork ? null : ProactiveTarget();
        var combatRole = canWork && InActiveGang ? GangMember?.Role : null;
        if (combatRole == PvpRole.Scout && proactive is { Npc: false })
        {
            combatRole = PvpRole.Tackle;
        }
        var previousDefense = Work.Defense;
        var defended =
            _background || capsule
                ? new CapsuleerDefenseResult(Pilot, null, false, false, false)
                : defense.Advance(
                    Pilot,
                    _navigation,
                    previousDefense,
                    Now,
                    destinyStamp,
                    _obstacles,
                    Work.Retreating,
                    Work.Activity is not (HaulerActivity.Jumping or HaulerActivity.Inbound) && Work.Stamp >= Work.ArrivalUntil,
                    proactive: proactive,
                    pvpRole: combatRole,
                    gang: canWork && InActiveGang ? Work.Gang : null
                );
        _pilot = defended.Pilot;
        _warpBlocked = defended.WarpBlocked ?? _warpBlocked;
        _work = Work with { Defense = defended.State, LastMotionStamp = defended.HoldsNavigation ? destinyStamp : Work.LastMotionStamp };
        if (defended.NewThreat)
        {
            _work = Work with
            {
                Routine = TravelerRoutineRules.Threatened(
                    Work.Routine ?? TravelerRoutineRules.Create(pilotId),
                    Pilot.SystemId,
                    job.DueTicks
                ),
            };
        }
        if (defended.Escape && !Work.Retreating)
        {
            ObserveAggression(Pilot.ShipId);
        }
        bool resumeCourse = defended.ResumeNavigation;
        if (Pilot.Position is not null && Volatile.Read(ref _threatenedShip) == Pilot.ShipId)
        {
            _work = Work with
            {
                Routine = TravelerRoutineRules.Threatened(
                    Work.Routine ?? TravelerRoutineRules.Create(pilotId),
                    Pilot.SystemId,
                    job.DueTicks
                ),
            };
        }
        var beforePilot = Pilot;
        var before = Work;
        var beforeRoute = _route;
        var beforeTarget = _target;
        var beforeObjects = _objects;
        bool beforeResourceGeometry = _resourceGeometry;
        var beforeObstacles = _obstacles;
        long stepTicks = job.DueTicks;
        _work = Work with { ActiveTicks = stepTicks, Stamp = CapsuleerScheduleRules.Stamp(stepTicks, Work.ClockOffsetTicks) };
        _operation = new(database, job, marketSignal);
        try
        {
            if (capsule && Pilot.Position is not null)
            {
                _work = Work with { SovereigntyExpedition = null, HomeDefenseAllianceId = null };
                AdvanceCapsuleEscape();
                return Commit(job);
            }
            // Scouting can populate the system's public sites. Finish that work before opening the station transaction.
            var scoutedAnomaly = capsule || !canWork ? null : ScoutDepartureAnomaly();
            var signatures = canWork ? ExplorationSignatures() : [];
            if (canWork)
            {
                SynchronizeGang(reconcile: true);
                PublishRegionalReports();
            }
            if (canWork && defended.NewThreat)
            {
                RegionalConflictStore.Attack(
                    _operation,
                    database,
                    Pilot,
                    Work.Defense,
                    Work.Ratting is { } activity ? activity.Belts[activity.BeltIndex] : Work.TargetId,
                    Work.ActiveTicks
                );
            }
            if (canWork)
            {
                ApplyGangReturn();
            }
            if (Pilot.Position is not null && _warpBlocked && _navigation.Warp is null && _navigation.WarpDestination is not null)
            {
                throw new InvalidDataException("Warp disruption interrupts the hauler's alignment.");
            }
            if (
                canWork
                && Volatile.Read(ref _threatenedShip) == Pilot.ShipId
                && Pilot.Position is not null
                && Work.Activity != HaulerActivity.Jumping
                && !Work.Retreating
            )
            {
                BeginRetreat();
            }
            if (!defended.HoldsNavigation)
            {
                if (resumeCourse && Pilot.Position is not null && Work.Ratting is null && Work.Exploration is null)
                {
                    BeginTravel();
                }
                else if (resumeCourse && Work.Ratting is { Phase: RattingPhase.Searching, Patrol.Kind: PatrolKind.Transit })
                {
                    _navigation.SetAtRest();
                }
                Step(scoutedAnomaly, signatures);
            }
            return Commit(job);
        }
        catch (Exception error) when (error is InvalidDataException or CargoCapacityException)
        {
            _trafficRefreshChecked = false;
            _operation.Dispose();
            _pilot = beforePilot;
            _work = before;
            _route = beforeRoute;
            _navigation = NavigationState.Restore(before.Navigation, beforePilot.Flight);
            _target = beforeTarget;
            _objects = beforeObjects;
            _resourceGeometry = beforeResourceGeometry;
            _obstacles = beforeObstacles;
            RestorePosition(beforePilot);
            _operation = new(database, job, marketSignal);
            _navigation.AdoptHull();
            _work = Work with
            {
                ActiveTicks = stepTicks,
                Stamp = CapsuleerScheduleRules.Stamp(stepTicks, Work.ClockOffsetTicks),
                Activity = HaulerActivity.Waiting,
                DueStamp = CapsuleerScheduleRules.Stamp(stepTicks, Work.ClockOffsetTicks) + HaulingRules.PollSeconds,
                DueTicks = checked(stepTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks),
                WaitingReason = error.Message,
            };
            try
            {
                return Commit(job);
            }
            catch
            {
                _pilot = beforePilot;
                _work = before;
                _route = beforeRoute;
                _navigation = NavigationState.Restore(before.Navigation, beforePilot.Flight);
                _target = beforeTarget;
                _objects = beforeObjects;
                _resourceGeometry = beforeResourceGeometry;
                _obstacles = beforeObstacles;
                RestorePosition(beforePilot);
                throw;
            }
        }
        catch
        {
            _pilot = beforePilot;
            _trafficRefreshChecked = false;
            _work = before;
            _route = beforeRoute;
            _navigation = NavigationState.Restore(before.Navigation, beforePilot.Flight);
            _target = beforeTarget;
            _objects = beforeObjects;
            _resourceGeometry = beforeResourceGeometry;
            _obstacles = beforeObstacles;
            RestorePosition(beforePilot);
            throw;
        }
        finally
        {
            _operation.Dispose();
        }
    }

    private void RestorePosition(DockedPilot pilot)
    {
        if (pilot.Position is { } position)
        {
            Live?.CombatForSystem(pilot.SystemId)?.PublishPilotPosition(pilot.ShipId, pilot.SystemId, position);
        }
    }

    private CapsuleerJob Commit(CapsuleerJob job)
    {
        if (CanWorkHere)
        {
            FinishRegional();
        }
        _work = Work with { Navigation = _navigation.Capture() };
        var schedule = Schedule();
        var next = _operation.IncomingMiner is { } incoming
            ? job.Next(incoming.Work.DueTicks, CapsuleerAction.Prepare)
            : job.Next(schedule.DueTicks, schedule.Action);
        _operation.Commit(next, null, new HaulerCheckpoint(pilotId, Work));
        if (Work.Ratting is { Phase: RattingPhase.TravellingToBelt, Patrol.Camp: { } camp })
        {
            defense.Camps.At(Pilot.SystemId, camp.GateId, camp.Until, Work.ActiveTicks)?.Join(pilotId, false, camp.Role);
        }
        Interlocked.CompareExchange(ref _threatenedShip, 0, Pilot.ShipId);
        return next;
    }

    private CapsuleerSchedule Schedule()
    {
        var schedule = CapsuleerScheduleRules.Next(Work, Pilot.Position is not null, _background);
        if (_background && Work.WarpReadyAt > Work.ActiveTicks)
        {
            schedule = schedule with { DueTicks = Work.WarpReadyAt };
        }
        else if (
            _background
            && Work.Defense is null
            && CapsuleerScheduleRules.WarpDeadline(Work.Navigation, Pilot.Flight, Work.ClockOffsetTicks, Work.ActiveTicks) is { } exit
        )
        {
            schedule = schedule with { DueTicks = exit };
        }
        else if (_background && Work.Defense is null && Work.Activity == HaulerActivity.Flying && !_warpBlocked)
        {
            schedule = schedule with
            {
                DueTicks = CapsuleerScheduleRules.BackgroundFlightDeadline(
                    Work,
                    Pilot.Flight,
                    Pilot.Position!,
                    _target!,
                    CapsuleerTrafficFlight.ApproachRange
                ),
            };
        }
        else if (_background && Work.Defense is null && Work.Activity == HaulerActivity.Jumping && Work.DueTicks > Work.ActiveTicks)
        {
            schedule = schedule with { DueTicks = Work.DueTicks };
        }
        schedule = schedule with { DueTicks = CapsuleerDefenseRules.NextDeadline(Work.Defense, Now, Work.ActiveTicks, schedule.DueTicks) };
        return schedule;
    }

    internal bool Sample(long throughTicks)
    {
        if (Live is not { } live || !_loaded || _pilot?.Position is null || _navigation.Warp is not { } warp)
        {
            return false;
        }
        SynchronizeGang();
        int stamp = CapsuleerScheduleRules.Stamp(throughTicks, Work.ClockOffsetTicks);
        if (
            stamp <= Work.Stamp
            || warp.Sample(stamp, Math.Min(Pilot.Flight.MaxSpeed / 2, CapsuleerScheduleRules.MaximumWarpExitSpeed)).Exited
        )
        {
            return false;
        }
        var tick = _navigation.Advance(Pilot.ShipId, stamp, Pilot.Flight, Pilot.Position!, _ => null, []);
        _pilot = Pilot with { Position = tick.Position };
        _work = Work with
        {
            Stamp = stamp,
            ActiveTicks = checked(Work.ClockOffsetTicks + stamp * TimeSpan.TicksPerSecond),
            Navigation = _navigation.Capture(),
        };
        live.CombatForSystem(Pilot.SystemId)?.PublishPilotPosition(Pilot.ShipId, Pilot.SystemId, tick.Position);
        live.HaulerCheckpoints[pilotId] = new(pilotId, Work);
        live.ObjectsChanged();
        return true;
    }

    private HaulerObservation Observation(bool includeHealth = true) => new(Pilot, Work, includeHealth ? health.Read(pilotId, Now) : null);

    private void Load()
    {
        using var transaction = new WorldActionScope(database, new(pilots: [pilotId]));
        var world = transaction.World;
        _founder = world.Set<SavedCapsuleerHauler>().AsNoTracking().SingleOrDefault(h => h.PilotId == pilotId);
        _loaded = true;
        if (_founder is null)
        {
            return;
        }
        long account = world.Pilots.Where(p => p.Id == Founder.PilotId).Select(p => p.AccountId).Single();
        _pilot = ReadPilot(world, account);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var savedWork = HaulerCheckpoint.Read(Founder.Progress);
        _route = CapsuleerTrafficFlight.Route(Founder, catalog, savedWork?.Journey);
        _incoming = [];
        _tackleRevision = -1;
        _warpBlocked = false;
        _work = Live?.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work ?? savedWork;
        if (_work is { Ratting.Patrol.Camp: { } camp } && Work.ShipId == Pilot.ShipId)
        {
            defense.Camps.At(Pilot.SystemId, camp.GateId, camp.Until, Work.ActiveTicks)?.Join(pilotId, false, camp.Role);
        }
        var previous = _work;
        long anchor =
            previous?.ActiveTicks
            ?? (
                (
                    Live?.CapsuleerJobs.GetValueOrDefault(pilotId)?.DueTicks
                    ?? world.Set<SavedCapsuleerJob>().Single(j => j.PilotId == pilotId).DueTicks
                ) - TimeSpan.TicksPerSecond
            );
        if (_work is null || Work.ShipId != Pilot.ShipId)
        {
            _navigation = new();
            _work = new(
                Pilot.Position is null ? HaulerActivity.Docked : HaulerActivity.Waiting,
                0,
                0,
                Pilot.ShipId,
                Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup ? _route.SourceStationId : _route.DestinationStationId,
                0,
                _navigation.Capture(),
                RouteDirection: Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup ? -1 : 1,
                TrafficLane: previous?.TrafficLane,
                DwellSequence: previous?.DwellSequence ?? 0
            )
            {
                CareerPlan = previous?.CareerPlan,
                Routine = previous?.Routine,
                Defense = previous?.Defense,
                Gang = previous?.Gang,
                GangReturning = previous?.GangReturning ?? false,
                RegionalEscort = previous?.RegionalEscort,
                PlanetaryProduction = previous?.PlanetaryProduction,
                PlanetaryRetryAt = previous?.PlanetaryRetryAt ?? 0,
                RegionalStockRetryAt = previous?.RegionalStockRetryAt ?? 0,
                RegionalStockEmptySupplier = previous?.RegionalStockEmptySupplier ?? 0,
            };
        }
        if (previous is null || previous.ShipId != Pilot.ShipId)
        {
            _work = Work with
            {
                ActiveTicks = anchor,
                ClockOffsetTicks = previous?.ClockOffsetTicks ?? anchor,
                Stamp = previous?.Stamp ?? 0,
            };
        }
        if (Work.Journey is { } journey)
        {
            _route = new(journey.SourceStationId, journey.DestinationStationId, journey.Systems);
        }
        if (Founder.RouteSystems is null && Work.Journey is null)
        {
            // Older direct routes had only their destination station as a cursor.
            _work = Work with
            {
                RouteDirection = Work.DestinationStationId == _route.SourceStationId ? -1 : 1,
            };
        }
        _work = CapsuleerPauseRules.ExpireArrival(Work, Now);
        _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
        LoadInboundDwells(catalog);
        var office =
            Work.CorporationSupply?.FactorySupply?.Planetary is { Phase: PlanetaryVisitPhase.Orbit } visit
            && Work.TargetId == Eden.Gameplay.Planetary.PlanetaryRules.OfficeId(visit.Production.PlanetId)
                ? CapsuleerIndustry.PlanetaryOffice(world, catalog, visit.Production.PlanetId)
                : null;
        var refinery =
            Work.CorporationSupply?.FactorySupply?.Refinery is { Phase: RefineryVisitPhase.OnGrid } build
            && Work.TargetId == (build.Task.StructureId ?? Eden.World.Structures.MoonMiningCatalog.BeaconId(build.Task.MoonId))
                ? CapsuleerIndustry.RefineryTarget(world, catalog, build.Task)
                : null;
        var hub =
            Work.SovereigntyExpedition is { HubId: long hubId } && Work.TargetId == hubId
                ? PilotEntosisStore.ExpeditionTarget(world, catalog, Pilot.SystemId, hubId)
                : null;
        transaction.Commit();
        transaction.Dispose();
        var activity = Live?.CapsuleerActivity.Read();
        RefreshObjects(
            loadResources: activity is not { Active: true } || activity.ActiveSystems.Contains(Pilot.SystemId) || RequiresDetail
        );
        _target =
            hub
            ?? refinery
            ?? office
            ?? _objects.FirstOrDefault(o => o.Id == Work.TargetId)
            ?? SiteTarget(Work.Ratting)
            ?? ExplorationTarget(Work.Exploration);
    }

    private void RefreshObjects(bool? loadResources = null)
    {
        using var phase = SessionPerformance.Phase("traffic.geometry");
        _flightCatalog = database.Catalog.Identity.Generation;
        _resourceGeometry = loadResources ?? !_background;
        if (!_resourceGeometry)
        {
            // Background travel only needs the static route geometry until detailed work begins.
            using CatalogDbContext catalog = new(database.CatalogPath);
            _objects = Live?.TrafficGeometry.StaticObjects(catalog, Pilot.SystemId) ?? SystemObjects.Geometry(catalog, Pilot.SystemId);
            _obstacles = [];
            return;
        }
        _objects = _operation is { HasWorld: true }
            ? CapsuleerGeometry.Load(_operation.World, _operation.Catalog, Pilot.SystemId, Now)
            : systems.Load(Pilot.SystemId);
        _obstacles = _objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup).ToArray();
    }

    private void RefreshTackle()
    {
        // A demoted system discards its read views. Known-absent hostility is still valid
        // until either owning table commits a change; exact active effects keep their normal reads.
        var live = Live;
        if (_background && _incoming.Length == 0 && live is not null && _tackleRevision == live.HostilityRevision)
        {
            return;
        }
        var incoming = tackle.Incoming(Pilot.ShipId).ToArray();
        _tackleRevision = live?.HostilityRevision ?? -1;
        if (_incoming.SequenceEqual(incoming))
        {
            return;
        }
        _incoming = incoming;
        var attributes = tackle.ShipAttributes(Pilot.PilotId);
        _warpBlocked = attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus) > 0;
        _pilot = Pilot with
        {
            Ship = Pilot.Ship with
            {
                Flight = Pilot.Flight with { MaxSpeed = (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity) },
            },
        };
    }

    private void Step(CombatAnomaly? scoutedAnomaly, IReadOnlyList<CombatAnomaly>? signatures = null)
    {
        if (Work.Exploration is not null)
        {
            ExplorationStep(signatures ?? []);
            return;
        }
        if (Work.Ratting is not null)
        {
            RattingStep();
            return;
        }
        switch (Work.Activity)
        {
            case HaulerActivity.Docked when Work.ActiveTicks >= Work.DueTicks:
                TradeAndLaunch(scoutedAnomaly);
                break;
            case HaulerActivity.Flying:
                Fly();
                break;
            case HaulerActivity.Inbound when Work.ActiveTicks >= Work.DueTicks:
                CompleteInbound();
                break;
            case HaulerActivity.Jumping when Work.ActiveTicks >= Work.DueTicks:
                CompleteJump();
                break;
            case HaulerActivity.Waiting when Work.ActiveTicks >= Work.DueTicks:
                _work = Work with { WaitingReason = null };
                if (!CanWorkHere)
                {
                    BeginTravel();
                }
                else if (Pilot.Position is not null && Work.SovereigntyExpedition is { Returning: true } returning)
                {
                    ContinueSovereigntyReturn(returning);
                }
                else if (
                    Pilot.Position is not null
                    && Work.SovereigntyExpedition is { Returning: false, HubId: not null } claiming
                    && Pilot.SystemId == claiming.TargetSystemId
                )
                {
                    AdvanceSovereignty(claiming);
                }
                else if (Pilot.Position is null)
                {
                    _work = Work with { Activity = HaulerActivity.Docked };
                }
                else if (Work.CorporationSupply?.FactorySupply?.Planetary is { Phase: PlanetaryVisitPhase.Orbit } visit)
                {
                    BeginPlanetaryOrbit(visit);
                }
                else if (Work.CorporationSupply?.FactorySupply?.Refinery is { Phase: RefineryVisitPhase.OnGrid } build)
                {
                    BeginRefineryApproach(build);
                }
                else
                {
                    BeginTravel();
                }
                break;
        }
    }

    private bool CanWorkHere => CapsuleerPopulationRules.AllowsWork(database.Settings.Capsuleers, Pilot.SystemId, Pilot.RegionId);

    private bool CanContinueTransit() =>
        Pilot.Position is not null
        && Work is { Ratting: null, Exploration: null }
        && (
            _target?.DestinationGateId is not null
                && Work.Activity is HaulerActivity.Flying or HaulerActivity.Jumping or HaulerActivity.Waiting
            || Work.Activity == HaulerActivity.Inbound && Work.TrafficLane is not null
        );

    private void AdvanceCapsuleEscape()
    {
        if (Work.Activity == HaulerActivity.Waiting && Work.ActiveTicks < Work.DueTicks)
        {
            return;
        }
        if (Work.Journey?.Kind == TrafficJourneyKind.CapsuleEscape)
        {
            Step(null);
            return;
        }
        _route =
            CapsuleerTrafficRoutes.EscapeRoute(_operation.Catalog, Pilot.SystemId, database.Settings.Capsuleers)
            ?? throw new InvalidDataException("The capsule has no reachable NPC station.");
        _work = Work with
        {
            RouteIndex = _route.Systems.Length - 1,
            RouteDirection = -1,
            Retreating = true,
            WaitingReason = null,
            Ratting = null,
            Exploration = null,
            TrafficLane = null,
            LaneDeparting = false,
            Journey = new(
                TrafficJourneyKind.CapsuleEscape,
                Founder.SourceStationId,
                _route.SourceStationId,
                _route.DestinationStationId,
                _route.Systems,
                Work.DwellSequence
            ),
        };
        BeginTravel();
    }

    private InventoryItem[] Ore(long location) =>
        PilotInventoryStore
            .List(_operation.World, _operation.Catalog, Pilot.PilotId, location)
            .Where(i =>
                i.CategoryId == AsteroidDefinitions.Category
                && (i.FlagId == (int)InventoryFlag.Cargo || i.FlagId == (int)InventoryFlag.Hangar)
            )
            .OrderBy(i => i.Id)
            .ToArray();

    internal void RefreshCorporation()
    {
        if (
            _pilot is not null
            && Live is { } live
            && live.Economy.CorporationAffiliation(pilotId) != (_pilot.CorpId, _pilot.Identity.AllianceId)
        )
        {
            var current = ships.FindPilot(CapsuleerCommunityStore.MinerAccountId, pilotId)!;
            _pilot = _pilot with { Identity = current.Identity };
        }
    }

    private void JoinCorporationAtDock()
    {
        if (
            Live is not null
            && _operation.World is EconomyTransaction world
            && CapsuleerCorporations.Place(world, _operation.Catalog, database, pilotId, Now, Work.ActiveTicks)
        )
        {
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
        }
    }

    private void TradeAndLaunch(CombatAnomaly? scoutedAnomaly)
    {
        using var phase = SessionPerformance.Phase("traffic.tradeAndLaunch");
        if (database.Settings.FreshUniverse && _operation.World is EconomyTransaction freshWorld)
        {
            WorkFreshCareer(freshWorld, scoutedAnomaly);
            return;
        }
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            _work = Work with { SovereigntyExpedition = null, HomeDefenseAllianceId = null };
            AttemptShipReplacement();
            return;
        }
        if (_operation.World is EconomyTransaction explorationWorld && TryLaunchExploration(explorationWorld))
        {
            return;
        }
        if (_operation.World is EconomyTransaction resident)
        {
            var profession =
                CapsuleerCareerTraining.Profession(_operation.Catalog, Pilot.ShipTypeId, (HullClass)Pilot.ShipGroupId)
                ?? Work.CareerPlan?.Profession
                ?? CapsuleerProfession.Hauling;
            var training = careerTraining.Advance(resident, _operation.Catalog, Pilot, profession, Work.CareerPlan, Now);
            _work = Work with { CareerPlan = training.Plan };
            _operation.MarketChanged |= training.MarketChanged;
            if (training.LevelChanged)
            {
                _pilot = ReadPilot(resident, CapsuleerCommunityStore.MinerAccountId);
            }
            if (
                Work.Freight is null
                && resident.Set<SavedTrafficIdentity>().Find(pilotId) is null
                && !PilotInventoryStore
                    .List(resident, _operation.Catalog, pilotId, Pilot.ShipId)
                    .Any(item => ShipHoldRules.IsHold(item.FlagId) && item.CategoryId != (int)InventoryCategory.Charge)
            )
            {
                var acquisition = ammunitionSupply.AdvanceCareerShip(resident, _operation.Catalog, Pilot, training.Plan, Now);
                _operation.MarketChanged |= acquisition.MarketChanged;
                if (acquisition.Boarded)
                {
                    _pilot = ReadPilot(resident, CapsuleerCommunityStore.MinerAccountId);
                    ResetTradeHull();
                }
            }
        }
        JoinCorporationAtDock();
        if (TryCorporationRelocation())
        {
            return;
        }
        if (TrySovereigntyExpedition())
        {
            return;
        }
        if (TryCorporationSupply())
        {
            return;
        }
        if (TryDynamicDockWork(scoutedAnomaly))
        {
            return;
        }
        bool upgraded =
            Work.Freight is null
            && Pilot.ShipTypeId == HaulingRules.BadgerType
            && CapsuleerFreight.Upgrade(_operation.World, _operation.Catalog, pilotId, Pilot.ShipId);
        if (upgraded)
        {
            _pilot = ReadPilot(_operation.World, CapsuleerCommunityStore.MinerAccountId);
            _navigation.AdoptHull();
        }
        if (!_trafficRefreshChecked && database.Traffic.Profiles.Count > 0)
        {
            if (TrafficProvisioning.Refresh(database, _operation.World, _operation.Catalog, pilotId, _route, Now))
            {
                long account = _operation.World.Pilots.Where(value => value.Id == pilotId).Select(value => value.AccountId).Single();
                _pilot = ReadPilot(_operation.World, account);
                _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
            }
            _trafficRefreshChecked = true;
        }
        if (TrafficCorrection.Apply(database, _operation.World, _operation.Catalog, pilotId, Now))
        {
            long account = _operation.World.Pilots.Where(p => p.Id == pilotId).Select(p => p.AccountId).Single();
            _pilot = ReadPilot(_operation.World, account);
            _navigation.AdoptHull();
        }
        CapsuleerSupplyStore.PrepareCombatSkills(_operation.World, _operation.Catalog, pilotId);
        CapsuleerSupplyStore.Ensure(_operation.World, _operation.Catalog, pilotId, Founder.FounderShipId);
        UnloadRattingLoot();
        var supplies = resupply.Dock(database, _operation.World, _operation.Catalog, pilotId, Work.Resupply, Now);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        if (TryGangDeparture())
        {
            return;
        }
        if (TryLaunchActivity(scoutedAnomaly))
        {
            return;
        }
        bool restrictedRoute = !database.Settings.Capsuleers.CanUse(_operation.Catalog.Definitions, _route);
        if (restrictedRoute && Work.Freight is not null)
        {
            Wait("The saved freight route needs a destination that allows bot work.", (int)CapsuleerWorkRegions.HoldInterval.TotalSeconds);
            return;
        }
        if (restrictedRoute || Work.Retreating || Work.Journey is null || Pilot.StationId == Work.Journey.DestinationStationId)
        {
            AssignJourney();
        }
        var freight = Work.Freight;
        int destination = Pilot.StationId == _route.DestinationStationId ? _route.SourceStationId : _route.DestinationStationId;
        if (
            freight?.Destination == Pilot.StationId
            || upgraded
            || (
                freight is null
                && Pilot.ShipGroupId == (int)HullClass.Hauler
                && _operation.World.Set<SavedTrafficIdentity>().Find(pilotId) is not null
            )
        )
        {
            if (freight is not null)
            {
                bool intact =
                    Work.RegionalEscort is null
                    || freight.Stacks.Length > 0
                        && freight.Stacks.All(stack =>
                            _operation.World.Set<OwnedItem>().Find(stack.ItemId) is { } item
                            && item.OwnerId == pilotId
                            && item.LocationId == Pilot.ShipId
                            && item.TypeId == stack.TypeId
                            && item.Quantity >= stack.Quantity
                        );
                CapsuleerFreight.Deliver(_operation.World, pilotId, Pilot.ShipId, freight);
                DeliverRegionalShipment(intact);
            }
            freight = CapsuleerFreight.Load(
                _operation.World,
                _operation.Catalog,
                pilotId,
                Pilot.ShipId,
                Pilot.ShipTypeId,
                destination,
                checked((freight?.Sequence ?? -1) + 1)
            );
        }
        _work = Work with
        {
            DestinationStationId = destination,
            Freight = freight is not null && freight.Destination != destination ? freight with { Destination = destination } : freight,
            RouteIndex = Pilot.StationId == _route.SourceStationId ? 0 : _route.Systems.Length - 1,
            RouteDirection = Pilot.StationId == _route.SourceStationId ? 1 : -1,
            Retreating = false,
            DueTicks = 0,
            DueStamp = 0,
            LaneDeparting = true,
            ArrivalGateId = null,
            ArrivalAt = null,
            DepartureAt = null,
        };
        var launch = StationUndocking.Launch(Pilot.ShipId, Pilot.Home, Pilot.Flight);
        SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, launch.Position, Pilot.SystemId, Now);
        _operation.TravelChanged = true;
        _pilot = Pilot with { Position = launch.Position };
        _navigation.AdoptHull();
        _navigation.SetVelocity(launch.Velocity);
        RefreshObjects();
        BeginTravel();
    }

    private void Wait(string reason, int seconds = HaulingRules.PollSeconds) =>
        _work = Work with
        {
            Activity = HaulerActivity.Waiting,
            DueStamp = Work.Stamp + seconds,
            DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(seconds).Ticks,
            WaitingReason = reason,
        };

    private void BeginTravel()
    {
        bool canWork = CanWorkHere;
        if (canWork && Work.SovereigntyExpedition is { Returning: false } expedition && Pilot.SystemId == expedition.TargetSystemId)
        {
            BeginSovereigntyGrid(expedition);
            return;
        }
        var leg = CapsuleerTrafficFlight.Leg(_route, Work, Pilot.SystemId);
        if (!canWork && leg.NextSystemId is null)
        {
            _target = null;
            _work = Work with { TargetId = 0 };
            _navigation.SetAtRest();
            Wait(CapsuleerWorkRegions.TransitOnlyReason, (int)CapsuleerWorkRegions.HoldInterval.TotalSeconds);
            return;
        }
        _work = Work with
        {
            RouteIndex = leg.Index,
            RouteDirection = leg.Direction,
            DestinationStationId = leg.Direction < 0 ? _route.SourceStationId : _route.DestinationStationId,
        };
        _target = CapsuleerTrafficFlight.Target(_route, Work, Pilot.SystemId, Objects);
        var saved = _operation.World.Pilots.Single(p => p.Id == Pilot.PilotId);
        Eden.World.Combat.CrimewatchPersistence.CheckTravel(_operation.World, _operation.Catalog, saved, CrimewatchTravel.Warp, Now);
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the hauler from escaping.");
        }
        var target = _target ?? throw new InvalidDataException("The hauling route has no travel target.");
        _work = Work with { Activity = HaulerActivity.Flying, TargetId = target.Id };
        // A nearby refuge needs subwarp movement. Do not retain an old outbound alignment
        // when StartWarp rejects the new destination as too close.
        _work = Work with
        {
            WarpReadyAt = CapsuleerWarpRules.Begin(_navigation, WarpOrder(target), Work.ActiveTicks),
        };
    }

    private CapsuleerWarpOrder WarpOrder(StaticSpaceObject target) =>
        new(
            Pilot,
            target,
            Work.Stamp,
            CapsuleerTrafficFlight.ApproachRange,
            Work.SovereigntyExpedition?.HubId == target.Id ? HubWarpStandOff
                : Work.Ratting is { Phase: RattingPhase.TravellingToBelt, Patrol.Camp: { } camp } ? GateCampRules.StandOff(camp.Role)
                : 0
        );

    private void Follow() =>
        _navigation.Follow(
            Pilot.ShipId,
            Work.Stamp,
            Pilot.Position!,
            Pilot.Flight.ShipRadius,
            _target!,
            CapsuleerTrafficFlight.ApproachRange,
            false,
            true
        );

    private void Fly()
    {
        if (Work.LastMotionStamp == Work.Stamp)
        {
            return;
        }
        _work = Work with { LastMotionStamp = Work.Stamp };
        // The transfer publishes a cloaked arrival. Executing its next movement order breaks that cloak,
        // including when subsequent coordinates are published directly to resident combat.
        if (
            Work.ArrivalUntil > 0
            && (_navigation.FollowTarget is not null || _navigation.WarpDestination is not null || _navigation.Direction is not null)
            && Live?.Economy.Peek<SpatialNavigationState>(Pilot.ShipId)?.GateCloakUntil > Now
        )
        {
            GateCloakPersistence.Cancel(_operation.World, Pilot.ShipId);
        }
        if (_target is not null)
        {
            _work = Work with
            {
                WarpReadyAt = CapsuleerWarpRules.Advance(_navigation, WarpOrder(_target), Work.WarpReadyAt, Work.ActiveTicks, _warpBlocked),
            };
        }
        if (_target is not null && Work.ActiveTicks >= Work.WarpReadyAt && (_navigation.WarpDestination is not null || Work.EscapingBubble))
        {
            bool escaping = defense.NavigateField(
                Pilot,
                _navigation,
                _target,
                Work.EscapingBubble,
                Work.Stamp,
                Now,
                _warpBlocked,
                Work.Ratting is { Phase: RattingPhase.TravellingToBelt, Patrol.Camp: { } camp } ? GateCampRules.StandOff(camp.Role) : 0
            );
            _work = Work with { EscapingBubble = escaping };
        }
        bool wasWarping = _navigation.Warp is not null;
        var tick = _navigation.Advance(
            Pilot.ShipId,
            Work.Stamp,
            Pilot.Flight,
            Pilot.Position!,
            id => _target?.Id == id ? _target : null,
            _obstacles
        );
        _pilot = Pilot with { Position = tick.Position };
        if (_operation.HasWorld)
        {
            // An open mutation must see subsequent movement in its private transaction.
            SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, tick.Position, Pilot.SystemId, Now);
        }
        else if (Live?.Combat is { } resident)
        {
            resident.PublishPilotPosition(Pilot.ShipId, Pilot.SystemId, tick.Position);
        }
        else
        {
            SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, tick.Position, Pilot.SystemId, Now);
        }
        if (!_background && !wasWarping && _navigation.Warp is not null)
        {
            Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, _operation.Catalog, Pilot.ShipId);
        }
        if (
            _navigation.Warp is not null
            || _navigation.WarpDestination is not null
            || Work.EscapingBubble
                && (
                    _target?.DestinationGateId is null
                    || Work.Ratting is not null
                    || Work.Exploration is not null
                    || !GateTravelRules.InRange(Pilot.Position!, Pilot.Flight.ShipRadius, _target)
                )
        )
        {
            return;
        }
        if (Work.Ratting is { Phase: RattingPhase.TravellingToBelt })
        {
            if (
                Pilot.Position!.DistanceTo(_target!.Position) - Pilot.Flight.ShipRadius - _target.Radius
                > NpcEncounterDefinitions.DetectionRange
            )
            {
                Follow();
                return;
            }
            _navigation.Stop();
            _work = Work with { Ratting = Work.Ratting with { Phase = RattingPhase.Searching } };
            RegionalArrival();
            return;
        }
        if (Work.Exploration is { Phase: ExplorationPhase.Warping } exploring)
        {
            if (Pilot.Position!.DistanceTo(exploring.Center) > NpcEncounterDefinitions.DetectionRange)
            {
                Follow();
                return;
            }
            _navigation.Stop();
            _work = Work with { Exploration = exploring with { Phase = ExplorationPhase.Working } };
            return;
        }
        if (_target is null)
        {
            if (Work.SovereigntyExpedition is { Returning: false, HubId: not null } displaced)
            {
                ReturnFromSovereignty(displaced);
                return;
            }
            throw new InvalidOperationException(
                $"Missing hauling target {Work.TargetId} in system {Pilot.SystemId}, activity {Work.Activity}, "
                    + $"route index {Work.RouteIndex}, available {string.Join(',', Objects.Select(o => o.Id))}."
            );
        }
        if (
            Work.CorporationSupply?.FactorySupply?.Planetary is { Phase: PlanetaryVisitPhase.Orbit } visit
            && _target.Id == Eden.Gameplay.Planetary.PlanetaryRules.OfficeId(visit.Production.PlanetId)
        )
        {
            ArriveAtPlanetaryOffice(visit);
            return;
        }
        if (
            Work.CorporationSupply?.FactorySupply?.Refinery is { Phase: RefineryVisitPhase.OnGrid } build
            && _target.Id == (build.Task.StructureId ?? Eden.World.Structures.MoonMiningCatalog.BeaconId(build.Task.MoonId))
        )
        {
            ArriveAtRefinery(build);
            return;
        }
        if (Work.SovereigntyExpedition is { Returning: false, HubId: long hubId } expedition && _target.Id == hubId)
        {
            AdvanceSovereignty(expedition);
            return;
        }
        if (_target.DestinationGateId is not null)
        {
            if (CanWorkHere && !_background && GateIsDangerous())
            {
                _work = Work with
                {
                    Routine = TravelerRoutineRules.Threatened(
                        Work.Routine ?? TravelerRoutineRules.Create(pilotId),
                        Pilot.SystemId,
                        Work.ActiveTicks
                    ),
                };
                BeginRetreat();
                return;
            }
            if (GateTravelRules.InRange(Pilot.Position!, Pilot.Flight.ShipRadius, _target))
            {
                if (WaitForGangAtGate(_target) || WaitForEscortAtGate(_target))
                {
                    return;
                }
                if (CanWorkHere && TryPatrolTransitGate(_target))
                {
                    return;
                }
                var saved = _operation.World.Pilots.Single(p => p.Id == Pilot.PilotId);
                Eden.World.Combat.CrimewatchPersistence.CheckTravel(
                    _operation.World,
                    _operation.Catalog,
                    saved,
                    CrimewatchTravel.Jump,
                    Now,
                    _target.DestinationSystemId
                );
                _navigation.SetAtRest();
                Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, _operation.Catalog, Pilot.ShipId);
                _work = Work with
                {
                    Activity = HaulerActivity.Jumping,
                    DueStamp = Work.Stamp + GateTravelRules.DurationTicks,
                    DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(GateTravelRules.DurationTicks).Ticks,
                    DepartureAt = Now,
                };
            }
            else
            {
                Follow();
            }
        }
        else if (Pilot.Position!.DistanceTo(_target.Position) - Pilot.Flight.ShipRadius - _target.Radius <= DockingDefinitions.MaximumRange)
        {
            _pilot = ships.Dock(_operation.World, _operation.Catalog, Pilot.PilotId, Pilot.ShipId, Work.DestinationStationId);
            Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, _operation.Catalog, Pilot.ShipId);
            _navigation.Dock();
            _operation.TravelChanged = true;
            var routine = TravelerRoutineRules.Arrive(Work.Routine ?? TravelerRoutineRules.Create(pilotId), Pilot.StationId);
            int dwell = Work.Journey is { Kind: not TrafficJourneyKind.CapsuleEscape } journey
                ? TravelerRoutineRules.LayoverSeconds(routine, journey.Kind, pilotId, Work.DwellSequence)
                : HaulingRules.StationDwellSeconds(Pilot.PilotId, Work.DwellSequence);
            if (
                Work.Gang is { Phase: not RoamingGangPhase.Complete }
                || Work.RegionalEscort is not null
                || Work.SovereigntyExpedition is not null
                || Work.CorporationSupply?.FactorySupply is { Mercoxit: true } or { Planetary: not null } or { Refinery: not null }
            )
            {
                // An operational stop loads or unloads before the next ordinary layover.
                dwell = 1;
            }
            if (routine.ShelterPending)
            {
                dwell = Math.Max(dwell, TravelerRoutineRules.ShelterSeconds(routine, Work.DwellSequence));
                routine = routine with { ShelterPending = false };
            }
            _work = Work with
            {
                Activity = HaulerActivity.Docked,
                TargetId = 0,
                DueStamp = Work.Stamp + dwell,
                DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(dwell).Ticks,
                DwellSequence = checked(Work.DwellSequence + 1),
                Routine = routine,
                Resupply = new(
                    Pilot.StationId,
                    (_operation.World.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0) / RattingRules.BudgetDivisor,
                    0
                ),
            };
            FinishCorporationRelocation();
            if (Work.Ratting is { } returning)
            {
                _work = Work with { RattingLoot = returning.LootIds };
                UnloadRattingLoot();
            }
            if (Work.Exploration is { } exploration)
            {
                FinishExplorationAtDock(exploration);
            }
        }
        else
        {
            Follow();
        }
    }

    private void CompleteJump()
    {
        using var phase = SessionPerformance.Phase("traffic.completeJump");
        long arrivalGateId = _target!.DestinationGateId!.Value;
        long arrivalAt = Now;
        var transfer = PilotGateTravelStore.TransferDetailed(
            _operation.World,
            _operation.Catalog,
            Pilot.PilotId,
            Pilot.ShipId,
            Work.TargetId,
            arrivalGateId,
            arrivalAt
        );
        string? revision = _operation.World.FitRevision(pilotId);
        _pilot =
            revision is not null && revision == _snapshotFitRevision && database.Catalog.Identity.Generation == _snapshotCatalogGeneration
                ? Pilot with
                {
                    Location = new(transfer.Position, transfer.System),
                }
                : ReadPilot(_operation.World, transfer.AccountId);
        using var destinationContext = Live?.UseSystem(_pilot.SystemId);
        _operation.TravelChanged = true;
        _background = _background && Live?.CapsuleerActivity.Read().ActiveSystems.Contains(_pilot.SystemId) != true;
        _navigation.AdoptHull();
        _work = Work with
        {
            ArrivalUntil = Work.Stamp + GateTravelRules.ArrivalTicks,
            RouteIndex = Work.RouteIndex + Work.RouteDirection,
            LaneDeparting = false,
            LastPatrolledGateId = 0,
            GangRendezvousUntil = 0,
            ArrivalGateId = arrivalGateId,
            ArrivalAt = arrivalAt,
            DepartureAt = null,
        };
        RefreshObjects(loadResources: Live?.CapsuleerActivity.Read().ActiveSystems.Contains(Pilot.SystemId) != false);
        if (
            Work.Journey?.Kind
                is TrafficJourneyKind.CapsuleEscape
                    or TrafficJourneyKind.SovereigntyExpedition
                    or TrafficJourneyKind.CorporationRelocation
            || !TryRegionalGrid()
        )
        {
            BeginTravel();
        }
    }

    private long _nextLook;

    private void BeginRetreat()
    {
        if (Work.SovereigntyExpedition is { Returning: false } expedition)
        {
            ReturnFromSovereignty(expedition);
            return;
        }
        var leg = CapsuleerTrafficFlight.Leg(_route, Work, Pilot.SystemId);
        int direction =
            _route.SourceStationId == _route.DestinationStationId || leg.Index == 0 ? -1
            : leg.Index == _route.Systems.Length - 1 ? 1
            : -leg.Direction;
        _work = Work with
        {
            RouteIndex = leg.Index,
            RouteDirection = direction,
            DestinationStationId = direction < 0 ? _route.SourceStationId : _route.DestinationStationId,
            Retreating = true,
            LaneDeparting = false,
        };
        if (Work.Ratting is { } activity)
        {
            _work = Work with { Ratting = activity with { Phase = RattingPhase.Returning } };
        }
        else if (Work.Exploration is null)
        {
            BeginTravel();
        }
    }

    private bool GateIsDangerous()
    {
        if (
            !database.Settings.Capsuleers.SituationalAwareness
            || Work.Retreating
            || Work.Freight?.Stacks.Length is not > 0
                && (HullClass)Pilot.ShipGroupId
                    is not (
                        HullClass.Hauler
                        or HullClass.DeepSpaceTransport
                        or HullClass.BlockadeRunner
                        or HullClass.Freighter
                        or HullClass.JumpFreighter
                    )
            || _target is not { DestinationSystemId: not null } gate
            || Pilot.Position is not { } position
            || Work.ActiveTicks < _nextLook
            || position.DistanceTo(gate.Position) > NpcEncounterDefinitions.VisibilityRange
        )
        {
            return false;
        }
        _nextLook = Work.ActiveTicks + TimeSpan.FromSeconds(PilotAwareness.LookSeconds(pilotId)).Ticks;
        return defense.ObserveCivilianDanger(Pilot, _navigation.Velocity, Work.ActiveTicks, gate) == CivilianDanger.Escape;
    }

    private void ObserveDanger(int destinyStamp)
    {
        if (
            Pilot.Position is not { } position
            || _navigation.Warp is not null
            || Work.Activity == HaulerActivity.Jumping
            || Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup
        )
        {
            return;
        }
        var contacts = encounters.Advance(Pilot.SystemId, Now);
        combat.Acquire([new(Pilot.PilotId, Pilot.ShipId)], Now);
        warfare.Advance(Pilot.PilotId, Pilot.ShipId, Now);
        var attackers = combat.Aggressors(Pilot.ShipId);
        if (attackers.Count > 0)
        {
            HashSet<long> visible = [];
            foreach (var contact in contacts)
            {
                if (!contact.Warping && contact.Position.DistanceTo(position) <= AsteroidDefinitions.VisibilityRange)
                {
                    visible.Add(contact.Id);
                }
            }
            foreach (long npc in attackers)
            {
                if (!visible.Contains(npc))
                {
                    continue;
                }
                var shot = combat.Fire(
                    Pilot.PilotId,
                    Pilot.ShipId,
                    npc,
                    _navigation.Velocity,
                    Now,
                    new Random(unchecked((int)Pilot.PilotId ^ Work.Stamp ^ npc.GetHashCode())).NextDouble(),
                    destinyStamp
                );
                if (shot?.Capsule is not null)
                {
                    Load();
                    return;
                }
            }
        }
        foreach (var impact in missiles.Advance(Pilot.PilotId, Pilot.ShipId, _navigation.Velocity, Now))
        {
            if (impact.Capsule is not null)
            {
                Load();
                return;
            }
        }
    }
}
