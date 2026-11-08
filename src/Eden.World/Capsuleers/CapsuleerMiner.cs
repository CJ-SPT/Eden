using Eden.Data.World.Capsuleers;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
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
    ISystemObjects systems,
    IAsteroidField belts,
    IPilotInventory inventory,
    IPilotFitting fitting,
    TimeProvider clock,
    INpcCombat combat,
    INpcEncounters encounters,
    INpcMissiles missiles,
    IPilotHealth health,
    MarketChangeSignal marketSignal,
    PilotMarketStore marketStore
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
            health,
            marketSignal,
            marketStore
        );
}

internal sealed class CapsuleerMiner(
    long pilotId,
    WorldDatabase database,
    SqliteWorld ships,
    ISystemObjects systems,
    IAsteroidField belts,
    IPilotInventory inventory,
    IPilotFitting fitting,
    TimeProvider clock,
    INpcCombat combat,
    INpcEncounters encounters,
    INpcMissiles missiles,
    IPilotHealth health,
    MarketChangeSignal marketSignal,
    PilotMarketStore marketStore
)
{
    private const int RetrySeconds = 60;
    private const double ApproachRange = 500;
    private bool _loaded;
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

    internal MinerObservation? Read()
    {
        if (!_loaded)
        {
            Load();
        }
        return _pilot is null ? null : Observation();
    }

    internal CapsuleerJob Execute(CapsuleerJob job, int destinyStamp, long activeNow, long resumes)
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
        ObserveDanger(destinyStamp);
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
            Step();
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
        var next = job.Next(schedule.DueTicks, schedule.Action);
        _operation.Commit(next, new MinerCheckpoint(pilotId, Work), null);
        return next;
    }

    private MinerObservation Observation() => new(Pilot, Work, _laser, Work.WaitingReason) { Health = health.Read(pilotId, Now) };

    private void ObserveDanger(int destinyStamp)
    {
        if (Pilot.Position is null || _navigation.Warp is not null || Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            return;
        }
        var visible = encounters
            .List(Pilot.SystemId, Now)
            .Where(n => n.Warp is null && n.Position.DistanceTo(Pilot.Position) <= AsteroidDefinitions.VisibilityRange)
            .Select(n => n.Id)
            .ToHashSet();
        combat.Acquire([new(Pilot.PilotId, Pilot.ShipId)], Now);
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
        if (attackers.Length > 0 && Work.Activity != MinerActivity.Returning)
        {
            _work = Work with { Activity = MinerActivity.ReadyToReturn };
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
            _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
            RefreshObjects();
            _target = _objects.FirstOrDefault(o => o.Id == Work.TargetId);
            _laser = inventory.List(id, Pilot.ShipId).FirstOrDefault(i => i.Id == Work.ModuleId);
        }
        _loaded = true;
    }

    private void RefreshObjects()
    {
        _objects = _operation is { HasWorld: true }
            ? SystemObjects.Load(_operation.World, _operation.Catalog, Pilot.SystemId, Now)
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
        if (_operation.World.Set<SavedCapsuleerHauler>().Any())
        {
            _operation.MarketChanged |= CapsuleerTrade.ListStock(
                _operation.World,
                _operation.Catalog,
                marketStore,
                Pilot.PilotId,
                Pilot.StationId
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
        var fields = _objects.Where(o => o.GroupId == AsteroidDefinitions.BeltGroup).OrderBy(o => o.Id).ToArray();
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
