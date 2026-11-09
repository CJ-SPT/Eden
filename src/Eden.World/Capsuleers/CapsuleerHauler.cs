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

internal sealed class CapsuleerHauler(
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
    private const double ApproachRange = 500;
    private SavedCapsuleerHauler? _founder;
    private DockedPilot? _pilot;
    private HaulerProgress? _work;
    private NavigationState _navigation = new();
    private IReadOnlyList<StaticSpaceObject> _objects = [];
    private StaticSpaceObject? _target;
    private IReadOnlyList<StaticSpaceObject> _obstacles = [];
    private TackleEffect[] _incoming = [];
    private bool _warpBlocked;
    private int _sourceSystem;
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
        var resumed = Resume(job, elapsed);
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup || Pilot.SystemId == systemId)
        {
            return resumed;
        }
        using CapsuleerOperation operation = new(database, resumed, marketSignal);
        try
        {
            var arrival = systems.Load(systemId).First(o => o.DestinationSystemId == Pilot.SystemId);
            SqliteWorld.SetPosition(operation.World, operation.Catalog, Pilot.ShipId, arrival.Position, systemId, Now);
            long account = operation.World.Pilots.Where(p => p.Id == pilotId).Select(p => p.AccountId).Single();
            _pilot = ships.FindPilot(operation.World, account, pilotId)!;
            _navigation.AdoptHull();
            _objects = systems.Load(systemId);
            _obstacles = _objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup).ToArray();
            int station = systemId == _sourceSystem ? Founder.SourceStationId : Founder.DestinationStationId;
            _target = _objects.Single(o => o.Id == station);
            _work = Work with
            {
                Activity = HaulerActivity.Flying,
                DestinationStationId = station,
                TargetId = station,
                Navigation = _navigation.Capture(),
                ArrivalUntil = Work.Stamp + GateTravelRules.ArrivalTicks,
            };
            _operation = operation;
            BeginTravel();
            _work = Work with { Navigation = _navigation.Capture() };
            var next = resumed.Next(resumed.DueTicks, CapsuleerAction.Arrival);
            operation.TravelChanged = true;
            operation.Commit(next, null, new HaulerCheckpoint(pilotId, Work));
            return next;
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
        ObserveDanger(destinyStamp);
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
            )
            {
                int refuge = Pilot.SystemId == _sourceSystem ? Founder.SourceStationId : Founder.DestinationStationId;
                if (Work.DestinationStationId != refuge)
                {
                    _work = Work with { DestinationStationId = refuge };
                    BeginTravel();
                }
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
        _sourceSystem = catalog.RequireStation(Founder.SourceStationId).SolarSystemId;
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
                _navigation.Capture()
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
        _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
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
            case HaulerActivity.Docked:
                TradeAndLaunch();
                break;
            case HaulerActivity.Flying:
                Fly();
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
        using CatalogDbContext catalog = new(database.CatalogPath);
        int destination = catalog.RequireStation(Work.DestinationStationId).SolarSystemId;
        _target =
            destination == Pilot.SystemId
                ? _objects.Single(o => o.Id == Work.DestinationStationId)
                : _objects.Where(o => o.DestinationSystemId == destination).OrderBy(o => o.Id).FirstOrDefault()
                    ?? throw new InvalidDataException("The saved hauling route has no connected stargate.");
        var saved = _operation.World.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == Pilot.PilotId);
        Eden.World.Combat.CrimewatchPersistence.CheckTravel(_operation.World, _operation.Catalog, saved, CrimewatchTravel.Warp, Now);
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the hauler from escaping.");
        }
        _work = Work with { Activity = HaulerActivity.Flying, TargetId = _target.Id };
        if (!_navigation.StartWarp(Pilot.ShipId, Pilot.Flight, Pilot.Position!, _target, 0, Work.Stamp))
        {
            Follow();
        }
    }

    private void Follow() =>
        _navigation.Follow(Pilot.ShipId, Work.Stamp, Pilot.Position!, Pilot.Flight.ShipRadius, _target!, ApproachRange, false, true);

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
            _work = Work with { Activity = HaulerActivity.Docked, TargetId = 0 };
        }
        else
        {
            Follow();
        }
    }

    private void CompleteJump()
    {
        long account = PilotGateTravelStore.Transfer(
            _operation.World,
            _operation.Catalog,
            Pilot.PilotId,
            Pilot.ShipId,
            Work.TargetId,
            _target!.DestinationGateId!.Value,
            Now
        );
        _pilot = ships.FindPilot(_operation.World, account, Pilot.PilotId)!;
        _operation.TravelChanged = true;
        _navigation.AdoptHull();
        _work = Work with { ArrivalUntil = Work.Stamp + GateTravelRules.ArrivalTicks };
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
