using Eden.Data.World.Capsuleers;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerMinerFactory(
    WorldDatabase database,
    SqliteWorld ships,
    CapsuleerGeometry systems,
    IAsteroidField belts,
    IPilotInventory inventory,
    IPilotFitting fitting,
    TimeProvider clock,
    INpcCombat combat,
    INpcSimulation encounters,
    INpcMissiles missiles,
    INpcElectronicWarfare warfare,
    IPilotTackle tackle,
    IPilotHealth health,
    MarketChangeSignal marketSignal,
    CapsuleerDefense defense
)
{
    internal CapsuleerMiner Create(long pilotId) =>
        new(
            pilotId,
            database,
            ships,
            systems,
            belts,
            inventory,
            fitting,
            clock,
            combat,
            encounters,
            missiles,
            warfare,
            tackle,
            health,
            marketSignal,
            defense
        );
}

internal sealed class CapsuleerMiner(
    long pilotId,
    WorldDatabase database,
    SqliteWorld ships,
    CapsuleerGeometry systems,
    IAsteroidField belts,
    IPilotInventory inventory,
    IPilotFitting fitting,
    TimeProvider clock,
    INpcCombat combat,
    INpcSimulation encounters,
    INpcMissiles missiles,
    INpcElectronicWarfare warfare,
    IPilotTackle tackle,
    IPilotHealth health,
    MarketChangeSignal marketSignal,
    CapsuleerDefense defense
)
{
    private const int RetrySeconds = 60;
    private const double ApproachRange = 500;
    private bool _loaded;
    private bool _background;
    private TackleEffect[] _incoming = [];
    private bool _warpBlocked;
    private CapsuleerOperation _operation = null!;
    private long _threatenedShip;
    private long _resumeCount = -1;

    public void ObserveAggression(long shipId)
    {
        if (_pilot?.ShipId == shipId)
        {
            Interlocked.Exchange(ref _threatenedShip, shipId);
        }
    }

    private int _seed;
    private DockedPilot? _pilot;
    private InventoryItem? _laser;
    private MinerProgress? _work;
    private NavigationState _navigation = new();
    private IReadOnlyList<StaticSpaceObject> _objects = [];
    private IReadOnlyList<StaticSpaceObject> _obstacles = [];
    private StaticSpaceObject? _target;
    private long _hullRevision;
    private LiveWorld? Live => LiveWorld.Find(database.Path);
    private DockedPilot Pilot => _pilot!;
    private MinerProgress Work => _work!;
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

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

    internal MinerObservation? Read(bool includeHealth = true)
    {
        if (!_loaded)
        {
            Load();
        }
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
        _resumeCount = -1;
        long delay = Math.Max(0, elapsed - Work.ActiveTicks);
        if (delay == 0)
        {
            return job;
        }
        var resumed = CapsuleerPauseRules.Resume(Work, delay);
        var next = job.Next(Math.Max(elapsed + TimeSpan.TicksPerSecond, job.DueTicks + delay), job.Action);
        using CapsuleerOperation operation = new(database, job, marketSignal);
        operation.Commit(next, new MinerCheckpoint(pilotId, resumed), null);
        _work = resumed;
        return next;
    }

    internal CapsuleerJob Execute(CapsuleerJob job, int destinyStamp, long activeNow, long resumes, bool background = false)
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
        var defended = defense.Advance(Pilot, _navigation, Work.Defense, Now, destinyStamp, _obstacles, escapeFirst: true);
        _pilot = defended.Pilot;
        _warpBlocked = defended.WarpBlocked ?? _warpBlocked;
        _work = Work with { Defense = defended.State, LastMotionStamp = defended.HoldsNavigation ? destinyStamp : Work.LastMotionStamp };
        if (defended.Escape)
        {
            ObserveAggression(Pilot.ShipId);
        }
        if (
            Interlocked.Exchange(ref _threatenedShip, 0) == Pilot.ShipId
            && Pilot.Position is not null
            && Work.Activity != MinerActivity.Returning
        )
        {
            _work = Work with { Activity = MinerActivity.ReadyToReturn };
        }
        var beforePilot = Pilot;
        var before = Work;
        var beforeLaser = _laser;
        var beforeTarget = _target;
        var beforeObjects = _objects;
        var beforeObstacles = _obstacles;
        _work = Work with { ActiveTicks = job.DueTicks, Stamp = CapsuleerScheduleRules.Stamp(job.DueTicks, Work.ClockOffsetTicks) };
        _operation = new(database, job, marketSignal);
        try
        {
            if (_resumeCount != resumes && Work.Activity == MinerActivity.Mining && Work.PaidUntil != 0 && Work.DueTicks > activeNow)
            {
                if (
                    !_operation
                        .World.Set<Eden.Data.World.Inventory.OwnedItem>()
                        .Any(i => i.Id == Work.ModuleId && i.OwnerId == pilotId && i.LocationId == Pilot.ShipId && i.Online)
                )
                {
                    throw new InvalidDataException("The funded mining laser is no longer fitted to this pilot's ship.");
                }
                long completes = checked(Now + Work.DueTicks - activeNow);
                var cooldown =
                    _operation.World.Set<Eden.Data.World.Combat.ModuleCooldown>().Find(Work.ModuleId)
                    ?? _operation.World.Add(new Eden.Data.World.Combat.ModuleCooldown { ModuleId = Work.ModuleId }).Entity;
                cooldown.Kind = Eden.Data.World.Combat.ModuleCooldownKind.Cycle;
                cooldown.ReadyAt = completes;
                _work = Work with { PaidUntil = completes };
            }
            if (_warpBlocked && _navigation.Warp is null && _navigation.WarpDestination is not null)
            {
                throw new InvalidDataException("Warp disruption interrupts the miner's alignment.");
            }
            if (!defended.HoldsNavigation)
            {
                Step();
            }
            var next = Commit(job);
            _resumeCount = resumes;
            return next;
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
            _laser = beforeLaser;
            _operation = new(database, job, marketSignal);
            _navigation.AdoptHull();
            _work = Work with
            {
                ActiveTicks = job.DueTicks,
                Stamp = CapsuleerScheduleRules.Stamp(job.DueTicks, Work.ClockOffsetTicks),
                Activity = MinerActivity.Waiting,
                DueStamp = CapsuleerScheduleRules.Stamp(job.DueTicks, Work.ClockOffsetTicks) + RetrySeconds,
                DueTicks = checked(job.DueTicks + TimeSpan.FromSeconds(RetrySeconds).Ticks),
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
                _laser = beforeLaser;
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
            _laser = beforeLaser;
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
            && Work.Defense is null
            && CapsuleerScheduleRules.WarpDeadline(Work.Navigation, Pilot.Flight, Work.ClockOffsetTicks, Work.ActiveTicks) is { } exit
        )
        {
            schedule = schedule with { DueTicks = exit };
        }
        schedule = schedule with { DueTicks = CapsuleerDefenseRules.NextDeadline(Work.Defense, Now, Work.ActiveTicks, schedule.DueTicks) };
        var next = job.Next(schedule.DueTicks, schedule.Action);
        _operation.Commit(next, new MinerCheckpoint(pilotId, Work), null);
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
        live.MinerCheckpoints[pilotId] = new(pilotId, Work);
        live.ObjectsChanged();
        return true;
    }

    private MinerObservation Observation(bool includeHealth) =>
        new(Pilot, Work, _laser, Work.WaitingReason) { Health = includeHealth ? health.Read(pilotId, Now) : null };

    private void ObserveDanger(int destinyStamp)
    {
        if (Pilot.Position is null || _navigation.Warp is not null || Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
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
                new Random(unchecked(_seed ^ Work.Stamp ^ npc.GetHashCode())).NextDouble(),
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

    private void Load()
    {
        using WorldDbContext world = new(database.Path);
        var row = world.Set<SavedCapsuleerCommunity>().AsNoTracking().SingleOrDefault(c => c.PilotId == pilotId);
        if (row?.PilotId is long id)
        {
            long account = world.Pilots.Where(p => p.Id == id).Select(p => p.AccountId).Single();
            _pilot = ships.FindPilot(account, id)!;
            _seed = row.Seed;
            _work = Live?.MinerCheckpoints.GetValueOrDefault(pilotId)?.Work ?? MinerCheckpoint.Read(row.Progress);
            var previous = _work;
            long anchor =
                previous?.ActiveTicks
                ?? (world.Set<SavedCapsuleerJob>().Single(j => j.PilotId == pilotId).DueTicks - TimeSpan.TicksPerSecond);
            if (_work is null || Work.ShipId != Pilot.ShipId)
            {
                _navigation = new();
                _work = new(
                    Pilot.Position is null ? MinerActivity.Docked : MinerActivity.ReadyToReturn,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    _navigation.Capture(),
                    Pilot.ShipId
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
            _incoming = [];
            _warpBlocked = false;
            _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
            RefreshObjects();
            _target = _objects.FirstOrDefault(o => o.Id == Work.TargetId);
            _laser = inventory.List(id, Pilot.ShipId).FirstOrDefault(i => i.Id == Work.ModuleId);
        }
        _loaded = true;
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

    private void RefreshObjects()
    {
        _objects = _operation is { HasWorld: true }
            ? CapsuleerGeometry.Load(_operation.World, _operation.Catalog, Pilot.SystemId, Now)
            : systems.Load(Pilot.SystemId);
        _obstacles = _objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup).ToArray();
    }

    private void Step()
    {
        switch (Work.Activity)
        {
            case MinerActivity.Docked:
                PrepareTrip();
                break;
            case MinerActivity.FlyingToBelt:
            case MinerActivity.Returning:
                Travel();
                break;
            case MinerActivity.ApproachingOre:
                Approach();
                break;
            case MinerActivity.Locking when Work.ActiveTicks >= Work.DueTicks:
                PayCycle();
                break;
            case MinerActivity.Mining when Work.ActiveTicks >= Work.DueTicks:
                Collect();
                break;
            case MinerActivity.ReadyToReturn:
                ReturnHome();
                break;
            case MinerActivity.Waiting when Work.ActiveTicks >= Work.DueTicks:
                _work = Work with { WaitingReason = null };
                if (Pilot.Position is null)
                {
                    _work = Work with { Activity = MinerActivity.Docked };
                }
                else
                {
                    ReturnHome();
                }
                break;
        }
    }

    private void PrepareTrip()
    {
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Waiting for a replacement mining ship.");
        }
        CapsuleerSupplyStore.Ensure(
            _operation.World,
            _operation.Catalog,
            pilotId,
            _operation.World.Set<SavedCapsuleerAgent>().Find(pilotId)!.FounderShipId
        );
        var aboard = PilotInventoryStore.List(_operation.World, _operation.Catalog, Pilot.PilotId, Pilot.ShipId);
        var ore = aboard.Where(i => i.CategoryId == AsteroidDefinitions.Category && ShipHoldRules.IsHold(i.FlagId)).ToArray();
        if (ore.Length > 0)
        {
            PilotInventoryStore.Move(
                _operation.World,
                _operation.Catalog,
                Pilot.PilotId,
                Pilot.StationId,
                new(ore.Select(i => i.Id).ToArray(), Pilot.ShipId, (int)InventoryFlag.Hangar, null),
                Now
            );
        }
        _laser = aboard.FirstOrDefault(i => i.TypeId == (int)MiningLaserType.MinerI && InventoryDefinitions.IsHighSlot(i.FlagId));
        if (_laser is null)
        {
            var kit =
                PilotInventoryStore
                    .List(_operation.World, _operation.Catalog, Pilot.PilotId, Pilot.StationId)
                    .FirstOrDefault(i => i.TypeId == (int)MiningLaserType.MinerI)
                ?? throw new InvalidDataException("Waiting for a mining laser.");
            PilotFittingStore.Fit(
                _operation.World,
                _operation.Catalog,
                Pilot.PilotId,
                [kit.Id],
                Pilot.StationId,
                (int)InventoryFlag.AutoFit,
                Now
            );
            _laser = PilotInventoryStore
                .List(_operation.World, _operation.Catalog, Pilot.PilotId, Pilot.ShipId)
                .Single(i => i.Id == kit.Id);
        }
        if (!_laser.Online)
        {
            PilotFittingStore.SetOnline(_operation.World, _operation.Catalog, Pilot.PilotId, Pilot.ShipId, _laser.Id, true, Now);
            _laser = _laser with { Online = true };
        }
        RefreshObjects();
        var fields = _objects
            .Where(o =>
                o.GroupId == AsteroidDefinitions.BeltGroup && CapsuleerProvisioningCatalog.UsableOre(_operation.Catalog, checked((int)o.Id))
            )
            .OrderBy(o => o.Id)
            .ToArray();
        if (fields.Length == 0)
        {
            throw new InvalidDataException("Waiting for a known asteroid belt.");
        }
        var field = fields[(int)((uint)(_seed + Work.Trips) % (uint)fields.Length)];
        var launch = StationUndocking.Launch(Pilot.ShipId, Pilot.Home, Pilot.Flight);
        SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, launch.Position, Pilot.SystemId, Now);
        _operation.TravelChanged = true;
        _pilot = Pilot with { Position = launch.Position };
        _navigation.AdoptHull();
        _navigation.SetVelocity(launch.Velocity);
        _work = Work with { ModuleId = _laser.Id, Cycles = 0, Trips = Work.Trips + 1 };
        StartCourse(field, MinerActivity.FlyingToBelt);
    }

    private void StartCourse(StaticSpaceObject target, MinerActivity activity)
    {
        _target = target;
        _work = Work with { TargetId = target.Id, Activity = activity };
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the miner from leaving.");
        }
        if (!_navigation.StartWarp(Pilot.ShipId, Pilot.Flight, Pilot.Position!, target, 0, Work.Stamp))
        {
            Follow(target);
        }
    }

    private void Follow(StaticSpaceObject target) =>
        _navigation.Follow(Pilot.ShipId, Work.Stamp, Pilot.Position!, Pilot.Flight.ShipRadius, target, ApproachRange, false, true);

    private double Distance(StaticSpaceObject target) =>
        Pilot.Position!.DistanceTo(target.Position) - target.Radius - Pilot.Flight.ShipRadius;

    private void Move()
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
        if (!wasWarping && _navigation.Warp is not null)
        {
            Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, Pilot.ShipId);
        }
        if (Live?.Combat is { } residentCombat)
        {
            residentCombat.PublishPilotPosition(Pilot.ShipId, Pilot.SystemId, tick.Position);
        }
        else
        {
            SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, tick.Position, Pilot.SystemId, Now);
        }
    }

    private void Travel()
    {
        Move();
        if (_navigation.Warp is not null || _navigation.WarpDestination is not null)
        {
            return;
        }
        if (Work.Activity == MinerActivity.Returning)
        {
            if (Distance(_target!) <= DockingDefinitions.MaximumRange)
            {
                _pilot = ships.Dock(_operation.World, _operation.Catalog, Pilot.PilotId, Pilot.ShipId, Pilot.StationId);
                Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, Pilot.ShipId);
                _navigation.Dock();
                _operation.TravelChanged = true;
                _work = Work with { Activity = MinerActivity.Docked, TargetId = 0 };
            }
            else
            {
                Follow(_target!);
            }
            return;
        }
        var rock =
            belts.List(Pilot.SystemId).Where(o => Distance(o) <= AsteroidDefinitions.VisibilityRange).MinBy(o => Distance(o))
            ?? throw new InvalidDataException("No ore is visible at this belt.");
        _target = rock;
        Follow(rock);
        _work = Work with { Activity = MinerActivity.ApproachingOre, TargetId = rock.Id };
    }

    private void Approach()
    {
        if (_target is not { } target || _laser is null)
        {
            throw new InvalidDataException("Mining target or fitted laser unavailable.");
        }
        Move();
        if (Distance(target) > ApproachRange * 2)
        {
            return;
        }
        var attributes = _operation.HasWorld
            ? PilotFittingStore.ShipAttributes(_operation.World, _operation.Catalog, Pilot.PilotId)
            : fitting.ShipAttributes(Pilot.PilotId);
        if (
            attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets) <= 0
            || Distance(target) > attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange)
        )
        {
            throw new InvalidDataException("The asteroid is outside the ship's targeting limits.");
        }
        double seconds = TargetingDefinitions.LockSeconds(
            attributes.GetValueOrDefault(DogmaAttributeId.ScanResolution),
            _operation.HasWorld
                ? PilotFittingStore.SignatureRadius(_operation.World, _operation.Catalog, target.Id, target.TypeId)
                : fitting.SignatureRadius(target.Id, target.TypeId)
        );
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            throw new InvalidDataException("The ship cannot lock this asteroid.");
        }
        _navigation.SetAtRest();
        _work = Work with
        {
            Activity = MinerActivity.Locking,
            DueStamp = Work.Stamp + (int)Math.Ceiling(seconds),
            DueTicks = checked(Work.ActiveTicks + (long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond)),
        };
    }

    private void PayCycle()
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        var start = PilotMiningStore.Begin(world, catalog, Pilot.PilotId, Work.ModuleId, Work.TargetId, Now);
        int seconds = checked((int)Math.Ceiling(start.Laser.DurationMilliseconds / 1000));
        var next = Work with
        {
            Activity = MinerActivity.Mining,
            DueStamp = Work.Stamp + seconds,
            CycleSeconds = seconds,
            DueTicks = checked(Work.ActiveTicks + (long)Math.Ceiling(start.Laser.DurationMilliseconds * TimeSpan.TicksPerMillisecond)),
            PaidUntil = checked(Now + (long)(start.Laser.DurationMilliseconds * TimeSpan.TicksPerMillisecond)),
            Navigation = _navigation.Capture(),
        };
        _work = next;
    }

    private void Collect()
    {
        var world = _operation.World;
        var catalog = _operation.Catalog;
        if (world.Set<WorldAsteroid>().Find(Work.TargetId) is not { Quantity: > 0 })
        {
            var searching = Work with { Activity = MinerActivity.FlyingToBelt, Navigation = _navigation.Capture() };
            _work = searching;
            return;
        }
        var random = new Random(unchecked(_seed + Work.DueStamp));
        MiningRolls rolls = new(random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble());
        var result = PilotMiningStore.Collect(world, catalog, Pilot.PilotId, Work.ModuleId, Work.TargetId, new(1, true, rolls), Now);
        var next = Work with { Activity = MinerActivity.Locking, Cycles = Work.Cycles + 1, Navigation = _navigation.Capture() };
        if (result.CargoFull)
        {
            next = next with { Activity = MinerActivity.ReadyToReturn };
        }
        if (result.Depleted && !result.CargoFull)
        {
            next = next with { Activity = MinerActivity.FlyingToBelt };
        }
        _work = next;
    }

    private void ReturnHome()
    {
        if (Work.PaidUntil != 0)
        {
            PilotMiningStore.Stop(_operation.World, Pilot.PilotId, Work.ModuleId, Work.PaidUntil);
            _work = Work with { PaidUntil = 0 };
        }
        StartCourse(_objects.Single(o => o.Id == Pilot.StationId), MinerActivity.Returning);
    }
}
