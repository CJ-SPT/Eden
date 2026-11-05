using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
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
    IWrecks? wrecks = null,
    IPilotMissiles? missiles = null,
    IPilotTackle? tackle = null,
    IPilotShipAppearance? appearance = null
) : IDisposable
{
    internal const int ArrivalDurationTicks = 8;
    private FlightEffects? _effects;
    private FlightEffects Effects => _effects ??= new(Update);
    private NavigationState _navigation = new();
    private ShipContact[] _peers = [];
    private ShipDirtState? _dirt;

    private long? DirtTimestamp()
    {
        if (appearance is not null && _dirt?.ShipId != Pilot.ShipId)
        {
            _dirt = appearance.Read(Pilot.PilotId, Pilot.ShipId);
        }
        return _dirt?.Timestamp;
    }

    public void UpdateCorporation(CorporationIdentity identity)
    {
        Pilot = Pilot with
        {
            Identity = Pilot.Identity with
            {
                CorporationId = identity.Id,
                CorporationTicker = identity.Ticker,
                CorporationRoles = identity.Roles,
            },
        };
        if (InBallpark && !_destroying)
        {
            var contact = CreateContact();
            Update(new DestinyCommand.OnSlimItemChange(Pilot.ShipId, new ShipSlimItem(contact)));
            space.Publish(contact);
        }
    }

    public void RefreshDirtTimestamp(long shipId, long timestamp)
    {
        if (Pilot.ShipId != shipId)
        {
            return;
        }
        _dirt = new(shipId, timestamp);
        if (InBallpark && !_destroying)
        {
            var contact = CreateContact();
            Update(new DestinyCommand.OnSlimItemChange(shipId, new ShipSlimItem(contact)));
            space.Publish(contact);
        }
    }

    private readonly Dictionary<long, long> _peerOrderSequences = new();
    private StaticSpaceObject[] _visibleAsteroids = [];
    private MiningContact[] _mining = [];
    private WeaponContact[] _weapons = [];
    private ShieldBoostContact[] _shieldBoosts = [];
    private LocalRepairContact[] _localRepairs = [];
    private HardenerContact[] _hardeners = [];
    private SupportContact[] _support = [];
    private TackleContact[] _tackleEffects = [];
    private TackleEffect[] _incomingTackle = [];
    private bool _warpBlocked;
    private bool _mwdBlocked;
    private PropulsionContact? _propulsion;
    private FlightProperties _baseFlight = pilot.Flight;
    private NpcContact[] _npcs = [];
    private WreckContact[] _wrecks = [];
    private MissileContact[] _missiles = [];
    private bool _destroying;
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

    // The pinned cyno camera has a one-second look phase. Keep the source scene
    // alive for three ticks so its native camera/controller handoff can begin.
    internal const int TranslocationDurationTicks = 3;

    private sealed record TranslocationTransit(
        IPilotTranslocation Travel,
        TranslocationTarget Target,
        IReadOnlyList<StaticSpaceObject> Objects,
        int TransferStamp,
        bool AwaitingSourceState = false
    );

    private TranslocationTransit? _translocation;
    private bool _translocationArrival;
    public bool Translocating => _translocation is not null;
    private bool _awaitingArrival;
    private int? _arrivalStamp;
    public DockedPilot Pilot { get; private set; } = pilot;
    public List<StampedDestinyUpdate> Updates { get; } = [];
    public bool Moving => _navigation.Direction is not null || _navigation.Warp is not null || _navigation.Velocity != SpacePosition.Zero;
    public bool CanTarget =>
        InBallpark && !_destroying && Pilot.Position is not null && _jump is null && !Translocating && _navigation.Warp is null;

    public bool InWarp => _navigation.Warp is not null;
    public bool MicrowarpdriveBlocked => _mwdBlocked;

    // Pinned shieldBoosting effect 4 is warp-safe. Gate/session transitions and
    // the terminal hull transition still prevent local module activations.
    public bool CanUseSelfModules => InBallpark && !_destroying && Pilot.Position is not null && _jump is null && !Translocating;
    public SpacePosition Velocity => _navigation.Velocity;
    public float BaseSpeed => _baseFlight.MaxSpeed;

    public TargetContact? FindTarget(long id)
    {
        if (!CanTarget)
        {
            return null;
        }
        if (space.Peers(Pilot).FirstOrDefault(s => s.Pilot.ShipId == id && s.Warp is null && s.DepartureGate is null) is { } ship)
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
    public IReadOnlyList<MissileContact> VisibleMissiles => _missiles;

    private MissileContact[] ReadMissiles() =>
        Pilot.Position is not { } position
            ? []
            : (missiles?.List(Pilot.SystemId) ?? [])
                .Where(m => position.DistanceTo(m.Flight.Position) <= NpcEncounterDefinitions.VisibilityRange)
                .ToArray();

    public void RefreshMissiles()
    {
        if (!InBallpark)
        {
            return;
        }
        var current = ReadMissiles();
        foreach (var departed in _missiles.ExceptBy(current.Select(m => m.Id), m => m.Id))
        {
            Update(new DestinyCommand.RemoveBall(departed.Id, missiles?.Impacted(departed.Id) == true));
        }
        var added = current.ExceptBy(_missiles.Select(m => m.Id), m => m.Id).ToArray();
        if (added.Length > 0)
        {
            Update(new DestinyCommand.AddBalls(Ballpark.AddMissiles(added, Stamp)));
        }
        foreach (var missile in current)
        {
            var previous = _missiles.FirstOrDefault(m => m.Id == missile.Id);
            if (previous is not null && previous.Flight.ElapsedSeconds != missile.Flight.ElapsedSeconds)
            {
                var position = missile.Flight.Position;
                var velocity = missile.Flight.Velocity;
                Update(new DestinyCommand.SetBallPosition(missile.Id, position.X, position.Y, position.Z));
                Update(new DestinyCommand.SetBallVelocity(missile.Id, velocity.X, velocity.Y, velocity.Z));
            }
            if (previous is not null && missile.Flight.Orphaned && !previous.Flight.Orphaned)
            {
                var point = missile.Flight.Goto;
                Update(new DestinyCommand.GotoPoint(missile.Id, point.X, point.Y, point.Z));
            }
        }
        _missiles = current;
    }

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
        using var phase = Eden.Protocol.PilotSessions.SessionPerformance.Phase("contact.npcs");
        if (!InBallpark)
        {
            return;
        }
        var current = ReadNpcs();
        var previousById = _npcs.ToDictionary(n => n.Id);
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
        foreach (var npc in current)
        {
            var previous = previousById.GetValueOrDefault(npc.Id);
            if (previous is not null && previous.MaxSpeed != npc.MaxSpeed)
            {
                Update(new DestinyCommand.SetMaxSpeed(npc.Id, (double)npc.MaxSpeed));
            }
            if (previous is not null && npc.Stamp - previous.Stamp > SharedSpace.MovementHistoryTicks)
            {
                CorrectPosition(
                    npc.Id,
                    npc.Position,
                    npc.Navigation?.Velocity ?? SpacePosition.Zero,
                    npc.Stamp == 0 ? _simulatedStamp : npc.Stamp
                );
            }
            if (previous is not null && NpcCourseChanged(previous.Navigation, npc.Navigation))
            {
                ApplyNpcCourse(npc);
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
        _npcs = current;
    }

    private void ApplyNpcCourse(NpcContact npc)
    {
        int stamp = npc.Stamp == 0 ? _simulatedStamp : npc.Stamp;
        void Send(DestinyCommand command) => Updates.Add(new(stamp, command));
        var course = npc.Navigation;
        if (course is null || course.State == NpcNavigationState.Idle)
        {
            Send(new DestinyCommand.Stop(npc.Id));
        }
        else if (course.TargetId != 0)
        {
            Send(
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
            Send(new DestinyCommand.GotoPoint(npc.Id, destination.X, destination.Y, destination.Z));
        }
        Send(new DestinyCommand.SetSpeedFraction(npc.Id, course?.SpeedFraction ?? 0));
    }

    private static bool NpcCourseChanged(NpcNavigationContact? previous, NpcNavigationContact? current) =>
        previous?.State != current?.State
        || previous?.TargetId != current?.TargetId
        || previous?.FollowRange != current?.FollowRange
        || previous?.SpeedFraction != current?.SpeedFraction
        || previous?.Destination != current?.Destination;

    private void NpcWeaponEffect(long npcId, NpcWeaponContact weapon, bool active)
    {
        if (weapon.TurretTypeId <= 0)
        {
            return;
        }
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
    }

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
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        tackle?.Cancel(Pilot.PilotId, Pilot.ShipId);
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
        _missiles = [];
        _mining = [];
        _weapons = [];
        _shieldBoosts = [];
        _localRepairs = [];
        _hardeners = [];
        _support = [];
        _tackleEffects = [];
        _incomingTackle = [];
        _warpBlocked = false;
        _mwdBlocked = false;
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
        RefreshVisibleAsteroids();
    }

    private void RefreshVisibleAsteroids()
    {
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

    private ShipMotion Motion() =>
        new(
            Pilot.ShipId,
            Pilot.SystemId,
            _simulatedStamp,
            Pilot.Position!,
            Pilot.Flight,
            _navigation,
            _jump is null && !_destroying,
            _navigation.FollowTarget is { } target ? NavigationTarget(target.Id) : null
        );

    private void PublishMotion()
    {
        if (Pilot.Position is not null && world is ISystemMotionStore)
        {
            space.PublishMotion(Motion());
        }
    }

    public void Checkpoint()
    {
        lock (space.MotionLock)
        {
            RefreshTackle();
            int now = Stamp;
            if (Pilot.Position is not null && world is ISystemMotionStore motionStore)
            {
                var history = space.Advance(Motion(), now, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), motionStore, objects);
                bool recover = history.Count > 0 && history[0].Ship.Stamp > _simulatedStamp + 1;
                if (recover)
                {
                    history = [history[^1]];
                }
                foreach (var result in history)
                {
                    _simulatedStamp = result.Ship.Stamp;
                    _navigation = result.Ship.Navigation.Copy();
                    Pilot = Pilot with { Position = result.Ship.Position };
                    ApplyChanges(result.Changes, shared: true);
                }
                _simulatedStamp = now;
                if (recover && InBallpark)
                {
                    var contact = CreateContact();
                    if (_navigation.Warp is not null)
                    {
                        Snapshot(space.Peers(Pilot), contact);
                    }
                    else
                    {
                        CorrectPosition(Pilot.ShipId, Pilot.Position!, _navigation.Velocity, now);
                        ApplyCourse(contact);
                    }
                }
                return;
            }
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
    }

    private void CorrectPosition(long id, SpacePosition position, SpacePosition velocity, int stamp)
    {
        Updates.Add(new(stamp, new DestinyCommand.SetBallPosition(id, position.X, position.Y, position.Z)));
        // This restores a missed sample. Ordinary prediction avoids setters that reset native interpolation and facing.
        Updates.Add(new(stamp, new DestinyCommand.SetBallVelocity(id, velocity.X, velocity.Y, velocity.Z)));
    }

    public void RefreshTackle(bool force = false)
    {
        if (tackle is null || !InBallpark || Pilot.Position is null || _destroying)
        {
            return;
        }
        tackle.Sweep(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        var incoming = tackle.Incoming(Pilot.ShipId).ToArray();
        if (!force && _incomingTackle.SequenceEqual(incoming) && _navigation.WarpDestination is null)
        {
            return;
        }
        _incomingTackle = incoming;
        var attributes = tackle.ShipAttributes(Pilot.PilotId);
        _warpBlocked = attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus) > 0;
        _mwdBlocked = incoming.Any(t => t.Module.MwdBlock > 0);
        _baseFlight = _baseFlight with { MaxSpeed = (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity) };
        ApplyFlightProperties();
        if (_warpBlocked && _navigation.WarpDestination is not null)
        {
            _navigation.CancelAlignment();
            _navigation.Stop();
            WarpEffect(Pilot.ShipId, false);
            Update(new DestinyCommand.Stop(Pilot.ShipId));
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
        ApplyChanges(tick.Changes);
    }

    private void ApplyChanges(IReadOnlyList<NavigationChange> changes, bool shared = false)
    {
        void Send(DestinyCommand command, int? sourceStamp = null)
        {
            int stamp = sourceStamp ?? _simulatedStamp;
            Updates.Add(new(stamp, command));
            if (!shared)
            {
                space.RecordOrder(Pilot.ShipId, new(stamp, command));
            }
        }
        foreach (var change in changes)
        {
            switch (change)
            {
                case NavigationChange.WarpExited:
                    WarpEffect(Pilot.ShipId, false);
                    break;
                case NavigationChange.Heading heading:
                    Send(
                        new DestinyCommand.GotoDirection(Pilot.ShipId, heading.Direction.X, heading.Direction.Y, heading.Direction.Z),
                        _simulatedStamp - 1
                    );
                    break;
                case NavigationChange.Stopped:
                    Send(new DestinyCommand.Stop(Pilot.ShipId), _simulatedStamp - 1);
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
            Stamp - _arrivalStamp < ArrivalDurationTicks,
            modules,
            _mining,
            health?.Read(Pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            _weapons,
            _shieldBoosts,
            _propulsion,
            _navigation.FollowMode,
            _localRepairs,
            _hardeners,
            _support,
            _tackleEffects,
            _simulatedStamp,
            DirtTime: DirtTimestamp()
        );

    public void RefreshFitting(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        Checkpoint();
        _baseFlight = _baseFlight with
        {
            MaxSpeed = (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            WarpSpeed =
                attributes.GetValueOrDefault(DogmaAttributeId.BaseWarpSpeed)
                * attributes.GetValueOrDefault(DogmaAttributeId.WarpSpeedMultiplier),
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
        var running = _mwdBlocked && _propulsion?.Module.Effect == DogmaEffect.Microwarpdrive ? null : _propulsion;
        var properties = running?.Module.Apply(_baseFlight) ?? _baseFlight;
        if (Pilot.Flight == properties)
        {
            return;
        }
        Pilot = Pilot with { Flight = properties };
        PublishMotion();
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

    public void SetTackleEffect(TackleContact effect, bool active)
    {
        _tackleEffects = _tackleEffects
            .Where(t => t.Module.ItemId != effect.Module.ItemId)
            .Concat(active ? [effect] : Array.Empty<TackleContact>())
            .ToArray();
        Effects.TackleEffect(Pilot.ShipId, effect, active);
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

    public void RepeatWeaponEffect(WeaponContact previous, WeaponContact current)
    {
        using var phase = Eden.Protocol.PilotSessions.SessionPerformance.Phase("weapon.publish");
        _weapons = _weapons.Where(w => w.ModuleId != current.ModuleId).Append(current).ToArray();
        Effects.WeaponEffect(Pilot.ShipId, previous, false);
        Effects.WeaponEffect(Pilot.ShipId, current, true);
        if (InBallpark && !_destroying)
        {
            space.Publish(CreateContact());
        }
    }

    public void SetLocalRepairEffect(LocalRepairContact effect, bool active)
    {
        _localRepairs = _localRepairs
            .Where(r => r.ModuleId != effect.ModuleId)
            .Concat(active ? [effect] : Array.Empty<LocalRepairContact>())
            .ToArray();
        Effects.LocalRepairEffect(Pilot.ShipId, effect, active);
        if (InBallpark && !_destroying)
        {
            space.Publish(CreateContact());
        }
    }

    public void SetHardenerEffect(HardenerContact effect, bool active)
    {
        _hardeners = _hardeners
            .Where(h => h.Module.ItemId != effect.Module.ItemId)
            .Concat(active ? [effect] : Array.Empty<HardenerContact>())
            .ToArray();
        Effects.HardenerEffect(Pilot.ShipId, effect, active);
        if (InBallpark && !_destroying)
        {
            space.Publish(CreateContact());
        }
    }

    public void SetSupportEffect(SupportContact effect, bool active)
    {
        _support = _support
            .Where(h => h.Module.ItemId != effect.Module.ItemId)
            .Concat(active ? [effect] : Array.Empty<SupportContact>())
            .ToArray();
        Effects.SupportEffect(Pilot.ShipId, effect, active);
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
        using var phase = Eden.Protocol.PilotSessions.SessionPerformance.Phase("contact.peers");
        lock (space.MotionLock)
        {
            RefreshPeersCore();
        }
    }

    private void RefreshPeersCore()
    {
        Checkpoint();
        PublishMotion();
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
            _peerOrderSequences.Remove(departed.Pilot.ShipId);
        }
        // Native Destiny predicts the common movement and collisions between stamped course changes.
        // Absolute position setters collapse its interpolation interval and are reserved for recovery.
        foreach (var ship in current)
        {
            var previous = Array.Find(_peers, peer => peer.Pilot.ShipId == ship.Pilot.ShipId);
            if (previous is null)
            {
                // AddBalls takes a single chunk. Replacing the entire park resets the observer's origin.
                Update(new DestinyCommand.AddBalls(Ballpark.AddShip(ship, Stamp)));
                _peerOrderSequences[ship.Pilot.ShipId] = space.OrderSequence(ship.Pilot.ShipId);
                Effects.Replay(ship, null);
                if (ship.Arriving)
                {
                    ArrivalEffect(ship.Pilot.ShipId);
                }
                // The client plays the arrival flash when it loads a ship already in warp.
                continue;
            }
            Effects.Replay(ship, previous);
            if (ship.DirtTime != previous.DirtTime || ship.Pilot.CorpId != previous.Pilot.CorpId)
            {
                Update(new DestinyCommand.OnSlimItemChange(ship.Pilot.ShipId, new ShipSlimItem(ship)));
            }
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
            long after = _peerOrderSequences.GetValueOrDefault(ship.Pilot.ShipId);
            bool missing = space.MissingOrders(ship.Pilot.ShipId, after) || ship.Stamp - previous.Stamp > SharedSpace.MovementHistoryTicks;
            if (missing)
            {
                CorrectPosition(ship.Pilot.ShipId, ship.Pilot.Position!, ship.Velocity, ship.Stamp);
                ApplyCourse(ship);
            }
            else
            {
                foreach (var order in space.Orders(ship.Pilot.ShipId, after))
                {
                    Updates.Add(order.Update);
                }
            }
            _peerOrderSequences[ship.Pilot.ShipId] = space.OrderSequence(ship.Pilot.ShipId);
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
                continue;
            }
            if (previous.Warp is not null)
            {
                WarpEffect(ship.Pilot.ShipId, false);
            }
        }
        _peers = current;
    }

    public IEnumerable<PilotContact> VisiblePilots() => space.Peers(Pilot).Select(ship => ship.Pilot).Prepend(PilotContact.From(Pilot));

    private void LeaveSpace(bool keepBallpark = false)
    {
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        tackle?.Cancel(Pilot.PilotId, Pilot.ShipId);
        if (tackle is not null && _incomingTackle.Length > 0)
        {
            _baseFlight = _baseFlight with
            {
                MaxSpeed = (float)tackle.ShipAttributes(Pilot.PilotId).GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            };
        }
        if (!keepBallpark)
        {
            InBallpark = false;
            _peers = [];
            _peerOrderSequences.Clear();
            _visibleAsteroids = [];
            _missiles = [];
            _npcs = [];
            _wrecks = [];
        }
        _propulsion = null;
        Pilot = Pilot with { Flight = _baseFlight };
        _mining = [];
        _weapons = [];
        _shieldBoosts = [];
        _localRepairs = [];
        _hardeners = [];
        _support = [];
        _tackleEffects = [];
        _incomingTackle = [];
        _warpBlocked = false;
        _mwdBlocked = false;
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
            _translocation = null;
            LeaveSpace();
        }
    }

    public void Relocate(DockedPilot destination, IReadOnlyList<StaticSpaceObject> destinationObjects)
    {
        lock (space.MotionLock)
        {
            bool keepBallpark = InBallpark && destination.Position is not null && Pilot.SystemId == destination.SystemId;
            bool wasWarping = _navigation.Warp is not null || _navigation.WarpDestination is not null;
            LeaveSpace(keepBallpark);
            if (!keepBallpark)
            {
                Updates.Clear();
            }
            Pilot = destination;
            objects = destinationObjects;
            _baseFlight = destination.Flight;
            _navigation = new();
            _simulatedStamp = Stamp;
            _jump = null;
            _translocation = null;
            _translocationArrival = false;
            _awaitingArrival = false;
            _arrivalStamp = null;
            if (wasWarping)
            {
                WarpEffect(Pilot.ShipId, false);
            }
            if (keepBallpark)
            {
                // This is an actual teleport, so observers need absolute setters
                // rather than predicting a course across the intervening distance.
                Update(
                    new DestinyCommand.SetBallPosition(
                        Pilot.ShipId,
                        destination.Position!.X,
                        destination.Position.Y,
                        destination.Position.Z
                    )
                );
                Update(new DestinyCommand.SetBallVelocity(Pilot.ShipId, 0, 0, 0));
                Update(new DestinyCommand.Stop(Pilot.ShipId));
                // Michelle retains this park and skips a SetState after another command at the same tick.
                // Diff the contacts the viewer actually has against the destination grid.
                _arrivalStamp = _simulatedStamp;
                RefreshVisibleAsteroids();
                RefreshWrecks();
                RefreshMissiles();
                RefreshNpcs();
                RefreshPeersCore();
            }
        }
    }

    public void StartTranslocation(
        IPilotTranslocation travel,
        TranslocationTarget target,
        IReadOnlyList<StaticSpaceObject> destinationObjects
    )
    {
        lock (space.MotionLock)
        {
            bool wasWarping = _navigation.Warp is not null || _navigation.WarpDestination is not null;
            _navigation = new();
            _jump = null;
            _arrivalStamp = null;
            _awaitingArrival = false;
            _simulatedStamp = Stamp;
            _translocation = new(travel, target, destinationObjects, Stamp + TranslocationDurationTicks);
            if (wasWarping)
            {
                WarpEffect(Pilot.ShipId, false);
            }
            Update(new DestinyCommand.Stop(Pilot.ShipId));
            Update(new DestinyCommand.TranslocationOut(Pilot.ShipId, target.System.Id, TranslocationDurationTicks * 1000));
            space.Publish(CreateContact());
        }
    }

    public void QueueTranslocationAfterSourceState(
        IPilotTranslocation travel,
        TranslocationTarget target,
        IReadOnlyList<StaticSpaceObject> destinationObjects
    )
    {
        _translocation = new(travel, target, destinationObjects, int.MaxValue, AwaitingSourceState: true);
    }

    public PilotTranslocation? CompleteTranslocation()
    {
        lock (space.MotionLock)
        {
            if (_translocation is not { AwaitingSourceState: false } transit || Stamp < transit.TransferStamp)
            {
                return null;
            }
            var moved = transit.Travel.Move(Pilot.PilotId, Pilot.ShipId, transit.Target.Id);
            Relocate(moved.Pilot, transit.Objects);
            _awaitingArrival = true;
            _translocationArrival = true;
            return moved;
        }
    }

    private void SetPosition(SpacePosition? position)
    {
        world.SetPosition(Pilot.ShipId, position);
        Pilot = Pilot with { Position = position, CurrentSystem = position is null ? null : Pilot.CurrentSystem };
    }

    public void Snapshot()
    {
        lock (space.MotionLock)
        {
            SnapshotCore();
        }
    }

    private void SnapshotCore()
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
        PublishMotion();
        var contact = CreateContact();
        space.Publish(contact);
        Snapshot(space.Peers(Pilot), contact);
        if (arriving)
        {
            if (_translocationArrival)
            {
                Update(new DestinyCommand.SpecialEffect(Pilot.ShipId, null, null, null, null, "effects.JumpDriveIn", false, true, false));
                _translocationArrival = false;
            }
            else
            {
                ArrivalEffect(Pilot.ShipId);
            }
        }
        if (_translocation is { AwaitingSourceState: true } departure)
        {
            // A station-origin TR first loads a real source-space ego. Only then
            // can the client's native jump camera take over from its undock camera.
            StartTranslocation(departure.Travel, departure.Target, departure.Objects);
        }
    }

    private void Snapshot(ShipContact[] peers, ShipContact own)
    {
        _peers = peers;
        _peerOrderSequences.Clear();
        foreach (var peer in peers)
        {
            _peerOrderSequences[peer.Pilot.ShipId] = space.OrderSequence(peer.Pilot.ShipId);
        }
        int stamp = Stamp;
        var visible = VisibleObjects();
        _npcs = ReadNpcs();
        _wrecks = ReadWrecks();
        _missiles = ReadMissiles();
        _visibleAsteroids = visible.Where(o => o.CategoryId == AsteroidDefinitions.Category).ToArray();
        Updates.Add(new(stamp, new DestinyCommand.SetState(Ballpark.Snapshot(own, stamp, visible, peers, _npcs, _wrecks, _missiles))));
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
        void Send(DestinyCommand command)
        {
            Updates.Add(new(ship.Stamp, command));
            if (ship.Pilot.ShipId == Pilot.ShipId)
            {
                space.RecordOrder(Pilot.ShipId, new(ship.Stamp, command));
            }
        }
        long id = ship.Pilot.ShipId;
        if (ship.AlignTo is { } end)
        {
            Send(new DestinyCommand.SetSpeedFraction(id, 1d));
            Send(new DestinyCommand.SetBallVelocity(id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z));
            Send(new DestinyCommand.WarpTo(id, end.X, end.Y, end.Z, 0d, ship.WarpFactor));
            return;
        }
        if (ship.Heading is null)
        {
            Send(new DestinyCommand.Stop(id));
        }
        else
        {
            if (ship.FollowRange is double range)
            {
                Send(new DestinyCommand.Follow(ship.FollowMode, id, ship.FollowTargetId, range));
            }
            else
            {
                Send(new DestinyCommand.GotoDirection(id, ship.Heading.X, ship.Heading.Y, ship.Heading.Z));
            }
            Send(new DestinyCommand.SetSpeedFraction(id, ship.SpeedFraction));
        }
    }

    private void WarpEffect(long id, bool start)
    {
        // The simulation command moves the ball. This separate toggle drives the pilot's tunnel and peer flash.
        Update(new DestinyCommand.SpecialEffect(id, null, null, null, null, "effects.Warping", false, start, true));
    }

    private void SetAtRest() => _navigation.SetAtRest();

    private void Update(DestinyCommand command)
    {
        var update = new StampedDestinyUpdate(_simulatedStamp, command);
        Updates.Add(update);
        space.RecordOrder(Pilot.ShipId, update);
    }

    public PilotCallResult StartJump(FlightCommand.Jump request, ISystemObjects systems)
    {
        long sourceId = request.SourceId;
        long exitId = request.ExitId;
        long shipId = request.ShipId;
        Checkpoint();
        if (
            !InBallpark
            || Translocating
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
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        tackle?.Cancel(Pilot.PilotId, Pilot.ShipId);
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

    private PilotCallResult StartWarp(long target, double range, SpacePosition? coordinates = null)
    {
        RefreshTackle(true);
        if (_warpBlocked)
        {
            return new PilotCallResult.Failure("Warp disruption is stronger than your ship's warp core strength.");
        }
        var body = objects.FirstOrDefault(item => item.Id == target);
        if (body is null && coordinates is null)
        {
            return PilotCallResult.Unsupported;
        }
        if (
            !InBallpark
            || !(
                coordinates is { } point
                    ? _navigation.StartWarpPoint(Pilot.ShipId, Pilot.Flight, Pilot.Position!, point, target, range, _simulatedStamp)
                    : _navigation.StartWarp(Pilot.ShipId, Pilot.Flight, Pilot.Position!, body!, range, _simulatedStamp)
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
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
        lock (space.MotionLock)
        {
            try
            {
                return ExecuteCommand(command);
            }
            finally
            {
                PublishMotion();
                if (InBallpark && !_destroying)
                {
                    space.Publish(CreateContact());
                }
            }
        }
    }

    private PilotCallResult ExecuteCommand(FlightCommand command)
    {
        if (Translocating && command is not FlightCommand.RefreshState)
        {
            return new PilotCallResult.Failure("Wait for translocation to finish before navigating.");
        }
        if (command is FlightCommand.Undock undock && Pilot.Position is null && undock.ShipId == Pilot.ShipId)
        {
            _simulatedStamp = Stamp;
            var launch = StationUndocking.Launch(Pilot.ShipId, Pilot.Home, Pilot.Flight);
            SetPosition(launch.Position);
            _navigation.SetHeading(launch.Direction.X, launch.Direction.Y, launch.Direction.Z);
            _navigation.SetVelocity(launch.Velocity);
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
            if (_jump is null && command is FlightCommand.WarpToPoint pointRetry)
            {
                return _navigation.WarpOrder == (pointRetry.OrderId, pointRetry.Range) ? PilotCallResult.None : PilotCallResult.Unsupported;
            }
            return PilotCallResult.Unsupported;
        }
        switch (command)
        {
            case FlightCommand.WarpTo warp:
                return StartWarp(warp.TargetId, warp.Range);
            case FlightCommand.WarpToPoint point:
                return StartWarp(point.OrderId, point.Range, point.Position);
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
