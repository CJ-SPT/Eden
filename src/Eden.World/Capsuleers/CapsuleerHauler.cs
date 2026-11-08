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
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerHauler(
    WorldDatabase database,
    SqliteWorld ships,
    ISystemObjects systems,
    IPilotInventory inventory,
    IPilotMarket market,
    IPersonalMarket orders,
    IPilotGateTravel gates,
    IPilotCrimewatch crimewatch,
    IPilotTackle tackle,
    INpcCombat combat,
    INpcEncounters encounters,
    INpcMissiles missiles,
    IPilotHealth health,
    CapsuleerTrade trade,
    TimeProvider clock
) : ICapsuleerHauling
{
    private const double ApproachRange = 500;
    private readonly Lock _gate = new();
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
    private long _provisionRevision;
    private long _hullRevision;
    private long _miner;
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

    public HaulerObservation? Advance(int destinyStamp)
    {
        lock (_gate)
        {
            using var owner = Live?.Enter();
            if (!_loaded || _pilot is null && Live is null || Live is { } live && _provisionRevision != live.CapsuleerRevision)
            {
                Load();
            }
            if (_pilot is null)
            {
                return null;
            }
            if (Live is { } resident && _hullRevision != resident.HaulerHullRevision)
            {
                _hullRevision = resident.HaulerHullRevision;
                using WorldDbContext world = new(database.Path);
                if (!world.Pilots.Any(p => p.Id == Pilot.PilotId && p.ActiveShipId == Pilot.ShipId))
                {
                    Load();
                }
            }
            _work = Work with { Stamp = checked(Work.Stamp + 1) };
            try
            {
                if (Pilot.Position is not null)
                {
                    RefreshTackle();
                    if (_warpBlocked && _navigation.Warp is null && _navigation.WarpDestination is not null)
                    {
                        throw new InvalidDataException("Warp disruption interrupts the hauler's alignment.");
                    }
                }
                ObserveDanger(destinyStamp);
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
            }
            catch (Exception error) when (error is InvalidDataException or CargoCapacityException)
            {
                _navigation.AdoptHull();
                _work = Work with
                {
                    Activity = HaulerActivity.Waiting,
                    DueStamp = Work.Stamp + HaulingRules.PollSeconds,
                    WaitingReason = error.Message,
                };
            }
            _work = Work with { Navigation = _navigation.Capture() };
            var checkpoint = new HaulerCheckpoint(Pilot.PilotId, Work);
            if (Live is { } current)
            {
                current.HaulerCheckpoint = checkpoint;
                current.ObjectsChanged();
            }
            else
            {
                using WorldDbContext world = new(database.Path);
                checkpoint.Apply(world);
            }
            return new(Pilot, Work, health.Read(Pilot.PilotId, Now));
        }
    }

    private void Load()
    {
        using WorldDbContext world = new(database.Path);
        _founder = world.Set<SavedCapsuleerHauler>().AsNoTracking().SingleOrDefault();
        _provisionRevision = Live?.CapsuleerRevision ?? 0;
        _loaded = true;
        if (_founder is null)
        {
            return;
        }
        _miner = world.Set<SavedCapsuleerCommunity>().Single().PilotId!.Value;
        long account = world.Pilots.Where(p => p.Id == Founder.PilotId).Select(p => p.AccountId).Single();
        _pilot = ships.FindPilot(account, Founder.PilotId)!;
        using CatalogDbContext catalog = new(database.CatalogPath);
        _sourceSystem = catalog.RequireStation(Founder.SourceStationId).SolarSystemId;
        _incoming = [];
        _warpBlocked = false;
        _work = Live?.HaulerCheckpoint?.Work ?? HaulerCheckpoint.Read(Founder.Progress);
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
        _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
        RefreshObjects();
        _target = _objects.FirstOrDefault(o => o.Id == Work.TargetId);
    }

    private void RefreshObjects()
    {
        _objects = systems.Load(Pilot.SystemId);
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
            case HaulerActivity.Jumping when Work.Stamp >= Work.DueStamp:
                CompleteJump();
                break;
            case HaulerActivity.Waiting when Work.Stamp >= Work.DueStamp:
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
        inventory
            .List(Pilot.PilotId, location)
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
        var cargo = Ore(Pilot.ShipId);
        if (Pilot.StationId == Founder.DestinationStationId)
        {
            if (cargo.Length > 0)
            {
                inventory.Move(
                    Pilot.PilotId,
                    Pilot.StationId,
                    new(cargo.Select(i => i.Id).ToArray(), Pilot.ShipId, (int)InventoryFlag.Hangar, null)
                );
            }
            trade.ListStock(Pilot.PilotId, Pilot.StationId, HaulingRules.DestinationMultiplier);
            // Reserved market stock stays at this station until a buyer fills the ordinary order.
            _work = Work with
            {
                DestinationStationId = Founder.SourceStationId,
            };
        }
        else
        {
            if (cargo.Length == 0)
            {
                var stock = Ore(Pilot.StationId);
                if (stock.Length == 0)
                {
                    var offer = orders
                        .PersonalOrders(_miner)
                        .Where(o => !o.Bid && o.StationId == Founder.SourceStationId)
                        .OrderBy(o => o.PriceCents)
                        .ThenBy(o => o.Id)
                        .FirstOrDefault();
                    if (offer is null)
                    {
                        Wait("Waiting for mined ore on the home-station market.");
                        return;
                    }
                    using CatalogDbContext catalog = new(database.CatalogPath);
                    double volume = PilotInventoryStore.Volume(catalog, offer.TypeId);
                    int quantity = HaulingRules.PurchaseQuantity(
                        offer.Remaining,
                        offer.PriceCents,
                        market.Balance(Pilot.PilotId),
                        Pilot.CargoCapacity,
                        volume
                    );
                    if (quantity == 0)
                    {
                        Wait("Waiting for affordable ore and destination listing funds.");
                        return;
                    }
                    market.Buy(Pilot.PilotId, Founder.SourceStationId, [new(offer.TypeId, offer.PriceCents, quantity)]);
                    stock = Ore(Pilot.StationId);
                }
                var load = stock.FirstOrDefault();
                if (load is null)
                {
                    Wait("The quoted ore was purchased by another pilot.");
                    return;
                }
                int units = (int)Math.Min(load.Quantity, Math.Floor(Pilot.CargoCapacity / load.Volume));
                if (units <= 0)
                {
                    throw new InvalidDataException("The ore does not fit in the hauling ship.");
                }
                inventory.Move(Pilot.PilotId, Pilot.ShipId, new([load.Id], Pilot.StationId, (int)InventoryFlag.Cargo, units));
            }
            _work = Work with { DestinationStationId = Founder.DestinationStationId };
        }
        var launch = StationUndocking.Launch(Pilot.ShipId, Pilot.Home, Pilot.Flight);
        ships.SetPosition(Pilot.ShipId, launch.Position, Pilot.SystemId);
        _pilot = Pilot with { Position = launch.Position };
        _navigation.AdoptHull();
        _navigation.SetVelocity(launch.Velocity);
        RefreshObjects();
        BeginTravel();
    }

    private void Wait(string reason) =>
        _work = Work with { Activity = HaulerActivity.Waiting, DueStamp = Work.Stamp + HaulingRules.PollSeconds, WaitingReason = reason };

    private void BeginTravel()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        int destination = catalog.RequireStation(Work.DestinationStationId).SolarSystemId;
        _target =
            destination == Pilot.SystemId
                ? _objects.Single(o => o.Id == Work.DestinationStationId)
                : _objects.Where(o => o.DestinationSystemId == destination).OrderBy(o => o.Id).FirstOrDefault()
                    ?? throw new InvalidDataException("The saved hauling route has no connected stargate.");
        crimewatch.CheckTravel(Pilot.PilotId, CrimewatchTravel.Warp);
        RefreshTackle();
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
            ships.SetPosition(Pilot.ShipId, tick.Position, Pilot.SystemId);
        }
        if (!wasWarping && _navigation.Warp is not null)
        {
            combat.Disengage(Pilot.ShipId);
        }
        if (_navigation.Warp is not null || _navigation.WarpDestination is not null)
        {
            return;
        }
        if (_target!.DestinationGateId is not null)
        {
            if (GateTravelRules.InRange(Pilot.Position!, Pilot.Flight.ShipRadius, _target))
            {
                crimewatch.CheckTravel(Pilot.PilotId, CrimewatchTravel.Jump, _target.DestinationSystemId);
                _navigation.SetAtRest();
                combat.Disengage(Pilot.ShipId);
                _work = Work with { Activity = HaulerActivity.Jumping, DueStamp = Work.Stamp + GateTravelRules.DurationTicks };
            }
            else
            {
                Follow();
            }
        }
        else if (Pilot.Position!.DistanceTo(_target.Position) - Pilot.Flight.ShipRadius - _target.Radius <= DockingDefinitions.MaximumRange)
        {
            _pilot = ships.Dock(Pilot.PilotId, Pilot.ShipId, Work.DestinationStationId);
            combat.Disengage(Pilot.ShipId);
            _navigation.Dock();
            _work = Work with { Activity = HaulerActivity.Docked, TargetId = 0 };
        }
        else
        {
            Follow();
        }
    }

    private void CompleteJump()
    {
        _pilot = gates.Transfer(Pilot.PilotId, Pilot.ShipId, Work.TargetId, _target!.DestinationGateId!.Value);
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
