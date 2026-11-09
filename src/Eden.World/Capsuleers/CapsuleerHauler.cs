using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
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
    MarketChangeSignal marketSignal
)
{
    internal CapsuleerHauler Create(long pilotId) =>
        new(pilotId, database, ships, systems, tackle, combat, encounters, missiles, warfare, health, clock, marketSignal);
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
    MarketChangeSignal marketSignal
)
{
    private SavedCapsuleerHauler? _founder;
    private DockedPilot? _pilot;
    private HaulerProgress? _work;
    private NavigationState _navigation = new();
    private IReadOnlyList<StaticSpaceObject> _objects = [];
    private StaticSpaceObject? _target;
    private IReadOnlyList<StaticSpaceObject> _obstacles = [];
    private TackleEffect[] _incoming = [];
    private bool _warpBlocked;
    private CapsuleerTrafficRoute _route = null!;
    private long _threatenedShip;
    private bool _loaded;
    private bool _background;
    private CapsuleerOperation _operation = null!;
    private long _hullRevision;
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

    internal int SystemId
    {
        get
        {
            if (!_loaded)
            {
                Load();
            }
            return _pilot?.SystemId ?? throw new InvalidDataException("The scheduled capsuleer is missing.");
        }
    }

    internal HaulerObservation? Read()
    {
        if (!_loaded)
        {
            Load();
        }
        return _pilot is null ? null : Observation();
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
        if (delay == 0)
        {
            return job;
        }
        var resumed = Work with
        {
            ActiveTicks = Work.ActiveTicks + delay,
            ClockOffsetTicks = Work.ClockOffsetTicks + delay,
            DueTicks = Work.DueTicks == 0 ? 0 : Work.DueTicks + delay,
        };
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
                RouteIndex = Pilot.StationId == Founder.SourceStationId ? 0 : _route.Systems.Length - 1,
                RouteDirection = Pilot.StationId == Founder.SourceStationId ? 1 : -1,
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
            var departure = _objects.Where(o => o.DestinationSystemId == systemId).OrderBy(o => o.Id).FirstOrDefault();
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
                int station = leg.Direction > 0 ? Founder.SourceStationId : Founder.DestinationStationId;
                var endpoint = _objects.Single(o => o.Id == station);
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
            long account = PilotGateTravelStore.Transfer(
                operation.World,
                operation.Catalog,
                pilotId,
                Pilot.ShipId,
                departure.Id,
                exit,
                Now
            );
            _pilot = ships.FindPilot(operation.World, account, pilotId)!;
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

    internal CapsuleerJob Execute(CapsuleerJob job, int destinyStamp, bool background = false)
    {
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
            using WorldDbContext world = new(database.Path);
            if (!world.Pilots.Any(p => p.Id == pilotId && p.ActiveShipId == Pilot.ShipId))
            {
                Load();
            }
        }
        _background = background;
        if (Work.Activity != HaulerActivity.Inbound)
        {
            ObserveDanger(destinyStamp);
        }
        if (Pilot.Position is not null)
        {
            RefreshTackle();
        }
        var beforePilot = Pilot;
        var before = Work;
        var beforeTarget = _target;
        var beforeObjects = _objects;
        var beforeObstacles = _obstacles;
        _work = Work with { ActiveTicks = job.DueTicks, Stamp = CapsuleerScheduleRules.Stamp(job.DueTicks, Work.ClockOffsetTicks) };
        _operation = new(database, job, marketSignal);
        try
        {
            if (Pilot.Position is not null && _warpBlocked && _navigation.Warp is null && _navigation.WarpDestination is not null)
            {
                throw new InvalidDataException("Warp disruption interrupts the hauler's alignment.");
            }
            if (
                Interlocked.Exchange(ref _threatenedShip, 0) == Pilot.ShipId
                && Pilot.Position is not null
                && Work.Activity != HaulerActivity.Jumping
                && !Work.Retreating
            )
            {
                var leg = CapsuleerTrafficFlight.Leg(_route, Work, Pilot.SystemId);
                int direction =
                    Founder.SourceStationId == Founder.DestinationStationId || leg.Index == 0 ? -1
                    : leg.Index == _route.Systems.Length - 1 ? 1
                    : -leg.Direction;
                _work = Work with
                {
                    RouteIndex = leg.Index,
                    RouteDirection = direction,
                    DestinationStationId = direction < 0 ? Founder.SourceStationId : Founder.DestinationStationId,
                    Retreating = true,
                    LaneDeparting = false,
                };
                BeginTravel();
            }
            Step();
            return Commit(job);
        }
        catch (Exception error) when (error is InvalidDataException or CargoCapacityException)
        {
            _operation.Dispose();
            _pilot = beforePilot;
            _work = before;
            _navigation = NavigationState.Restore(before.Navigation, beforePilot.Flight);
            _target = beforeTarget;
            _objects = beforeObjects;
            _obstacles = beforeObstacles;
            RestorePosition(beforePilot);
            _operation = new(database, job, marketSignal);
            _navigation.AdoptHull();
            _work = Work with
            {
                ActiveTicks = job.DueTicks,
                Stamp = CapsuleerScheduleRules.Stamp(job.DueTicks, Work.ClockOffsetTicks),
                Activity = HaulerActivity.Waiting,
                DueStamp = CapsuleerScheduleRules.Stamp(job.DueTicks, Work.ClockOffsetTicks) + HaulingRules.PollSeconds,
                DueTicks = checked(job.DueTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks),
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
            _work = before;
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
            Live?.Combat?.PublishPilotPosition(pilot.ShipId, pilot.SystemId, position);
        }
    }

    private CapsuleerJob Commit(CapsuleerJob job)
    {
        _work = Work with { Navigation = _navigation.Capture() };
        var schedule = CapsuleerScheduleRules.Next(Work, Pilot.Position is not null);
        if (
            _background
            && CapsuleerScheduleRules.WarpDeadline(Work.Navigation, Pilot.Flight, Work.ClockOffsetTicks, Work.ActiveTicks) is { } exit
        )
        {
            schedule = schedule with { DueTicks = exit };
        }
        else if (_background && Work.Activity == HaulerActivity.Jumping && Work.DueTicks > Work.ActiveTicks)
        {
            schedule = schedule with { DueTicks = Work.DueTicks };
        }
        var next = job.Next(schedule.DueTicks, schedule.Action);
        _operation.Commit(next, null, new HaulerCheckpoint(pilotId, Work));
        return next;
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
        live.Combat?.PublishPilotPosition(Pilot.ShipId, Pilot.SystemId, tick.Position);
        live.HaulerCheckpoints[pilotId] = new(pilotId, Work);
        live.ObjectsChanged();
        return true;
    }

    private HaulerObservation Observation() => new(Pilot, Work, health.Read(pilotId, Now));

    private void Load()
    {
        using WorldDbContext world = new(database.Path);
        _founder = world.Set<SavedCapsuleerHauler>().AsNoTracking().SingleOrDefault(h => h.PilotId == pilotId);
        _loaded = true;
        if (_founder is null)
        {
            return;
        }
        long account = world.Pilots.Where(p => p.Id == Founder.PilotId).Select(p => p.AccountId).Single();
        _pilot = ships.FindPilot(account, Founder.PilotId)!;
        using CatalogDbContext catalog = new(database.CatalogPath);
        _route = CapsuleerTrafficFlight.Route(Founder, catalog);
        _incoming = [];
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
                Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup ? Founder.SourceStationId : Founder.DestinationStationId,
                0,
                _navigation.Capture(),
                RouteDirection: Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup ? -1 : 1,
                TrafficLane: previous?.TrafficLane
            );
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
        if (Founder.RouteSystems is null)
        {
            // Older direct routes had only their destination station as a cursor.
            _work = Work with
            {
                RouteDirection = Work.DestinationStationId == Founder.SourceStationId ? -1 : 1,
            };
        }
        _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
        LoadInboundDwells(catalog);
        RefreshObjects();
        _target = _objects.FirstOrDefault(o => o.Id == Work.TargetId);
    }

    private void RefreshObjects()
    {
        _objects = _operation is { HasWorld: true }
            ? CapsuleerGeometry.Load(_operation.World, _operation.Catalog, Pilot.SystemId, Now)
            : systems.Load(Pilot.SystemId);
        _obstacles = _objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup).ToArray();
    }

    private void RefreshTackle()
    {
        var incoming = tackle.Incoming(Pilot.ShipId).ToArray();
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
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Waiting for a replacement hauling ship.");
        }
        // The cargo belongs to this encounter. A dock changes its route, never
        // purchases or lists goods on behalf of the independent regional economy.
        _work = Work with
        {
            DestinationStationId = Pilot.StationId == Founder.DestinationStationId ? Founder.SourceStationId : Founder.DestinationStationId,
            RouteIndex = Pilot.StationId == Founder.SourceStationId ? 0 : _route.Systems.Length - 1,
            RouteDirection = Pilot.StationId == Founder.SourceStationId ? 1 : -1,
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
        if (!SelectLaneTarget())
        {
            var leg = CapsuleerTrafficFlight.Leg(_route, Work, Pilot.SystemId);
            _work = Work with
            {
                RouteIndex = leg.Index,
                RouteDirection = leg.Direction,
                DestinationStationId = leg.Direction < 0 ? Founder.SourceStationId : Founder.DestinationStationId,
            };
            _target = CapsuleerTrafficFlight.Target(_route, Work, Pilot.SystemId, _objects);
        }
        var saved = _operation.World.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == Pilot.PilotId);
        Eden.World.Combat.CrimewatchPersistence.CheckTravel(_operation.World, _operation.Catalog, saved, CrimewatchTravel.Warp, Now);
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the hauler from escaping.");
        }
        var target = _target ?? throw new InvalidDataException("The hauling route has no travel target.");
        _work = Work with { Activity = HaulerActivity.Flying, TargetId = target.Id };
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
        if (Live?.Combat is { } resident)
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
            const int minimumDwellSeconds = 15;
            const int dwellSpreadSeconds = 31;
            int dwell = minimumDwellSeconds + (int)(Pilot.PilotId % dwellSpreadSeconds);
            _work = Work with
            {
                Activity = HaulerActivity.Docked,
                TargetId = 0,
                DueStamp = Work.Stamp + dwell,
                DueTicks = Work.ActiveTicks + TimeSpan.FromSeconds(dwell).Ticks,
            };
        }
        else
        {
            Follow();
        }
    }

    private void CompleteJump()
    {
        long arrivalGateId = _target!.DestinationGateId!.Value;
        long arrivalAt = Now;
        long account = PilotGateTravelStore.Transfer(
            _operation.World,
            _operation.Catalog,
            Pilot.PilotId,
            Pilot.ShipId,
            Work.TargetId,
            arrivalGateId,
            arrivalAt
        );
        _pilot = ships.FindPilot(_operation.World, account, Pilot.PilotId)!;
        _operation.TravelChanged = true;
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
        if (attackers.Length > 0 && Work.Activity != HaulerActivity.Jumping)
        {
            ObserveAggression(Pilot.ShipId);
        }
    }
}
