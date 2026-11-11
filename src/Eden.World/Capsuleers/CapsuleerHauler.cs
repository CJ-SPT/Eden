using Eden.Data.World.Capsuleers;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
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
    CapsuleerRatting ratting
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
            ratting
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
    CapsuleerRatting ratting
)
{
    private SavedCapsuleerHauler? _founder;
    private DockedPilot? _pilot;
    internal DockedPilot? PreparationPilot => _pilot;
    internal CapsuleerTrafficRoute? PreparationRoute => _loaded ? _route : null;
    private HaulerProgress? _work;
    private NavigationState _navigation = new();
    private IReadOnlyList<StaticSpaceObject> _objects = [];
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
    private bool _detailed = true;
    private bool _coarse;

    internal bool RequiresDetail =>
        (_work ?? Live?.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work) is { Defense: not null } or { Ratting: not null }
        || _incoming.Length != 0
        || _warpBlocked
        || Volatile.Read(ref _threatenedShip) != 0;

    internal void SetDetailed(bool detailed)
    {
        if (_detailed && detailed)
        {
            return;
        }
        _detailed = detailed;
        if (!detailed)
        {
            _objects = [];
            _obstacles = [];
        }
        else if (_loaded && _pilot is not null)
        {
            RefreshObjects();
            _target = _objects.FirstOrDefault(o => o.Id == Work.TargetId) ?? _target;
        }
    }

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
        return _pilot is null ? null : Observation(includeHealth);
    }

    internal CapsuleerJob Resume(CapsuleerJob job, long elapsed)
    {
        if (!_loaded)
        {
            Load();
        }
        if (Live is { } live && _hullRevision != live.CapsuleerHullRevisions.GetValueOrDefault(pilotId))
        {
            _hullRevision = live.CapsuleerHullRevisions.GetValueOrDefault(pilotId);
            Load();
        }
        long delay = Math.Max(0, elapsed - Work.ActiveTicks);
        if (delay == 0 && Work.Ratting is null)
        {
            return job;
        }
        var resumed = CapsuleerPauseRules.Resume(Work, delay, Now);
        var next = job.Next(Math.Max(elapsed + TimeSpan.TicksPerSecond, job.DueTicks + delay), job.Action);
        using CapsuleerOperation operation = new(database, job, marketSignal);
        operation.Commit(next, null, new HaulerCheckpoint(pilotId, resumed));
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
        using CapsuleerOperation operation = new(database, resumed, marketSignal);
        try
        {
            var departure = Objects.Where(o => o.DestinationSystemId == systemId).OrderBy(o => o.Id).FirstOrDefault();
            if (departure?.DestinationGateId is not { } exit)
            {
                return resumed;
            }
            var saved = operation.World.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
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

    internal CapsuleerJob Execute(
        CapsuleerJob job,
        int destinyStamp,
        bool background = false,
        PreparedJourney? preparedJourney = null,
        CapsuleerCoarseWork? coarse = null,
        CapsuleerResumeWork? resume = null
    )
    {
        _preparedJourney = preparedJourney;
        if (coarse is not null && !RequiresDetail)
        {
            SetDetailed(false);
        }
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
            using WorldDbContext world = new(database.Path) { Participants = new(pilots: [pilotId]) };
            if (!world.Pilots.Any(p => p.Id == pilotId && p.ActiveShipId == Pilot.ShipId))
            {
                Load();
            }
        }
        _background = background && !RequiresDetail;
        _coarse = coarse is not null;
        if (_coarse && _background)
        {
            _obstacles = [];
        }
        if (!_background && Work.Activity != HaulerActivity.Inbound)
        {
            ObserveDanger(destinyStamp);
        }
        if (Pilot.Position is not null)
        {
            RefreshTackle();
            _background = _background && !RequiresDetail;
        }
        AdvanceRattingClock(job.DueTicks);
        var proactive = ProactiveTarget();
        var previousDefense = Work.Defense;
        var defended = _background
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
                proactive: proactive
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
        var beforeObstacles = _obstacles;
        long stepTicks = resume is null
            ? job.DueTicks
            : Math.Max(job.DueTicks, Live?.CapsuleerActivity.Read().ElapsedTicks ?? resume.EntryTicks);
        if (resume is not null)
        {
            _work = CapsuleerPauseRules.Resume(Work, Math.Max(0, resume.EntryTicks - Math.Max(Work.ActiveTicks, resume.SettledTicks)), Now);
        }
        _work = Work with { ActiveTicks = stepTicks, Stamp = CapsuleerScheduleRules.Stamp(stepTicks, Work.ClockOffsetTicks) };
        _operation = new(database, job, marketSignal, coarse);
        try
        {
            if (Pilot.Position is not null && _warpBlocked && _navigation.Warp is null && _navigation.WarpDestination is not null)
            {
                throw new InvalidDataException("Warp disruption interrupts the hauler's alignment.");
            }
            if (
                Volatile.Read(ref _threatenedShip) == Pilot.ShipId
                && Pilot.Position is not null
                && Work.Activity != HaulerActivity.Jumping
                && !Work.Retreating
            )
            {
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
                else
                {
                    BeginTravel();
                }
            }
            if (!defended.HoldsNavigation)
            {
                if (resumeCourse && Pilot.Position is not null && Work.Ratting is null)
                {
                    BeginTravel();
                }
                Step();
                if (coarse is not null && _background && before.Activity != HaulerActivity.Docked)
                {
                    AdvanceCoarse(coarse.ThroughTicks);
                }
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
            _obstacles = beforeObstacles;
            RestorePosition(beforePilot);
            _operation = new(database, job, marketSignal, coarse);
            _navigation.AdoptHull();
            long failureTicks = coarse?.ThroughTicks ?? stepTicks;
            _work = Work with
            {
                ActiveTicks = failureTicks,
                Stamp = CapsuleerScheduleRules.Stamp(failureTicks, Work.ClockOffsetTicks),
                Activity = HaulerActivity.Waiting,
                DueStamp = CapsuleerScheduleRules.Stamp(failureTicks, Work.ClockOffsetTicks) + HaulingRules.PollSeconds,
                DueTicks = checked(failureTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks),
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
        _work = Work with { Navigation = _navigation.Capture() };
        var schedule = Schedule();
        var next = job.Next(schedule.DueTicks, schedule.Action);
        _operation.Commit(next, null, new HaulerCheckpoint(pilotId, Work));
        Interlocked.CompareExchange(ref _threatenedShip, 0, Pilot.ShipId);
        return next;
    }

    private CapsuleerSchedule Schedule()
    {
        var schedule = CapsuleerScheduleRules.Next(Work, Pilot.Position is not null, _background);
        if (
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

    private void AdvanceCoarse(long through)
    {
        for (int transition = 0; transition < CapsuleerCoarseRules.MaximumTransitions && _background; transition++)
        {
            if (Work.Activity is HaulerActivity.Docked or HaulerActivity.Waiting || RequiresDetail)
            {
                return;
            }
            _work = Work with { Navigation = _navigation.Capture() };
            var next = Schedule();
            if (next.DueTicks > through)
            {
                return;
            }
            if (
                Work.Activity == HaulerActivity.Jumping
                && _target?.DestinationSystemId is { } destination
                && Live?.CapsuleerActivity.Read().ActiveSystems.Contains(destination) == true
            )
            {
                return;
            }
            _work = Work with { ActiveTicks = next.DueTicks, Stamp = CapsuleerScheduleRules.Stamp(next.DueTicks, Work.ClockOffsetTicks) };
            Step();
        }
    }

    internal bool Sample(long throughTicks)
    {
        if (Live is not { } live || !_loaded || _pilot?.Position is null || _navigation.Warp is not { } warp)
        {
            return false;
        }
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
        using WorldDbContext world = new(database.Path) { Participants = new(pilots: [pilotId]) };
        using var transaction = world.Database.BeginTransaction();
        _founder = world.Set<SavedCapsuleerHauler>().AsNoTracking().SingleOrDefault(h => h.PilotId == pilotId);
        _loaded = true;
        if (_founder is null)
        {
            return;
        }
        long account = world.Pilots.Where(p => p.Id == Founder.PilotId).Select(p => p.AccountId).Single();
        _pilot = ReadPilot(world, account);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _route = CapsuleerTrafficFlight.Route(Founder, catalog);
        _incoming = [];
        _tackleRevision = -1;
        _warpBlocked = false;
        _work = Live?.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work ?? HaulerCheckpoint.Read(Founder.Progress);
        var previous = _work;
        long anchor =
            previous?.ActiveTicks ?? (world.Set<SavedCapsuleerJob>().Single(j => j.PilotId == pilotId).DueTicks - TimeSpan.TicksPerSecond);
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
                Routine = previous?.Routine,
                Defense = previous?.Defense,
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
        transaction.Commit();
        RefreshObjects();
        _target = _objects.FirstOrDefault(o => o.Id == Work.TargetId);
    }

    private void RefreshObjects()
    {
        using var phase = SessionPerformance.Phase("traffic.geometry");
        if (!_detailed && !RequiresDetail && Live?.CapsuleerActivity.Read().ActiveSystems.Contains(Pilot.SystemId) != true)
        {
            // Remote navigation uses station/gate targets and no collision obstacles.
            // Loading every belt here created resource rows and opened a connection per belt.
            using CatalogDbContext catalog = new(database.CatalogPath);
            _objects = SystemObjects.Geometry(catalog, Pilot.SystemId);
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

    private void Step()
    {
        if (Work.Ratting is not null)
        {
            RattingStep();
            return;
        }
        switch (Work.Activity)
        {
            case HaulerActivity.Docked when Work.ActiveTicks >= Work.DueTicks:
                TradeAndLaunch();
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
                if (Pilot.Position is null)
                {
                    _work = Work with { Activity = HaulerActivity.Docked };
                }
                else
                {
                    BeginTravel();
                }
                break;
        }
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

    private void TradeAndLaunch()
    {
        using var phase = SessionPerformance.Phase("traffic.tradeAndLaunch");
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Waiting for a replacement hauling ship.");
        }
        if (!_coarse && !_trafficRefreshChecked && database.Traffic.Profiles.Count > 0)
        {
            if (TrafficProvisioning.Refresh(database, _operation.World, _operation.Catalog, pilotId, _route, Now))
            {
                long account = _operation.World.Pilots.Where(value => value.Id == pilotId).Select(value => value.AccountId).Single();
                _pilot = ReadPilot(_operation.World, account);
                _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
            }
            _trafficRefreshChecked = true;
        }
        if (!_coarse && TrafficCorrection.Apply(database, _operation.World, _operation.Catalog, pilotId, Now))
        {
            long account = _operation.World.Pilots.Where(p => p.Id == pilotId).Select(p => p.AccountId).Single();
            _pilot = ReadPilot(_operation.World, account);
            _navigation.AdoptHull();
        }
        CapsuleerSupplyStore.Ensure(_operation.World, _operation.Catalog, pilotId, Founder.FounderShipId);
        UnloadRattingLoot();
        var supplies = resupply.Dock(database, _operation.World, _operation.Catalog, pilotId, Work.Resupply, Now);
        _operation.MarketChanged |= supplies.SpentCents != (Work.Resupply?.SpentCents ?? 0);
        _work = Work with { Resupply = supplies };
        if (TryLaunchActivity())
        {
            return;
        }
        if (Work.Retreating || Work.Journey is null || Pilot.StationId == Work.Journey.DestinationStationId)
        {
            AssignJourney();
        }
        // The cargo belongs to this encounter. A dock changes its route, never
        // purchases or lists goods on behalf of the independent regional economy.
        _work = Work with
        {
            DestinationStationId = Pilot.StationId == _route.DestinationStationId ? _route.SourceStationId : _route.DestinationStationId,
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

    private void Wait(string reason) =>
        _work = Work with
        {
            Activity = HaulerActivity.Waiting,
            DueStamp = Work.Stamp + HaulingRules.PollSeconds,
            DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks,
            WaitingReason = reason,
        };

    private void BeginTravel()
    {
        var leg = CapsuleerTrafficFlight.Leg(_route, Work, Pilot.SystemId);
        _work = Work with
        {
            RouteIndex = leg.Index,
            RouteDirection = leg.Direction,
            DestinationStationId = leg.Direction < 0 ? _route.SourceStationId : _route.DestinationStationId,
        };
        _target = CapsuleerTrafficFlight.Target(_route, Work, Pilot.SystemId, Objects);
        var saved = _operation.World.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == Pilot.PilotId);
        Eden.World.Combat.CrimewatchPersistence.CheckTravel(_operation.World, _operation.Catalog, saved, CrimewatchTravel.Warp, Now);
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the hauler from escaping.");
        }
        var target = _target ?? throw new InvalidDataException("The hauling route has no travel target.");
        _work = Work with { Activity = HaulerActivity.Flying, TargetId = target.Id };
        // A nearby refuge needs subwarp movement. Do not retain an old outbound alignment
        // when StartWarp rejects the new destination as too close.
        _navigation.CancelAlignment();
        if (!_navigation.StartWarp(Pilot.ShipId, Pilot.Flight, Pilot.Position!, target, 0, Work.Stamp))
        {
            Follow();
        }
    }

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
        if (_coarse)
        {
            // Several analytic boundaries share this transaction. Subsequent
            // docking/jump validation must see its own latest position in SQL.
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
        if (!wasWarping && _navigation.Warp is not null)
        {
            Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, Pilot.ShipId);
        }
        if (_navigation.Warp is not null || _navigation.WarpDestination is not null)
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
            return;
        }
        if (_target!.DestinationGateId is not null)
        {
            if (GateTravelRules.InRange(Pilot.Position!, Pilot.Flight.ShipRadius, _target))
            {
                var saved = _operation
                    .World.Pilots.Include(p => p.ActiveShip)
                        .ThenInclude(s => s.Position)
                    .Single(p => p.Id == Pilot.PilotId);
                Eden.World.Combat.CrimewatchPersistence.CheckTravel(
                    _operation.World,
                    _operation.Catalog,
                    saved,
                    CrimewatchTravel.Jump,
                    Now,
                    _target.DestinationSystemId
                );
                _navigation.SetAtRest();
                Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, Pilot.ShipId);
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
            Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, Pilot.ShipId);
            _navigation.Dock();
            _operation.TravelChanged = true;
            var routine = TravelerRoutineRules.Arrive(Work.Routine ?? TravelerRoutineRules.Create(pilotId), Pilot.StationId);
            int dwell = Work.Journey is { } journey
                ? TravelerRoutineRules.LayoverSeconds(routine, journey.Kind, pilotId, Work.DwellSequence)
                : HaulingRules.StationDwellSeconds(Pilot.PilotId, Work.DwellSequence);
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
            if (Work.Ratting is { } returning)
            {
                _work = Work with { RattingLoot = returning.LootIds };
                UnloadRattingLoot();
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
            ArrivalGateId = arrivalGateId,
            ArrivalAt = arrivalAt,
            DepartureAt = null,
        };
        RefreshObjects();
        BeginTravel();
    }

    private void ObserveDanger(int destinyStamp)
    {
        if (
            Pilot.Position is null
            || _navigation.Warp is not null
            || Work.Activity == HaulerActivity.Jumping
            || Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup
        )
        {
            return;
        }
        var visible = encounters
            .Advance(Pilot.SystemId, Now)
            .Where(n => !n.Warping && n.Position.DistanceTo(Pilot.Position) <= AsteroidDefinitions.VisibilityRange)
            .Select(n => n.Id)
            .ToHashSet();
        combat.Acquire([new(Pilot.PilotId, Pilot.ShipId)], Now);
        warfare.Advance(Pilot.PilotId, Pilot.ShipId, Now);
        var attackers = combat.Aggressors(Pilot.ShipId).Where(visible.Contains).ToArray();
        foreach (long npc in attackers)
        {
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
