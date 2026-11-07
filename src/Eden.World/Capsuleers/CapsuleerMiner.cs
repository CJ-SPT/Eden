using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerMiner(
    WorldDatabase database,
    SqliteWorld ships,
    ISystemObjects systems,
    IAsteroidField belts,
    IPilotFleet fleet,
    IPilotInventory inventory,
    IPilotFitting fitting,
    TimeProvider clock
) : ICapsuleerSimulation
{
    private const int RetrySeconds = 60;
    private const int CyclesPerTrip = 3;
    private const double ApproachRange = 500;
    private readonly object _gate = new();
    private bool _loaded;
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

    public MinerObservation? Advance()
    {
        lock (_gate)
        {
            using var owner = Live?.Enter();
            if (!_loaded)
            {
                Load();
            }
            if (_pilot is null)
            {
                return null;
            }
            if (Live is { } ownerWorld && _hullRevision != ownerWorld.MinerHullRevision)
            {
                _hullRevision = ownerWorld.MinerHullRevision;
                using WorldDbContext world = new(database.Path);
                if (!world.Pilots.Any(p => p.Id == Pilot.PilotId && p.ActiveShipId == Pilot.ShipId))
                {
                    Load();
                }
            }
            _work = Work with { Stamp = checked(Work.Stamp + 1) };
            try
            {
                Step();
            }
            catch (Exception error) when (error is InvalidDataException or CargoCapacityException)
            {
                _navigation.AdoptHull();
                _work = Work with { Activity = MinerActivity.Waiting, DueStamp = Work.Stamp + RetrySeconds, WaitingReason = error.Message };
            }
            _work = Work with { Navigation = _navigation.Capture() };
            var checkpoint = new MinerCheckpoint(Pilot.PilotId, Work);
            if (Live is { } live)
            {
                live.MinerCheckpoint = checkpoint;
                live.ObjectsChanged();
            }
            else
            {
                using WorldDbContext world = new(database.Path);
                checkpoint.Apply(world);
            }
            return new(Pilot, Work, _laser, Work.WaitingReason);
        }
    }

    private void Load()
    {
        using WorldDbContext world = new(database.Path);
        var row = world.Set<SavedCapsuleerCommunity>().AsNoTracking().SingleOrDefault();
        if (row?.PilotId is long id)
        {
            long account = world.Pilots.Where(p => p.Id == id).Select(p => p.AccountId).Single();
            _pilot = ships.FindPilot(account, id)!;
            _seed = row.Seed;
            _work = Live?.MinerCheckpoint?.Work ?? MinerCheckpoint.Read(row.Progress);
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
            _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
            RefreshObjects();
            _target = _objects.FirstOrDefault(o => o.Id == Work.TargetId);
            _laser = inventory.List(id, Pilot.ShipId).FirstOrDefault(i => i.Id == Work.ModuleId);
        }
        _loaded = true;
    }

    private void RefreshObjects()
    {
        _objects = systems.Load(Pilot.SystemId);
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
            case MinerActivity.Locking when Work.Stamp >= Work.DueStamp:
                PayCycle();
                break;
            case MinerActivity.Mining when Work.Stamp >= Work.DueStamp:
                Collect();
                break;
            case MinerActivity.ReadyToReturn:
                ReturnHome();
                break;
            case MinerActivity.Waiting when Work.Stamp >= Work.DueStamp:
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
            _pilot = fleet.RecoverCorvette(Pilot.PilotId, Pilot.ShipId, Pilot.StationId).Pilot;
            _work = Work with { ShipId = Pilot.ShipId };
        }
        var aboard = inventory.List(Pilot.PilotId, Pilot.ShipId);
        var ore = aboard.Where(i => i.CategoryId == AsteroidDefinitions.Category && i.FlagId == (int)InventoryFlag.Cargo).ToArray();
        if (ore.Length > 0)
        {
            inventory.Move(Pilot.PilotId, ore.Select(i => i.Id).ToArray(), Pilot.ShipId, Pilot.StationId, (int)InventoryFlag.Hangar, null);
        }
        _laser = aboard.FirstOrDefault(i =>
            i.TypeId == InventoryDefinitions.CivilianMinerType && InventoryDefinitions.IsHighSlot(i.FlagId)
        );
        if (_laser is null)
        {
            var kit =
                inventory.List(Pilot.PilotId, Pilot.StationId).FirstOrDefault(i => i.TypeId == InventoryDefinitions.CivilianMinerType)
                ?? throw new InvalidDataException("Waiting for a civilian mining laser.");
            fitting.Fit(Pilot.PilotId, [kit.Id], Pilot.StationId, (int)InventoryFlag.AutoFit);
            _laser = inventory.List(Pilot.PilotId, Pilot.ShipId).Single(i => i.Id == kit.Id);
        }
        if (!_laser.Online)
        {
            fitting.SetOnline(Pilot.PilotId, Pilot.ShipId, _laser.Id, true);
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
        ships.SetPosition(Pilot.ShipId, launch.Position, Pilot.SystemId);
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
        var tick = _navigation.Advance(
            Pilot.ShipId,
            Work.Stamp,
            Pilot.Flight,
            Pilot.Position!,
            id => _target?.Id == id ? _target : null,
            _obstacles
        );
        _pilot = Pilot with { Position = tick.Position };
        if (Live?.Combat is { } combat)
        {
            combat.PublishPilotPosition(Pilot.ShipId, Pilot.SystemId, tick.Position);
        }
        else
        {
            ships.SetPosition(Pilot.ShipId, tick.Position, Pilot.SystemId);
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
                _pilot = ships.Dock(Pilot.PilotId, Pilot.ShipId, Pilot.StationId);
                _navigation.Dock();
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
        var attributes = fitting.ShipAttributes(Pilot.PilotId);
        if (
            attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets) <= 0
            || Distance(target) > attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange)
        )
        {
            throw new InvalidDataException("The asteroid is outside the ship's targeting limits.");
        }
        double seconds = TargetingDefinitions.LockSeconds(
            attributes.GetValueOrDefault(DogmaAttributeId.ScanResolution),
            fitting.SignatureRadius(target.Id, target.TypeId)
        );
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            throw new InvalidDataException("The ship cannot lock this asteroid.");
        }
        _navigation.SetAtRest();
        _work = Work with { Activity = MinerActivity.Locking, DueStamp = Work.Stamp + (int)Math.Ceiling(seconds) };
    }

    private void PayCycle()
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var start = PilotMiningStore.Begin(world, catalog, Pilot.PilotId, Work.ModuleId, Work.TargetId, Now);
        int seconds = checked((int)Math.Ceiling(start.Laser.DurationMilliseconds / 1000));
        var next = Work with
        {
            Activity = MinerActivity.Mining,
            DueStamp = Work.Stamp + seconds,
            CycleSeconds = seconds,
            Navigation = _navigation.Capture(),
        };
        new MinerCheckpoint(Pilot.PilotId, next).Apply(world);
        transaction.Commit();
        _work = next;
    }

    private void Collect()
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        var random = new Random(unchecked(_seed + Work.DueStamp));
        MiningRolls rolls = new(random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble());
        var result = PilotMiningStore.Collect(world, catalog, Pilot.PilotId, Work.ModuleId, Work.TargetId, new(1, true, rolls), Now);
        var next = Work with { Activity = MinerActivity.Locking, Cycles = Work.Cycles + 1, Navigation = _navigation.Capture() };
        if (next.Cycles >= CyclesPerTrip || result.CargoFull || result.Depleted)
        {
            next = next with { Activity = MinerActivity.ReadyToReturn };
        }
        new MinerCheckpoint(Pilot.PilotId, next).Apply(world);
        transaction.Commit();
        _work = next;
    }

    private void ReturnHome() => StartCourse(_objects.Single(o => o.Id == Pilot.StationId), MinerActivity.Returning);
}
