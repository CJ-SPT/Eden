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
    private SpacePosition _lastHeading = new(1, 0, 0);
    private SpacePosition? _direction;
    private SpacePosition _velocity = new(0, 0, 0);
    private StaticSpaceObject? _followTarget;
    private DestinyBallMode _followMode = DestinyBallMode.Follow;

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
    private double _followRange;
    private SpacePosition? _warpDestination;
    private (long Target, double Range)? _warpOrder;
    private int _alignStamp;
    private WarpCourse? _warp;
    private double _speedFraction;
    public DockedPilot Pilot { get; private set; } = pilot;
    public List<object?> Updates { get; } = [];
    public bool Moving => _direction is not null || _warp is not null || _velocity != SpacePosition.Zero;
    public bool CanTarget =>
        InBallpark && !_destroying && Pilot.Position is not null && _jump is null && _warp is null && _warpDestination is null;

    // Pinned shieldBoosting effect 4 is warp-safe. Gate/session transitions and
    // the terminal hull transition still prevent local module activations.
    public bool CanUseSelfModules => InBallpark && !_destroying && Pilot.Position is not null && _jump is null;
    public SpacePosition Velocity => _velocity;
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
            Update("RemoveBall", departed.Id, false);
        }
        var added = current.ExceptBy(_wrecks.Select(w => w.Id), w => w.Id).ToArray();
        if (added.Length > 0)
        {
            Update("AddBalls", [Ballpark.AddWrecks(added, Stamp)]);
        }
        foreach (var wreck in current)
        {
            if (_wrecks.FirstOrDefault(w => w.Id == wreck.Id) is { } previous && previous != wreck)
            {
                Update("OnSlimItemChange", wreck.Id, Ballpark.WreckSlim(wreck));
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
            Update("RemoveBall", departed.Id, encounters?.WasDestroyed(departed.Id) == true);
        }
        var added = current.ExceptBy(_npcs.Select(n => n.Id), n => n.Id).ToArray();
        if (added.Length > 0)
        {
            Update("AddBalls", [Ballpark.AddNpcs(added, Stamp)]);
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
                Update("SetBallPosition", npc.Id, npc.Position.X, npc.Position.Y, npc.Position.Z);
                if (npc.Navigation is { } course)
                {
                    if (course.TargetId != 0)
                    {
                        Update(
                            course.State == NpcNavigationState.Orbiting ? "Orbit" : "FollowBall",
                            npc.Id,
                            course.TargetId,
                            course.FollowRange
                        );
                    }
                    else if (course.Destination is { } destination)
                    {
                        Update("GotoPoint", npc.Id, destination.X, destination.Y, destination.Z);
                    }
                    Update("SetSpeedFraction", npc.Id, course.SpeedFraction);
                    Update("SetBallVelocity", npc.Id, course.Velocity.X, course.Velocity.Y, course.Velocity.Z);
                }
                else
                {
                    Update("Stop", npc.Id);
                    Update("SetBallVelocity", npc.Id, 0d, 0d, 0d);
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
            "OnSpecialFX",
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
            0,
            weapon.StartedAt,
            0,
            null
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
        _velocity = SpacePosition.Zero;
        _direction = null;
        _speedFraction = 0;
        _followTarget = null;
        _warp = null;
        _warpDestination = null;
        _warpOrder = null;
        _mining = [];
        _weapons = [];
        _shieldBoosts = [];
        _destroying = false;
        // The client can adopt an already loaded capsule as ego during the ship
        // session delta. Keep the old hull until the capsule ball has arrived.
        Update("AddBalls", [Ballpark.AddShip(Sample, Stamp)]);
        Update("RemoveBall", destroyed, true);
        space.Publish(Sample);
    }

    public void NotifyHealth(ShipHealth current)
    {
        _npcs = _npcs.Select(n => n.Id == current.ShipId ? n with { Health = current } : n).ToArray();
        Update("OnDamageStateChange", current.ShipId, current.DamageState());
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
            Update("RemoveBall", departed.Id, false);
        }
        var added = current.ExceptBy(_visibleAsteroids.Select(o => o.Id), o => o.Id).ToArray();
        if (added.Length > 0)
        {
            Update("AddBalls", [Ballpark.AddObjects(added, Stamp)]);
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
        if (_warpDestination is { } destination)
        {
            _direction = (destination - position).Unit;
            double speed = _velocity.Length;
            if ((speed > Pilot.Flight.MaxSpeed * 0.75 && _velocity.Unit.Dot(_direction) > 0.99) || _simulatedStamp - _alignStamp > 180)
            {
                _warp = new(position, destination, _simulatedStamp, Pilot.Flight, speed);
                _warpDestination = null;
                _direction = null;
            }
        }
        bool justExited = false;
        if (_warp is { } warp)
        {
            var sample = warp.Sample(_simulatedStamp, Math.Min(Pilot.Flight.MaxSpeed / 2, 100));
            position = sample.Position;
            Pilot = Pilot with { Position = position };
            _velocity = sample.Velocity;
            if (!sample.Exited)
            {
                return;
            }
            _warp = null;
            _speedFraction = 0;
            justExited = true;
            WarpEffect(Pilot.ShipId, false);
        }

        double cruise = Pilot.Flight.MaxSpeed * _speedFraction;
        if (_followTarget is { } follow)
        {
            if (NavigationTarget(follow.Id) is not { } current)
            {
                // Carbon's interactive orbiters keep their heading when a target
                // leaves. Keep-at-range followers instead coast to a stop.
                _followTarget = null;
                if (_followMode == DestinyBallMode.Orbit && _velocity.Length > 0)
                {
                    _direction = _lastHeading = _velocity.Unit;
                    Update("GotoDirection", Pilot.ShipId, _direction.X, _direction.Y, _direction.Z);
                }
                else
                {
                    _direction = null;
                    _speedFraction = cruise = 0;
                    Update("Stop", Pilot.ShipId);
                }
            }
            else
            {
                _followTarget = current;
                if (_followMode == DestinyBallMode.Orbit)
                {
                    // Destiny evolves behavior at the beginning of each tick.
                    _direction = OrbitCourse.Direction(
                        Pilot.ShipId,
                        _simulatedStamp - 1,
                        position,
                        current.Position,
                        Pilot.Flight.ShipRadius + current.Radius + _followRange
                    );
                }
                else
                {
                    var radial = position - current.Position;
                    double error = radial.Length - current.Radius - Pilot.Flight.ShipRadius - _followRange;
                    _direction = radial.Unit * -Math.Sign(error);
                    if (cruise > 0 && Math.Abs(error) < cruise)
                    {
                        // Destiny GotoThrust tapers within one tick's travel distance.
                        cruise *= Math.Pow(error / cruise, 4);
                    }
                }
            }
        }
        // Build 3662168 accepts the legacy free-ball layout (no orientation fields).
        // EvolveStop damps Y before drag on each STOP tick, except the warp-exit integration.
        if (_direction is null && !justExited)
        {
            _velocity = _velocity with { Y = _velocity.Y * (0.93 / 1.07) };
        }
        double tau = Pilot.Flight.InertiaTime;
        double decay = Math.Exp(-1 / tau);
        var targetVelocity = (_direction ?? SpacePosition.Zero) * cruise;
        var next = position + (targetVelocity + (_velocity - targetVelocity) * (tau * (1 - decay)));
        _velocity = targetVelocity + (_velocity - targetVelocity) * decay;
        if (FirstContact(position, next) is { } contact)
        {
            next = contact;
            _velocity = SpacePosition.Zero;
            if (_warpDestination is null)
            {
                Update("SetBallPosition", Pilot.ShipId, contact.X, contact.Y, contact.Z);
                Update("Stop", Pilot.ShipId);
                Update("SetBallVelocity", Pilot.ShipId, 0d, 0d, 0d);
                SetAtRest();
            }
        }
        else if ((_direction is null || _speedFraction == 0) && _velocity.Length < 0.01)
        {
            SetAtRest();
        }
        Pilot = Pilot with { Position = next };
    }

    private ShipContact Sample =>
        new(
            Pilot,
            _velocity,
            _direction,
            _speedFraction,
            _followTarget is null ? null : _followRange,
            _warp,
            _warpDestination,
            _alignStamp,
            (int)Math.Round(Pilot.Flight.WarpSpeed * 1000),
            _followTarget?.Id ?? 0,
            _jump?.SourceId,
            Stamp - _arrivalStamp < 8,
            modules,
            _mining,
            health?.Read(Pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            _weapons,
            _shieldBoosts,
            _propulsion,
            _followMode
        );

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
            PropulsionEffect(Pilot.ShipId, previous, false);
        }
        if (effect is not null)
        {
            PropulsionEffect(Pilot.ShipId, effect, true);
        }
        if (InBallpark && !_destroying)
        {
            space.Publish(Sample);
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
            Update("SetBallMass", Pilot.ShipId, properties.Mass);
            Update("SetMaxSpeed", Pilot.ShipId, (double)properties.MaxSpeed);
            if (!_destroying)
            {
                space.Publish(Sample);
            }
        }
    }

    private void PropulsionEffect(long shipId, PropulsionContact effect, bool active) =>
        Update(
            "OnSpecialFX",
            shipId,
            effect.Module.ItemId,
            effect.Module.TypeId,
            shipId,
            null,
            PropulsionDefinitions.EffectGraphic,
            false,
            active,
            active,
            effect.Module.DurationMilliseconds,
            0,
            effect.StartedAt,
            0,
            null
        );

    private void RefreshPropulsion(ShipContact ship, ShipContact? previous)
    {
        long id = ship.Pilot.ShipId;
        if (previous is not null && previous.Pilot.Flight != ship.Pilot.Flight)
        {
            Update("SetBallMass", id, ship.Pilot.Flight.Mass);
            Update("SetMaxSpeed", id, (double)ship.Pilot.Flight.MaxSpeed);
        }
        if (previous?.Propulsion == ship.Propulsion)
        {
            return;
        }
        if (previous?.Propulsion is { } stopped)
        {
            PropulsionEffect(id, stopped, false);
        }
        if (ship.Propulsion is { } started)
        {
            PropulsionEffect(id, started, true);
        }
    }

    public void SetMiningEffect(MiningContact effect, bool active)
    {
        _mining = _mining.Where(m => m.ModuleId != effect.ModuleId).Concat(active ? [effect] : Array.Empty<MiningContact>()).ToArray();
        MiningEffect(Pilot.ShipId, effect, active);
        if (InBallpark && !_destroying)
        {
            space.Publish(Sample);
        }
    }

    public void RefreshModules(IReadOnlyList<InventoryItem> items) =>
        modules = items
            .Where(i => InventoryDefinitions.IsHighSlot(i.FlagId))
            .Select(i => new FittedSpaceModule(i.Id, i.TypeId, i.FlagId))
            .ToArray();

    private void MiningEffect(long shipId, MiningContact effect, bool active) =>
        Update(
            "OnSpecialFX",
            shipId,
            effect.ModuleId,
            effect.TypeId,
            effect.Target,
            null,
            "effects.Laser",
            false,
            active,
            active,
            effect.Duration,
            0,
            effect.StartedAt,
            0,
            null
        );

    private void RefreshMining(ShipContact ship, ShipContact? previous)
    {
        foreach (var effect in (previous?.Mining ?? []).Except(ship.Mining ?? []))
        {
            MiningEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.Mining ?? []).Except(previous?.Mining ?? []))
        {
            MiningEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    public void SetWeaponEffect(WeaponContact effect, bool active)
    {
        _weapons = _weapons.Where(w => w.ModuleId != effect.ModuleId).Concat(active ? [effect] : Array.Empty<WeaponContact>()).ToArray();
        WeaponEffect(Pilot.ShipId, effect, active);
        if (InBallpark && !_destroying)
        {
            space.Publish(Sample);
        }
    }

    private void WeaponEffect(long shipId, WeaponContact effect, bool active) =>
        Update(
            "OnSpecialFX",
            shipId,
            effect.ModuleId,
            effect.TypeId,
            effect.Target,
            effect.ChargeTypeId,
            effect.Graphic,
            true,
            active,
            active,
            effect.Duration,
            0,
            effect.StartedAt,
            0,
            null
        );

    private void RefreshWeapons(ShipContact ship, ShipContact? previous)
    {
        foreach (var effect in (previous?.Weapons ?? []).Except(ship.Weapons ?? []))
        {
            WeaponEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.Weapons ?? []).Except(previous?.Weapons ?? []))
        {
            WeaponEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    public void SetShieldBoostEffect(ShieldBoostContact effect, bool active)
    {
        _shieldBoosts = _shieldBoosts
            .Where(b => b.ModuleId != effect.ModuleId)
            .Concat(active ? [effect] : Array.Empty<ShieldBoostContact>())
            .ToArray();
        ShieldBoostEffect(Pilot.ShipId, effect, active);
        if (InBallpark && !_destroying)
        {
            space.Publish(Sample);
        }
    }

    private void ShieldBoostEffect(long shipId, ShieldBoostContact effect, bool active) =>
        Update(
            "OnSpecialFX",
            shipId,
            effect.ModuleId,
            effect.TypeId,
            shipId,
            null,
            ShieldBoostDefinitions.EffectGraphic,
            false,
            active,
            active,
            effect.Duration,
            0,
            effect.StartedAt,
            0,
            null
        );

    private void RefreshShieldBoosts(ShipContact ship, ShipContact? previous)
    {
        foreach (var effect in (previous?.ShieldBoosts ?? []).Except(ship.ShieldBoosts ?? []))
        {
            ShieldBoostEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.ShieldBoosts ?? []).Except(previous?.ShieldBoosts ?? []))
        {
            ShieldBoostEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    private SpacePosition? FirstContact(SpacePosition start, SpacePosition end)
    {
        var delta = end - start;
        double lengthSquared = delta.Dot(delta);
        double earliest = double.PositiveInfinity;
        foreach (var body in objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup))
        {
            var radial = start - body.Position;
            double along = radial.Dot(delta);
            if (along >= 0)
            {
                continue;
            }
            double radius = body.Radius + Pilot.Flight.ShipRadius;
            double clearance = Math.Max(0, radial.Dot(radial) - radius * radius);
            double discriminant = along * along - lengthSquared * clearance;
            if (discriminant > 0)
            {
                earliest = Math.Min(earliest, clearance / (-along + Math.Sqrt(discriminant)));
            }
        }
        return earliest <= 1 ? start + delta * earliest : null;
    }

    public void RefreshPeers()
    {
        space.Publish(Sample);
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
            Snapshot(current);
            return;
        }
        foreach (var departed in _peers.ExceptBy(current.Select(ship => ship.Pilot.ShipId), ship => ship.Pilot.ShipId))
        {
            Update("RemoveBall", departed.Pilot.ShipId, health?.WasDestroyed(departed.Pilot.ShipId) == true);
        }
        // Destiny integrates the same course between changes. Occasionally reconcile drift.
        bool reconcile = Stamp - _lastPeerSync >= NavigationDefinitions.ReconciliationTicks;
        if (reconcile && _followTarget is not null && _followMode == DestinyBallMode.Orbit && Pilot.Position is { } ownPosition)
        {
            Update("SetBallPosition", Pilot.ShipId, ownPosition.X, ownPosition.Y, ownPosition.Z);
            Update("SetBallVelocity", Pilot.ShipId, _velocity.X, _velocity.Y, _velocity.Z);
        }
        foreach (var ship in current)
        {
            var previous = Array.Find(_peers, peer => peer.Pilot.ShipId == ship.Pilot.ShipId);
            if (previous is null)
            {
                // AddBalls takes a single chunk. Replacing the entire park resets the observer's origin.
                Update("AddBalls", [Ballpark.AddShip(ship, Stamp)]);
                RefreshMining(ship, null);
                RefreshWeapons(ship, null);
                RefreshShieldBoosts(ship, null);
                RefreshPropulsion(ship, null);
                if (ship.Arriving)
                {
                    ArrivalEffect(ship.Pilot.ShipId);
                }
                // The client plays the arrival flash when it loads a ship already in warp.
                continue;
            }
            RefreshMining(ship, previous);
            RefreshWeapons(ship, previous);
            RefreshShieldBoosts(ship, previous);
            RefreshPropulsion(ship, previous);
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
                    Update("Stop", ship.Pilot.ShipId);
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
                Update("SetBallPosition", ship.Pilot.ShipId, position.X, position.Y, position.Z);
                ApplyCourse(ship);
            }
        }
        if (reconcile)
        {
            _lastPeerSync = Stamp;
        }
        _peers = current;
    }

    public IEnumerable<DockedPilot> VisiblePilots() => space.Peers(Pilot).Select(ship => ship.Pilot).Prepend(Pilot);

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

    private double DistanceToStation(SpacePosition position)
    {
        double dx = position.X - Pilot.X,
            dy = position.Y - Pilot.Y,
            dz = position.Z - Pilot.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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
        space.Publish(Sample);
        Snapshot(space.Peers(Pilot));
        if (arriving)
        {
            ArrivalEffect(Pilot.ShipId);
        }
    }

    private void Snapshot(ShipContact[] peers)
    {
        _peers = peers;
        int stamp = Stamp;
        var visible = VisibleObjects();
        _npcs = ReadNpcs();
        _wrecks = ReadWrecks();
        _visibleAsteroids = visible.Where(o => o.CategoryId == AsteroidDefinitions.Category).ToArray();
        Updates.Add(
            new object?[]
            {
                stamp,
                new object?[] { "SetState", new object?[] { Ballpark.Snapshot(Sample, stamp, visible, peers, _npcs, _wrecks) } },
            }
        );
        foreach (var npc in _npcs.Where(n => n.Weapon is not null))
        {
            NpcWeaponEffect(npc.Id, npc.Weapon!, true);
        }
        foreach (var effect in _mining)
        {
            MiningEffect(Pilot.ShipId, effect, true);
        }
        foreach (var effect in _weapons)
        {
            WeaponEffect(Pilot.ShipId, effect, true);
        }
        foreach (var effect in _shieldBoosts)
        {
            ShieldBoostEffect(Pilot.ShipId, effect, true);
        }
        if (_propulsion is { } propulsion)
        {
            PropulsionEffect(Pilot.ShipId, propulsion, true);
        }
        foreach (var peer in peers)
        {
            RefreshMining(peer, null);
            RefreshWeapons(peer, null);
            RefreshShieldBoosts(peer, null);
            RefreshPropulsion(peer, null);
        }
        if (_warp is not null || _warpDestination is not null)
        {
            WarpEffect(Pilot.ShipId, true);
        }
    }

    private void ApplyCourse(ShipContact ship)
    {
        long id = ship.Pilot.ShipId;
        if (ship.AlignTo is { } end)
        {
            Update("SetSpeedFraction", id, 1d);
            Update("SetBallVelocity", id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z);
            Update("WarpTo", id, end.X, end.Y, end.Z, 0d, ship.WarpFactor);
            return;
        }
        if (ship.Heading is null)
        {
            Update("Stop", id);
        }
        else
        {
            if (ship.FollowRange is double range)
            {
                Update(ship.FollowMode == DestinyBallMode.Orbit ? "Orbit" : "FollowBall", id, ship.FollowTargetId, range);
            }
            else
            {
                Update("GotoDirection", id, ship.Heading.X, ship.Heading.Y, ship.Heading.Z);
            }
            Update("SetSpeedFraction", id, ship.SpeedFraction);
        }
        Update("SetBallVelocity", id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z);
    }

    private void WarpEffect(long id, bool start)
    {
        // The simulation command moves the ball. This separate toggle drives the pilot's tunnel and peer flash.
        Update("OnSpecialFX", id, null, null, null, null, "effects.Warping", false, start, true);
    }

    private void SetAtRest()
    {
        _direction = null;
        _followTarget = null;
        _velocity = new(0, 0, 0);
        _speedFraction = 0;
    }

    private void Update(string method, params object?[] args) =>
        Updates.Add(new object?[] { _simulatedStamp, new object?[] { method, args } });

    public object? StartJump(object?[] args, ISystemObjects systems)
    {
        Checkpoint();
        if (
            !InBallpark
            || Pilot.Position is not { } position
            || _jump is not null
            || _warp is not null
            || _warpDestination is not null
            || args is not [long sourceId, long exitId, long shipId]
            || shipId != Pilot.ShipId
            || objects.FirstOrDefault(o => o.Id == sourceId) is not { DestinationSystemId: int systemId } source
            || source.DestinationGateId != exitId
            || position.DistanceTo(source.Position) > source.Radius + Pilot.Flight.ShipRadius + 2500
        )
        {
            return PilotCallRouter.Unsupported;
        }
        var destination = systems.Load(systemId);
        var exit = destination.FirstOrDefault(o =>
            o.Id == exitId && o.DestinationGateId == source.Id && o.DestinationSystemId == Pilot.SystemId
        );
        if (exit is null)
        {
            return PilotCallRouter.Unsupported;
        }
        // Keep the source balls alive while the jump camera turns, fades the UI, and enters the tunnel.
        _arrivalStamp = null;
        _jump = new(source.Id, exit, systems.GetSystem(systemId), destination, Stamp + JumpDurationTicks);
        SetAtRest();
        Update("Stop", Pilot.ShipId);
        DepartureEffect(Pilot.ShipId, source.Id, systemId);
        space.Publish(Sample);
        return null;
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
        Update(
            "OnSpecialFX",
            shipId,
            null,
            null,
            gateId,
            null,
            "effects.JumpOut",
            false,
            true,
            false,
            JumpDurationTicks * 1000,
            0,
            null,
            0,
            new object?[] { systemId }
        );

    // SubwayService completes the gate transition. JumpIn supplies the ship flash.
    private void ArrivalEffect(long shipId) => Update("OnSpecialFX", shipId, null, null, null, null, "effects.JumpIn", false, true, false);

    private object? StartWarp(long target, double range)
    {
        var body = objects.FirstOrDefault(item => item.Id == target);
        if (body is null)
        {
            return PilotCallRouter.Unsupported;
        }
        if (!InBallpark || !double.IsFinite(range) || range is < 0 or > 100_000)
        {
            return PilotCallRouter.Unsupported;
        }
        var origin = Pilot.Position!;
        var anchor = CelestialWarpPoint.Resolve(body, origin);
        if (origin.DistanceTo(anchor) - Pilot.Flight.ShipRadius < 150_000)
        {
            return PilotCallRouter.Unsupported;
        }
        var toward = (anchor - origin).Unit;
        double standOff = range + Pilot.Flight.ShipRadius + (Pilot.ShipId % 2 == 0 ? 1500 : 500);
        var end = anchor - toward * standOff;
        if (origin.DistanceTo(end) < 101_000)
        {
            return PilotCallRouter.Unsupported;
        }
        _warpDestination = end;
        _warpOrder = (target, range);
        _alignStamp = _simulatedStamp;
        _direction = toward;
        _lastHeading = _direction;
        _speedFraction = 1;
        _followTarget = null;
        ApplyCourse(Sample);
        // WarpLoop prepares the tunnel while aligning, then observes Destiny's positive
        // effect stamp to trigger the camera and acceleration sequence on the client.
        WarpEffect(Pilot.ShipId, true);
        space.Publish(Sample);
        return null;
    }

    public object? Call(string service, string method, object?[] arguments, Dictionary<string, object?>? keywords = null)
    {
        if (service == "ship" && method == "Undock" && Pilot.Position is null && arguments is [long id, ..] && id == Pilot.ShipId)
        {
            // Separate the two local launch lanes even on simultaneous undocks.
            double lane = (Pilot.ShipId % 2 == 0) ? 1500 : 0;
            SetPosition(new(Pilot.X + Pilot.Flight.StationRadius + 500, Pilot.Y + lane, Pilot.Z));
            return null;
        }
        if (service != "beyonce" || Pilot.Position is not { } position)
        {
            return PilotCallRouter.Unsupported;
        }
        Checkpoint();
        if (_warpDestination is not null && method == "CmdStop")
        {
            _warpDestination = null;
            WarpEffect(Pilot.ShipId, false);
        }
        if ((_jump is not null || _warp is not null || _warpDestination is not null) && method != "UpdateStateRequest")
        {
            // The native docking navigator retries its warp order on a timer.
            // Acknowledge the same destination without restarting the active course.
            if (
                _jump is null
                && method == "CmdWarpToStuff"
                && arguments is [string or byte[], long retryTarget]
                && BlueMarshal.Text(arguments[0]) == "item"
                && (keywords?.GetValueOrDefault("minRange") ?? 0L) is double or long
            )
            {
                return _warpOrder == (retryTarget, Convert.ToDouble(keywords?.GetValueOrDefault("minRange") ?? 0L))
                    ? null
                    : PilotCallRouter.Unsupported;
            }
            return PilotCallRouter.Unsupported;
        }
        switch (method)
        {
            case "CmdWarpToStuff" when arguments is [string or byte[], long targetId] && BlueMarshal.Text(arguments[0]) == "item":
                var range = keywords?.GetValueOrDefault("minRange") ?? 0L;
                return range is double or long ? StartWarp(targetId, Convert.ToDouble(range)) : PilotCallRouter.Unsupported;
            case "UpdateStateRequest":
                Snapshot();
                return null;
            case "CmdGotoDirection" when arguments is [double x, double y, double z]:
                double length = Math.Sqrt(x * x + y * y + z * z);
                if (!(length > 0) || !double.IsFinite(length))
                {
                    return PilotCallRouter.Unsupported;
                }
                _followTarget = null;
                _lastHeading = new(x / length, y / length, z / length);
                _direction = _lastHeading;
                if (_speedFraction == 0)
                {
                    _speedFraction = 1;
                }
                Update("GotoDirection", Pilot.ShipId, x, y, z);
                Update("SetSpeedFraction", Pilot.ShipId, _speedFraction);
                return null;
            case "CmdFollowBall"
            or "CmdOrbit"
                when arguments is [long target, var distance] && NavigationTarget(target) is { } follow && distance is double or long:
                double standOff = Convert.ToDouble(distance);
                if (
                    standOff < 0
                    || standOff > NavigationDefinitions.MaximumFollowRange
                    || !double.IsFinite(standOff)
                    || method == "CmdOrbit" && !InBallpark
                )
                {
                    return PilotCallRouter.Unsupported;
                }
                position = Pilot.Position!;
                double separation = position.DistanceTo(follow.Position);
                if (separation > 0 || method == "CmdOrbit")
                {
                    double radius = follow.Radius + Pilot.Flight.ShipRadius + standOff;
                    double sign = separation >= radius ? 1 : -1;
                    _followMode = method == "CmdOrbit" ? DestinyBallMode.Orbit : DestinyBallMode.Follow;
                    _direction =
                        _followMode == DestinyBallMode.Orbit
                            ? OrbitCourse.Direction(Pilot.ShipId, _simulatedStamp, position, follow.Position, radius)
                            : (follow.Position - position) * (sign / separation);
                    _lastHeading = _direction;
                    _followTarget = follow;
                    _followRange = standOff;
                    _speedFraction = 1;
                    Update(_followMode == DestinyBallMode.Orbit ? "Orbit" : "FollowBall", Pilot.ShipId, target, standOff);
                    Update("SetSpeedFraction", Pilot.ShipId, 1d);
                }
                return null;
            case "CmdSetSpeedFraction" when arguments is [var speed] && speed is double or long:
                double fraction = Convert.ToDouble(speed);
                if (!(fraction >= 0 && fraction <= 1))
                {
                    return PilotCallRouter.Unsupported;
                }
                if (_direction is null && fraction > 0)
                {
                    _direction = _lastHeading;
                    _followTarget = null;
                    Update("GotoDirection", Pilot.ShipId, _lastHeading.X, _lastHeading.Y, _lastHeading.Z);
                }
                _speedFraction = fraction;
                Update("SetSpeedFraction", Pilot.ShipId, fraction);
                return null;
            case "CmdStop":
                _direction = null;
                _followTarget = null;
                _speedFraction = 0;
                Update("Stop", Pilot.ShipId);
                return null;
            case "CmdDock"
                when arguments is [long station, long ship]
                    && ship == Pilot.ShipId
                    && station is > 0 and <= int.MaxValue
                    && objects.FirstOrDefault(o => o.Id == station && o.CategoryId == (int)InventoryCategory.Station) is { } destination:
                position = Pilot.Position!;
                if (
                    position.DistanceTo(destination.Position)
                        > destination.Radius + Pilot.Flight.ShipRadius + DockingDefinitions.MaximumRange
                    || (docking is null && (station != Pilot.StationId || Pilot.SystemId != Pilot.HomeSystemId))
                )
                {
                    return PilotCallRouter.Unsupported;
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
                _direction = null;
                _velocity = new(0, 0, 0);
                _followTarget = null;
                _speedFraction = 1;
                _lastHeading = new(1, 0, 0);
                return null;
            default:
                return PilotCallRouter.Unsupported;
        }
    }
}
