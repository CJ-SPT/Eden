using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Loot;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Flight;

internal sealed class FlightSession(
    IShipPositionStore world,
    DockedPilot pilot,
    TimeProvider clock,
    SharedSpace space,
    IReadOnlyList<StaticSpaceObject> objects,
    FittedSpaceModule[]? modules = null,
    IStationDocking? docking = null,
    IPilotHealth? health = null,
    INpcEncounters? encounters = null,
    IWrecks? wrecks = null
) : IDisposable
{
    private FlightEffects? _effects;
    private FlightEffects Effects => _effects ??= new(Update);
    private readonly NavigationState _navigation = new();
    private ShipContact[] _peers = [];
    private StaticSpaceObject[] _visibleAsteroids = [];
    private MiningContact[] _mining = [];
    private WeaponContact[] _weapons = [];
    private ShieldBoostContact[] _shieldBoosts = [];
    private PropulsionContact? _propulsion;
    private FlightProperties _baseFlight = pilot.Flight;
    private NpcContact[] _npcs = [];
    private WreckContact[] _wrecks = [];
    private bool _destroying;
    private int _lastPeerSync;
    private int _lastNpcSync;
    private const int NpcReconciliationTicks = 10;
    public bool InBallpark { get; private set; }
    private int _simulatedStamp = space.Stamp(clock);
    private int Stamp => space.Stamp(clock);
    internal int CurrentStamp => Stamp;

    private sealed record GateTransit(
        long SourceId,
        StaticSpaceObject Exit,
        FlightSystem System,
        IReadOnlyList<StaticSpaceObject> Objects,
        int TransferStamp
    );

    // Build 3662168 client/effects defines effects.JumpOut with duration 10,000 ms.
    private const int JumpDurationTicks = 10;
    private GateTransit? _jump;
    private bool _awaitingArrival;
    private int? _arrivalStamp;
    public DockedPilot Pilot { get; private set; } = pilot;
    public List<StampedDestinyUpdate> Updates { get; } = [];
    public bool Moving => _navigation.Direction is not null || _navigation.Warp is not null || _navigation.Velocity != SpacePosition.Zero;
    public bool CanTarget =>
        InBallpark
        && !_destroying
        && Pilot.Position is not null
        && _jump is null
        && _navigation.Warp is null
        && _navigation.WarpDestination is null;

    // Pinned shieldBoosting effect 4 is warp-safe. Gate/session transitions and
    // the terminal hull transition still prevent local module activations.
    public bool CanUseSelfModules => InBallpark && !_destroying && Pilot.Position is not null && _jump is null;
    public SpacePosition Velocity => _navigation.Velocity;
    public float BaseSpeed => _baseFlight.MaxSpeed;

    public TargetContact? FindTarget(long id)
    {
        if (!CanTarget)
        {
            return null;
        }
        if (
            space
                .Peers(Pilot)
                .FirstOrDefault(s => s.Pilot.ShipId == id && s.Warp is null && s.AlignTo is null && s.DepartureGate is null) is
            { } ship
        )
        {
            return new(id, ship.Pilot.ShipTypeId, ship.Pilot.Position!, ship.Pilot.Flight.ShipRadius);
        }
        if (_npcs.FirstOrDefault(n => n.Id == id) is { } npc)
        {
            return new(id, npc.TypeId, npc.Position, npc.Radius);
        }
        if (_wrecks.FirstOrDefault(w => w.Id == id) is { } wreck)
        {
            return new(id, wreck.TypeId, wreck.Position, wreck.Radius);
        }
        return VisibleObjects().FirstOrDefault(o => o.Id == id && o.CategoryId == AsteroidDefinitions.Category) is { } asteroid
            ? new(id, asteroid.TypeId, asteroid.Position, asteroid.Radius)
            : null;
    }

    private StaticSpaceObject[] VisibleObjects() =>
        objects
            .Where(o =>
                o.CategoryId != AsteroidDefinitions.Category
                || (Pilot.Position is { } position && position.DistanceTo(o.Position) <= AsteroidDefinitions.VisibilityRange)
            )
            .ToArray();

    public IReadOnlyList<NpcContact> VisibleNpcs => _npcs;
    public IReadOnlyList<WreckContact> VisibleWrecks => _wrecks;

    private StaticSpaceObject? NavigationTarget(long id)
    {
        if (id == Pilot.ShipId || Pilot.Position is not { } position)
        {
            return null;
        }
        var target =
            VisibleObjects().FirstOrDefault(o => o.Id == id)
            ?? (
                space
                    .Peers(Pilot)
                    .FirstOrDefault(s => s.Pilot.ShipId == id && s.Warp is null && s.AlignTo is null && s.DepartureGate is null)
                    is { } peer
                    ? new StaticSpaceObject(
                        peer.Pilot.ShipId,
                        peer.Pilot.ShipTypeId,
                        peer.Pilot.ShipGroupId,
                        peer.Pilot.ShipCategoryId,
                        checked((int)peer.Pilot.PilotId),
                        peer.Pilot.Position!,
                        peer.Pilot.Flight.ShipRadius
                    )
                    : null
            )
            ?? (
                _npcs.FirstOrDefault(n => n.Id == id) is { } npc
                    ? new(npc.Id, npc.TypeId, npc.GroupId, npc.CategoryId, npc.CorporationId, npc.Position, npc.Radius)
                : _wrecks.FirstOrDefault(w => w.Id == id) is { } wreck
                    ? new(
                        wreck.Id,
                        wreck.TypeId,
                        WreckDefinitions.Group,
                        WreckDefinitions.Category,
                        checked((int)wreck.OwnerId),
                        wreck.Position,
                        wreck.Radius
                    )
                : null
            );
        return target is not null && position.DistanceTo(target.Position) <= NavigationDefinitions.MaximumTargetDistance ? target : null;
    }

    private WreckContact[] ReadWrecks() =>
        Pilot.Position is not { } position
            ? []
            : (wrecks?.List(Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) ?? [])
                .Where(w => w.SystemId == Pilot.SystemId && position.DistanceTo(w.Position) <= WreckDefinitions.VisibilityRange)
                .ToArray();

    public void RefreshWrecks()
    {
        if (!InBallpark)
        {
            return;
        }
        var current = ReadWrecks();
        foreach (var departed in _wrecks.ExceptBy(current.Select(w => w.Id), w => w.Id))
        {
            Update(new DestinyCommand.RemoveBall(departed.Id, false));
        }
        var added = current.ExceptBy(_wrecks.Select(w => w.Id), w => w.Id).ToArray();
        if (added.Length > 0)
        {
            Update(new DestinyCommand.AddBalls(Ballpark.AddWrecks(added, Stamp)));
        }
        foreach (var wreck in current)
        {
            if (_wrecks.FirstOrDefault(w => w.Id == wreck.Id) is { } previous && previous != wreck)
            {
                Update(new DestinyCommand.OnSlimItemChange(wreck.Id, Ballpark.WreckSlim(wreck)));
            }
        }
        _wrecks = current;
    }

    private NpcContact[] ReadNpcs() =>
        Pilot.Position is not { } position
            ? []
            : (encounters?.List(Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) ?? [])
                .Where(n => n.SystemId == Pilot.SystemId && position.DistanceTo(n.Position) <= NpcEncounterDefinitions.VisibilityRange)
                .ToArray();

    public void RefreshNpcs()
    {
        if (!InBallpark)
        {
            return;
        }
        var current = ReadNpcs();
        foreach (var departed in _npcs.ExceptBy(current.Select(n => n.Id), n => n.Id))
        {
            if (departed.Weapon is { } weapon)
            {
                NpcWeaponEffect(departed.Id, weapon, false);
            }
            Update(new DestinyCommand.RemoveBall(departed.Id, encounters?.WasDestroyed(departed.Id) == true));
        }
        var added = current.ExceptBy(_npcs.Select(n => n.Id), n => n.Id).ToArray();
        if (added.Length > 0)
        {
            Update(new DestinyCommand.AddBalls(Ballpark.AddNpcs(added, Stamp)));
        }
        bool reconcile = Stamp - _lastNpcSync >= NpcReconciliationTicks;
        foreach (var npc in current)
        {
            var previous = _npcs.FirstOrDefault(n => n.Id == npc.Id);
            if (
                previous is not null
                && (npc.Position != previous.Position || npc.Navigation != previous.Navigation || reconcile && npc.Navigation is not null)
            )
            {
                Update(new DestinyCommand.SetBallPosition(npc.Id, npc.Position.X, npc.Position.Y, npc.Position.Z));
                if (npc.Navigation is { } course)
                {
                    if (course.TargetId != 0)
                    {
                        Update(
                            new DestinyCommand.Follow(
                                course.State == NpcNavigationState.Orbiting ? NavigationMode.Orbit : NavigationMode.Follow,
                                npc.Id,
                                course.TargetId,
                                course.FollowRange
                            )
                        );
                    }
                    else if (course.Destination is { } destination)
                    {
                        Update(new DestinyCommand.GotoPoint(npc.Id, destination.X, destination.Y, destination.Z));
                    }
                    Update(new DestinyCommand.SetSpeedFraction(npc.Id, course.SpeedFraction));
                    Update(new DestinyCommand.SetBallVelocity(npc.Id, course.Velocity.X, course.Velocity.Y, course.Velocity.Z));
                }
                else
                {
                    Update(new DestinyCommand.Stop(npc.Id));
                    Update(new DestinyCommand.SetBallVelocity(npc.Id, 0d, 0d, 0d));
                }
            }
            if (previous?.Weapon != npc.Weapon)
            {
                if (previous?.Weapon is { } stopped)
                {
                    NpcWeaponEffect(npc.Id, stopped, false);
                }
                if (npc.Weapon is { } started)
                {
                    NpcWeaponEffect(npc.Id, started, true);
                }
            }
            if (
                previous is not null
                && (
                    npc.Health.Remaining.Armor != previous.Health.Remaining.Armor
                    || npc.Health.Remaining.Structure != previous.Health.Remaining.Structure
                    || npc.Health.Remaining.Shield < previous.Health.Remaining.Shield
                )
            )
            {
                NotifyHealth(npc.Health);
            }
        }
        if (reconcile)
        {
            _lastNpcSync = Stamp;
        }
        _npcs = current;
    }

    private void NpcWeaponEffect(long npcId, NpcWeaponContact weapon, bool active) =>
        Update(
            new DestinyCommand.TimedSpecialEffect(
                npcId,
                npcId,
                weapon.TurretTypeId,
                weapon.TargetId,
                null,
                "effects.Laser",
                true,
                active,
                active,
                weapon.Duration,
                weapon.StartedAt
            )
        );

    public void NotifyNpcWeapon(long npcId, NpcWeaponContact weapon)
    {
        if (_npcs.FirstOrDefault(n => n.Id == npcId)?.Weapon is { } previous)
        {
            NpcWeaponEffect(npcId, previous, false);
        }
        NpcWeaponEffect(npcId, weapon, true);
        _npcs = _npcs.Select(n => n.Id == npcId ? n with { Weapon = weapon } : n).ToArray();
    }

    public void BeginShipLoss()
    {
        _destroying = true;
        space.Leave(Pilot.ShipId);
    }

    public void ReplaceDestroyedShip(DockedPilot capsule)
    {
        long destroyed = Pilot.ShipId;
        foreach (var npc in _npcs.Where(n => n.Weapon?.TargetId == destroyed))
        {
            NpcWeaponEffect(npc.Id, npc.Weapon!, false);
        }
        _npcs = _npcs.Select(n => n.Weapon?.TargetId == destroyed ? n with { Weapon = null } : n).ToArray();
        Pilot = capsule;
        _baseFlight = capsule.Flight;
        _propulsion = null;
        modules = [];
        _navigation.AdoptHull();
        _mining = [];
        _weapons = [];
        _shieldBoosts = [];
        _destroying = false;
        // The client can adopt an already loaded capsule as ego during the ship
        // session delta. Keep the old hull until the capsule ball has arrived.
        var contact = CreateContact();
        Update(new DestinyCommand.AddBalls(Ballpark.AddShip(contact, Stamp)));
        Update(new DestinyCommand.RemoveBall(destroyed, true));
        space.Publish(contact);
    }

    public void NotifyHealth(ShipHealth current)
    {
        _npcs = _npcs.Select(n => n.Id == current.ShipId ? n with { Health = current } : n).ToArray();
        Update(new DestinyCommand.OnDamageStateChange(current.ShipId, current.DamageState()));
    }

    public void RefreshAsteroids(IReadOnlyList<StaticSpaceObject> asteroids)
    {
        objects = objects.Where(o => o.CategoryId != AsteroidDefinitions.Category).Concat(asteroids).ToArray();
        if (!InBallpark)
        {
            return;
        }
        var current = VisibleObjects().Where(o => o.CategoryId == AsteroidDefinitions.Category).ToArray();
        foreach (var departed in _visibleAsteroids.ExceptBy(current.Select(o => o.Id), o => o.Id))
        {
            Update(new DestinyCommand.RemoveBall(departed.Id, false));
        }
        var added = current.ExceptBy(_visibleAsteroids.Select(o => o.Id), o => o.Id).ToArray();
        if (added.Length > 0)
        {
            Update(new DestinyCommand.AddBalls(Ballpark.AddObjects(added, Stamp)));
        }
        _visibleAsteroids = current;
    }

    public void Checkpoint()
    {
        int now = Stamp;
        var previous = Pilot.Position;
        while (_simulatedStamp < now && Moving && Pilot.Position is { } position)
        {
            _simulatedStamp++;
            Evolve(position);
        }
        _simulatedStamp = now;
        if (Pilot.Position != previous)
        {
            world.SetPosition(Pilot.ShipId, Pilot.Position);
        }
    }

    private void Evolve(SpacePosition position)
    {
        var tick = _navigation.Advance(
            Pilot.ShipId,
            _simulatedStamp,
            Pilot.Flight,
            position,
            NavigationTarget,
            objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup).ToArray()
        );
        Pilot = Pilot with { Position = tick.Position };
        foreach (var change in tick.Changes)
        {
            switch (change)
            {
                case NavigationChange.WarpExited:
                    WarpEffect(Pilot.ShipId, false);
                    break;
                case NavigationChange.Heading heading:
                    Update(new DestinyCommand.GotoDirection(Pilot.ShipId, heading.Direction.X, heading.Direction.Y, heading.Direction.Z));
                    break;
                case NavigationChange.Stopped:
                    Update(new DestinyCommand.Stop(Pilot.ShipId));
                    break;
                case NavigationChange.Collision collision:
                    Update(
                        new DestinyCommand.SetBallPosition(Pilot.ShipId, collision.Position.X, collision.Position.Y, collision.Position.Z)
                    );
                    Update(new DestinyCommand.Stop(Pilot.ShipId));
                    Update(new DestinyCommand.SetBallVelocity(Pilot.ShipId, 0d, 0d, 0d));
                    break;
            }
        }
    }

    private ShipContact CreateContact() =>
        new(
            PilotContact.From(Pilot),
            _navigation.Velocity,
            _navigation.Direction,
            _navigation.SpeedFraction,
            _navigation.FollowTarget is null ? null : _navigation.FollowRange,
            _navigation.Warp,
            _navigation.WarpDestination,
            _navigation.AlignStamp,
            (int)Math.Round(Pilot.Flight.WarpSpeed * 1000),
            _navigation.FollowTarget?.Id ?? 0,
            _jump?.SourceId,
            Stamp - _arrivalStamp < 8,
            modules,
            _mining,
            health?.Read(Pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            _weapons,
            _shieldBoosts,
            _propulsion,
            _navigation.FollowMode
        );

    public void RefreshFitting(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        Checkpoint();
        _baseFlight = _baseFlight with
        {
            MaxSpeed = (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            Mass = attributes.GetValueOrDefault(DogmaAttributeId.Mass),
            Agility = (float)attributes.GetValueOrDefault(DogmaAttributeId.Agility),
        };
        Pilot = Pilot with
        {
            Ship = Pilot.Ship with
            {
                CargoCapacity = attributes.GetValueOrDefault(DogmaAttributeId.CargoCapacity),
                Hull = new(
                    attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints),
                    attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
                    attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
                    attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                    attributes.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints),
                    attributes.GetValueOrDefault(DogmaAttributeId.ShieldRechargeTime)
                ),
            },
        };
        ApplyFlightProperties();
    }

    public void SetBaseSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed <= 0 || speed > float.MaxValue)
        {
            throw new InvalidDataException("Invalid ship navigation speed.");
        }
        Checkpoint();
        _baseFlight = _baseFlight with { MaxSpeed = (float)speed };
        ApplyFlightProperties();
    }

    public void SetPropulsionEffect(PropulsionContact? effect)
    {
        Checkpoint();
        var previous = _propulsion;
        _propulsion = effect;
        ApplyFlightProperties();
        if (previous is not null)
        {
            Effects.PropulsionEffect(Pilot.ShipId, previous, false);
        }
        if (effect is not null)
        {
            Effects.PropulsionEffect(Pilot.ShipId, effect, true);
        }
        if (InBallpark && !_destroying)
        {
            space.Publish(CreateContact());
        }
    }

    private void ApplyFlightProperties()
    {
        var properties = _propulsion?.Module.Apply(_baseFlight) ?? _baseFlight;
        if (Pilot.Flight == properties)
        {
            return;
        }
        Pilot = Pilot with { Flight = properties };
        if (InBallpark)
        {
            Update(new DestinyCommand.SetBallMass(Pilot.ShipId, properties.Mass));
            Update(new DestinyCommand.SetMaxSpeed(Pilot.ShipId, (double)properties.MaxSpeed));
            if (!_destroying)
            {
                space.Publish(CreateContact());
            }
        }
    }

    public void SetMiningEffect(MiningContact effect, bool active)
    {
        _mining = _mining.Where(m => m.ModuleId != effect.ModuleId).Concat(active ? [effect] : Array.Empty<MiningContact>()).ToArray();
        Effects.MiningEffect(Pilot.ShipId, effect, active);
        if (InBallpark && !_destroying)
        {
            space.Publish(CreateContact());
        }
    }

    public void RefreshModules(IReadOnlyList<InventoryItem> items) =>
        modules = items
            .Where(i => InventoryDefinitions.IsHighSlot(i.FlagId))
            .Select(i => new FittedSpaceModule(i.Id, i.TypeId, i.FlagId))
            .ToArray();

    public void SetWeaponEffect(WeaponContact effect, bool active)
    {
        _weapons = _weapons.Where(w => w.ModuleId != effect.ModuleId).Concat(active ? [effect] : Array.Empty<WeaponContact>()).ToArray();
        Effects.WeaponEffect(Pilot.ShipId, effect, active);
        if (InBallpark && !_destroying)
        {
            space.Publish(CreateContact());
        }
    }

    public void SetShieldBoostEffect(ShieldBoostContact effect, bool active)
    {
        _shieldBoosts = _shieldBoosts
            .Where(b => b.ModuleId != effect.ModuleId)
            .Concat(active ? [effect] : Array.Empty<ShieldBoostContact>())
            .ToArray();
        Effects.ShieldBoostEffect(Pilot.ShipId, effect, active);
        if (InBallpark && !_destroying)
        {
            space.Publish(CreateContact());
        }
    }

    public void RefreshPeers()
    {
        var contact = CreateContact();
        space.Publish(contact);
        var current = space.Peers(Pilot);
        if (
            current.Any(ship =>
                ship.Warp is not null
                && Array.Exists(
                    _peers,
                    old => old.Pilot.ShipId == ship.Pilot.ShipId && old.Warp != ship.Warp && old.AlignTo != ship.Warp.Destination
                )
            )
        )
        {
            Snapshot(current, contact);
            return;
        }
        foreach (var departed in _peers.ExceptBy(current.Select(ship => ship.Pilot.ShipId), ship => ship.Pilot.ShipId))
        {
            Update(new DestinyCommand.RemoveBall(departed.Pilot.ShipId, health?.WasDestroyed(departed.Pilot.ShipId) == true));
        }
        // Destiny integrates the same course between changes. Occasionally reconcile drift.
        bool reconcile = Stamp - _lastPeerSync >= NavigationDefinitions.ReconciliationTicks;
        if (
            reconcile
            && _navigation.FollowTarget is not null
            && _navigation.FollowMode == NavigationMode.Orbit
            && Pilot.Position is { } ownPosition
        )
        {
            Update(new DestinyCommand.SetBallPosition(Pilot.ShipId, ownPosition.X, ownPosition.Y, ownPosition.Z));
            Update(
                new DestinyCommand.SetBallVelocity(Pilot.ShipId, _navigation.Velocity.X, _navigation.Velocity.Y, _navigation.Velocity.Z)
            );
        }
        foreach (var ship in current)
        {
            var previous = Array.Find(_peers, peer => peer.Pilot.ShipId == ship.Pilot.ShipId);
            if (previous is null)
            {
                // AddBalls takes a single chunk. Replacing the entire park resets the observer's origin.
                Update(new DestinyCommand.AddBalls(Ballpark.AddShip(ship, Stamp)));
                Effects.Replay(ship, null);
                if (ship.Arriving)
                {
                    ArrivalEffect(ship.Pilot.ShipId);
                }
                // The client plays the arrival flash when it loads a ship already in warp.
                continue;
            }
            Effects.Replay(ship, previous);
            if (
                ship.Health is { } currentHealth
                && previous.Health is { } previousHealth
                && (
                    currentHealth.Remaining.Armor != previousHealth.Remaining.Armor
                    || currentHealth.Remaining.Structure != previousHealth.Remaining.Structure
                    || currentHealth.Remaining.Shield != previousHealth.Remaining.Shield
                )
            )
            {
                NotifyHealth(currentHealth);
            }
            if (ship.DepartureGate is long departure && previous.DepartureGate != departure)
            {
                DepartureEffect(ship.Pilot.ShipId, departure, objects.Single(o => o.Id == departure).DestinationSystemId!.Value);
            }
            if (ship.Warp is not null)
            {
                if (ship.Warp != previous.Warp)
                {
                    WarpEffect(ship.Pilot.ShipId, true);
                }
                continue;
            }
            if (ship.AlignTo is not null)
            {
                if (ship.AlignTo != previous.AlignTo)
                {
                    ApplyCourse(ship);
                }
                continue;
            }
            if (previous.Warp is not null)
            {
                WarpEffect(ship.Pilot.ShipId, false);
            }
            if (ship.Heading is null)
            {
                if (previous.Heading is not null)
                {
                    Update(new DestinyCommand.Stop(ship.Pilot.ShipId));
                }
                continue;
            }
            if (
                (ship.FollowRange is null && ship.Heading != previous.Heading)
                || ship.SpeedFraction != previous.SpeedFraction
                || ship.FollowRange != previous.FollowRange
                || ship.FollowTargetId != previous.FollowTargetId
                || ship.FollowMode != previous.FollowMode
                || reconcile
            )
            {
                var position = ship.Pilot.Position!;
                Update(new DestinyCommand.SetBallPosition(ship.Pilot.ShipId, position.X, position.Y, position.Z));
                ApplyCourse(ship);
            }
        }
        if (reconcile)
        {
            _lastPeerSync = Stamp;
        }
        _peers = current;
    }

    public IEnumerable<PilotContact> VisiblePilots() => space.Peers(Pilot).Select(ship => ship.Pilot).Prepend(PilotContact.From(Pilot));

    private void LeaveSpace()
    {
        _propulsion = null;
        Pilot = Pilot with { Flight = _baseFlight };
        InBallpark = false;
        _peers = [];
        _visibleAsteroids = [];
        _mining = [];
        _weapons = [];
        _npcs = [];
        _shieldBoosts = [];
        _wrecks = [];
        space.Leave(Pilot.ShipId);
    }

    public void Dispose()
    {
        try
        {
            Checkpoint();
        }
        finally
        {
            LeaveSpace();
        }
    }

    private void SetPosition(SpacePosition? position)
    {
        world.SetPosition(Pilot.ShipId, position);
        Pilot = Pilot with { Position = position, CurrentSystem = position is null ? null : Pilot.CurrentSystem };
    }

    public void Snapshot()
    {
        Checkpoint();
        if (!InBallpark && Pilot.Position is { } position)
        {
            // A legacy save or a disconnect during warp can leave the ship inside a rigid body.
            foreach (var body in objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup))
            {
                var radial = position - body.Position;
                double surface = body.Radius + Pilot.Flight.ShipRadius;
                if (radial.Length < surface - 0.01)
                {
                    position = body.Position + (radial.Length < 1 ? new SpacePosition(1, 0, 0) : radial.Unit) * (surface + 1);
                    SetPosition(position);
                }
            }
        }
        bool entering = !InBallpark;
        InBallpark = true;
        bool arriving = entering && _awaitingArrival;
        if (arriving)
        {
            _awaitingArrival = false;
            _arrivalStamp = Stamp;
        }
        var contact = CreateContact();
        space.Publish(contact);
        Snapshot(space.Peers(Pilot), contact);
        if (arriving)
        {
            ArrivalEffect(Pilot.ShipId);
        }
    }

    private void Snapshot(ShipContact[] peers, ShipContact own)
    {
        _peers = peers;
        int stamp = Stamp;
        var visible = VisibleObjects();
        _npcs = ReadNpcs();
        _wrecks = ReadWrecks();
        _visibleAsteroids = visible.Where(o => o.CategoryId == AsteroidDefinitions.Category).ToArray();
        Updates.Add(new(stamp, new DestinyCommand.SetState(Ballpark.Snapshot(own, stamp, visible, peers, _npcs, _wrecks))));
        foreach (var npc in _npcs.Where(n => n.Weapon is not null))
        {
            NpcWeaponEffect(npc.Id, npc.Weapon!, true);
        }
        Effects.Replay(own, null);
        foreach (var peer in peers)
        {
            Effects.Replay(peer, null);
        }
        if (_navigation.Warp is not null || _navigation.WarpDestination is not null)
        {
            WarpEffect(Pilot.ShipId, true);
        }
    }

    private void ApplyCourse(ShipContact ship)
    {
        long id = ship.Pilot.ShipId;
        if (ship.AlignTo is { } end)
        {
            Update(new DestinyCommand.SetSpeedFraction(id, 1d));
            Update(new DestinyCommand.SetBallVelocity(id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z));
            Update(new DestinyCommand.WarpTo(id, end.X, end.Y, end.Z, 0d, ship.WarpFactor));
            return;
        }
        if (ship.Heading is null)
        {
            Update(new DestinyCommand.Stop(id));
        }
        else
        {
            if (ship.FollowRange is double range)
            {
                Update(new DestinyCommand.Follow(ship.FollowMode, id, ship.FollowTargetId, range));
            }
            else
            {
                Update(new DestinyCommand.GotoDirection(id, ship.Heading.X, ship.Heading.Y, ship.Heading.Z));
            }
            Update(new DestinyCommand.SetSpeedFraction(id, ship.SpeedFraction));
        }
        Update(new DestinyCommand.SetBallVelocity(id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z));
    }

    private void WarpEffect(long id, bool start)
    {
        // The simulation command moves the ball. This separate toggle drives the pilot's tunnel and peer flash.
        Update(new DestinyCommand.SpecialEffect(id, null, null, null, null, "effects.Warping", false, start, true));
    }

    private void SetAtRest() => _navigation.SetAtRest();

    private void Update(DestinyCommand command) => Updates.Add(new(_simulatedStamp, command));

    public PilotCallResult StartJump(FlightCommand.Jump request, ISystemObjects systems)
    {
        long sourceId = request.SourceId;
        long exitId = request.ExitId;
        long shipId = request.ShipId;
        Checkpoint();
        if (
            !InBallpark
            || Pilot.Position is not { } position
            || _jump is not null
            || _navigation.Warp is not null
            || _navigation.WarpDestination is not null
            || shipId != Pilot.ShipId
            || objects.FirstOrDefault(o => o.Id == sourceId) is not { DestinationSystemId: int systemId } source
            || source.DestinationGateId != exitId
            || position.DistanceTo(source.Position) > source.Radius + Pilot.Flight.ShipRadius + 2500
        )
        {
            return PilotCallResult.Unsupported;
        }
        var destination = systems.Load(systemId);
        var exit = destination.FirstOrDefault(o =>
            o.Id == exitId && o.DestinationGateId == source.Id && o.DestinationSystemId == Pilot.SystemId
        );
        if (exit is null)
        {
            return PilotCallResult.Unsupported;
        }
        // Keep the source balls alive while the jump camera turns, fades the UI, and enters the tunnel.
        _arrivalStamp = null;
        _jump = new(source.Id, exit, systems.GetSystem(systemId), destination, Stamp + JumpDurationTicks);
        SetAtRest();
        Update(new DestinyCommand.Stop(Pilot.ShipId));
        DepartureEffect(Pilot.ShipId, source.Id, systemId);
        space.Publish(CreateContact());
        return PilotCallResult.None;
    }

    public bool CompleteJump()
    {
        if (_jump is not { } jump || Stamp < jump.TransferStamp)
        {
            return false;
        }
        var lane = Pilot.ShipId % 2 == 0 ? -1 : 1;
        var position = jump.Exit.Position + new SpacePosition(lane * (jump.Exit.Radius + Pilot.Flight.ShipRadius + 12_000), 0, 0);
        world.SetPosition(Pilot.ShipId, position, jump.System.Id);
        LeaveSpace();
        objects = jump.Objects;
        Pilot = Pilot with { Position = position, CurrentSystem = jump.System };
        _jump = null;
        _awaitingArrival = true;
        return true;
    }

    // SubwayService reads graphicInfo[0] to preload the skybox and attach the destination camera.
    private void DepartureEffect(long shipId, long gateId, int systemId) =>
        Update(new DestinyCommand.JumpOut(shipId, gateId, systemId, JumpDurationTicks * 1000));

    // SubwayService completes the gate transition. JumpIn supplies the ship flash.
    private void ArrivalEffect(long shipId) =>
        Update(new DestinyCommand.SpecialEffect(shipId, null, null, null, null, "effects.JumpIn", false, true, false));

    private PilotCallResult StartWarp(long target, double range)
    {
        var body = objects.FirstOrDefault(item => item.Id == target);
        if (body is null)
        {
            return PilotCallResult.Unsupported;
        }
        if (!InBallpark || !_navigation.StartWarp(Pilot.ShipId, Pilot.Flight, Pilot.Position!, body, range, _simulatedStamp))
        {
            return PilotCallResult.Unsupported;
        }
        var contact = CreateContact();
        ApplyCourse(contact);
        // WarpLoop prepares the tunnel while aligning, then observes Destiny's positive
        // effect stamp to trigger the camera and acceleration sequence on the client.
        WarpEffect(Pilot.ShipId, true);
        space.Publish(contact);
        return PilotCallResult.None;
    }

    public PilotCallResult Execute(FlightCommand command)
    {
        if (command is FlightCommand.Undock undock && Pilot.Position is null && undock.ShipId == Pilot.ShipId)
        {
            // Separate the two local launch lanes even on simultaneous undocks.
            double lane = (Pilot.ShipId % 2 == 0) ? 1500 : 0;
            SetPosition(new(Pilot.X + Pilot.Flight.StationRadius + 500, Pilot.Y + lane, Pilot.Z));
            return PilotCallResult.None;
        }
        if (command is FlightCommand.Undock || Pilot.Position is not { } position)
        {
            return PilotCallResult.Unsupported;
        }
        Checkpoint();
        if (_navigation.WarpDestination is not null && command is FlightCommand.Stop)
        {
            _navigation.CancelAlignment();
            WarpEffect(Pilot.ShipId, false);
        }
        if (
            (_jump is not null || _navigation.Warp is not null || _navigation.WarpDestination is not null)
            && command is not FlightCommand.RefreshState
        )
        {
            // The native docking navigator retries its warp order on a timer.
            // Acknowledge the same destination without restarting the active course.
            if (_jump is null && command is FlightCommand.WarpTo retry)
            {
                return _navigation.WarpOrder == (retry.TargetId, retry.Range) ? PilotCallResult.None : PilotCallResult.Unsupported;
            }
            return PilotCallResult.Unsupported;
        }
        switch (command)
        {
            case FlightCommand.WarpTo warp:
                return StartWarp(warp.TargetId, warp.Range);
            case FlightCommand.RefreshState:
                Snapshot();
                return PilotCallResult.None;
            case FlightCommand.Heading(var x, var y, var z):
                if (!_navigation.SetHeading(x, y, z))
                {
                    return PilotCallResult.Unsupported;
                }
                Update(new DestinyCommand.GotoDirection(Pilot.ShipId, x, y, z));
                Update(new DestinyCommand.SetSpeedFraction(Pilot.ShipId, _navigation.SpeedFraction));
                return PilotCallResult.None;
            case FlightCommand.Follow(var target, var standOff, var orbit) when NavigationTarget(target) is { } follow:
                var followed = _navigation.Follow(
                    Pilot.ShipId,
                    _simulatedStamp,
                    Pilot.Position!,
                    Pilot.Flight.ShipRadius,
                    follow,
                    standOff,
                    orbit,
                    InBallpark
                );
                if (followed == NavigationOrderResult.Rejected)
                {
                    return PilotCallResult.Unsupported;
                }
                if (followed == NavigationOrderResult.Applied)
                {
                    Update(new DestinyCommand.Follow(_navigation.FollowMode, Pilot.ShipId, target, standOff));
                    Update(new DestinyCommand.SetSpeedFraction(Pilot.ShipId, 1d));
                }
                return PilotCallResult.None;
            case FlightCommand.Speed(var fraction):
                bool resumeHeading = _navigation.Direction is null && fraction > 0;
                if (!_navigation.SetSpeed(fraction))
                {
                    return PilotCallResult.Unsupported;
                }
                if (resumeHeading)
                {
                    var heading = _navigation.LastHeading;
                    Update(new DestinyCommand.GotoDirection(Pilot.ShipId, heading.X, heading.Y, heading.Z));
                }
                Update(new DestinyCommand.SetSpeedFraction(Pilot.ShipId, fraction));
                return PilotCallResult.None;
            case FlightCommand.Stop:
                _navigation.Stop();
                Update(new DestinyCommand.Stop(Pilot.ShipId));
                return PilotCallResult.None;
            case FlightCommand.Dock(var station, var ship)
                when ship == Pilot.ShipId
                    && station is > 0 and <= int.MaxValue
                    && objects.FirstOrDefault(o => o.Id == station && o.CategoryId == (int)InventoryCategory.Station) is { } destination:
                position = Pilot.Position!;
                if (
                    position.DistanceTo(destination.Position)
                        > destination.Radius + Pilot.Flight.ShipRadius + DockingDefinitions.MaximumRange
                    || (docking is null && (station != Pilot.StationId || Pilot.SystemId != Pilot.HomeSystemId))
                )
                {
                    return PilotCallResult.Unsupported;
                }
                if (docking is not null)
                {
                    Pilot = docking.Dock(Pilot.PilotId, ship, (int)station);
                }
                else
                {
                    SetPosition(null);
                }
                LeaveSpace();
                _navigation.Dock();
                return PilotCallResult.None;
            default:
                return PilotCallResult.Unsupported;
        }
    }
}
