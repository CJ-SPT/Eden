using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Flight;

internal sealed partial class FlightSession(
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
    IPilotShipAppearance? appearance = null,
    IPilotDrones? drones = null,
    IPilotCrimewatch? crimewatch = null,
    IPilotElectronicWarfare? electronicWarfare = null,
    IPilotGateTravel? gates = null,
    IHumanPresence? presence = null,
    IHumanPresenceLease? humanPresenceLease = null,
    ICapsuleerPresentation? presentation = null,
    IPilotMissions? missions = null,
    IPilotCommandBursts? commandBursts = null
) : IDisposable
{
    internal const int ArrivalDurationTicks = FlightTravel.ArrivalDurationTicks;
    internal const int JumpDurationTicks = FlightTravel.JumpDurationTicks;
    internal const int TranslocationDurationTicks = FlightTravel.TranslocationDurationTicks;

    private FlightEffects? _effects;
    private FlightScene? _scene;
    private FlightModuleEffects? _moduleEffects;
    private readonly IHumanPresenceLease? _humanPresence = humanPresenceLease ?? presence?.Select(pilot.PilotId, pilot.SystemId);

    // Docking can replace Pilot before the lazy effect state is first needed.
    private readonly FlightProperties _initialFlight = pilot.Flight;
    private NavigationState _navigation = new();
    private ShipDirtState? _dirt;
    private bool _disconnecting;
    private bool _destroying;
    private bool _accelerationGateWarp;
    private readonly List<InventoryChange> _gateInventoryChanges = [];

    internal IReadOnlyList<InventoryChange> TakeGateInventoryChanges()
    {
        var items = _gateInventoryChanges.ToArray();
        _gateInventoryChanges.Clear();
        return items;
    }

    private int _simulatedStamp = space.Stamp(clock);
    private readonly FlightTravel _travel = new();

    private FlightEffects Effects => _effects ??= new(Update);
    private FlightScene Scene =>
        _scene ??= new(space, clock, objects, Effects, Update, Updates.Add, health, encounters, wrecks, missiles, drones, commandBursts);
    private FlightSceneContext SceneContext => new(Pilot, InBallpark, CanTarget, _simulatedStamp);
    private FlightModuleEffects ModuleEffects => _moduleEffects ??= new(_initialFlight, modules, Effects);
    private int Stamp => space.Stamp(clock);

    public bool InBallpark { get; private set; }
    internal int CurrentStamp => Stamp;

    public bool Translocating => _travel.Translocating;
    private DockedPilot _pilot = pilot;
    public DockedPilot Pilot
    {
        get => _pilot;
        private set
        {
            if (value.SystemId != _pilot.SystemId)
            {
                _humanPresence?.Transfer(value.SystemId);
                presentation?.Refresh();
            }
            _pilot = value with { Identity = value.Identity with { Fleet = _pilot.Identity.Fleet } };
        }
    }

    internal void RenameShip(string name) => Pilot = Pilot with { Ship = Pilot.Ship with { Name = name } };

    public List<StampedDestinyUpdate> Updates { get; } = [];
    public bool Moving => _navigation.Direction is not null || _navigation.Warp is not null || _navigation.Velocity != SpacePosition.Zero;
    internal SpatialStatus SpatialStatus => space.SpatialStatus(Pilot.ShipId);
    public bool Cloaked => SpatialStatus.CloakModuleId != 0;
    public SpacePosition Heading => _navigation.Direction ?? _navigation.LastHeading;
    private SpatialActivation? _microJump;
    public bool MicroJumpSpooling => _microJump is not null;
    public bool CanTarget =>
        !Cloaked
        && InBallpark
        && !_destroying
        && Pilot.Position is not null
        && _travel.Gate is null
        && !Translocating
        && _navigation.Warp is null;
    internal bool CanAcquireTargets => CanTarget && SpatialStatus.TargetingReadyAt <= clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public bool InWarp => _navigation.Warp is not null;
    private bool _wormholeJumpCancelled;

    internal bool ConsumeWormholeJumpCancellation()
    {
        bool cancelled = _wormholeJumpCancelled;
        _wormholeJumpCancelled = false;
        return cancelled;
    }

    internal bool PreparingWarp => _navigation.WarpDestination is not null;
    public bool MicrowarpdriveBlocked => ModuleEffects.MwdBlocked;

    // Pinned shieldBoosting effect 4 is warp-safe. Gate/session transitions and
    // the terminal hull transition still prevent local module activations.
    public bool CanUseSelfModules => InBallpark && !_destroying && Pilot.Position is not null && _travel.Gate is null && !Translocating;
    public SpacePosition Velocity => _navigation.Velocity;
    public float BaseSpeed => ModuleEffects.BaseFlight.MaxSpeed;

    internal IReadOnlyList<DroneContact> VisibleDrones => Scene.VisibleDrones;
    public IReadOnlyList<NpcContact> VisibleNpcs => Scene.VisibleNpcs;
    public IReadOnlyList<WreckContact> VisibleWrecks => Scene.VisibleWrecks;
    public IReadOnlyList<MissileContact> VisibleMissiles => Scene.VisibleMissiles;

    internal CommandBurstCandidate[] CommandBurstCandidates() => space.CommandBurstCandidates(Pilot);

    internal void NotifyCommandBursts(IReadOnlyList<CommandBurstBoost> boosts) =>
        Update(new DestinyCommand.DbuffUpdated(Pilot.ShipId, boosts));

    internal void NotifyCommandBurstPulse(CommandBurstPulse pulse)
    {
        string family = pulse.Module.Effect switch
        {
            DogmaEffect.ArmorCommandBurst => "Armor",
            DogmaEffect.ShieldCommandBurst => "Shield",
            DogmaEffect.SkirmishCommandBurst => "Skirmish",
            DogmaEffect.MiningForemanBurst => "Mining",
            _ => "Information",
        };
        Emit(
            Pilot.ShipId,
            null,
            "effects.WarfareLinkSphere" + family,
            new() { ["graphicRadius"] = pulse.Module.Radius, ["radius"] = pulse.Module.Radius }
        );
        foreach (long recipient in pulse.Recipients)
        {
            Emit(recipient, Pilot.ShipId, "effects.WarfareLink" + family, null);
        }

        void Emit(long ship, long? target, string graphic, Dictionary<string, object?>? info)
        {
            var command = new DestinyCommand.CommandBurstVisual(
                ship,
                pulse.Module.Id,
                pulse.Module.TypeId,
                target,
                pulse.Magazine.TypeId,
                graphic,
                info
            );
            // Publish the source and recipient visuals together in the emitting ship's flight stream.
            Update(command);
        }
    }

    internal SmartbombCandidates SmartbombCandidates()
    {
        Scene.RefreshNpcs(SceneContext);
        Scene.RefreshDrones(SceneContext);
        Scene.RefreshMissiles(SceneContext);
        return new(
            space.SmartbombShips(Pilot),
            VisibleNpcs.Where(npc => npc.Warp is null).Select(npc => npc.Id).ToArray(),
            VisibleDrones.Select(drone => drone.Id).ToArray(),
            VisibleMissiles.Select(missile => missile.Id).ToArray(),
            Scene.Objects
        );
    }

    internal AdvancedWeaponCycle VortonCandidates()
    {
        Scene.RefreshNpcs(SceneContext);
        Scene.RefreshDrones(SceneContext);
        return new(
            WeaponActivation.Vorton,
            Ships: space.SmartbombShips(Pilot, includeCloaked: false),
            Npcs: VisibleNpcs.Where(npc => npc.Warp is null).Select(npc => npc.Id).ToArray(),
            Drones: VisibleDrones.Select(drone => drone.Id).ToArray()
        );
    }

    public void RefreshDrones() => Scene.RefreshDrones(SceneContext);

    private void PublishContact()
    {
        if (InBallpark && !_destroying)
        {
            space.Publish(CreateContact());
        }
    }

    private long? DirtTimestamp()
    {
        if (appearance is not null && _dirt?.ShipId != Pilot.ShipId)
        {
            _dirt = appearance.Read(Pilot.PilotId, Pilot.ShipId);
        }
        return _dirt?.Timestamp;
    }

    public void UpdateFleet(FleetIdentity? identity)
    {
        _pilot = Pilot with { Identity = Pilot.Identity with { Fleet = identity } };
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
                CorporationAccess = identity.Access,
                CorporationAccountKey = identity.AccountKey,
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

    internal StaticSpaceObject? FindBookmarkObject(long id) => Scene.NavigationTarget(SceneContext, id);

    public TargetContact? FindTarget(long id) => Scene.FindTarget(SceneContext, id);

    internal PilotWeaponTarget? FindPilotWeaponTarget(long id) => Scene.FindPilotWeaponTarget(SceneContext, id);

    internal bool NpcInWarp(long id) => Scene.NpcInWarp(id);

    public void RefreshMissiles() => Scene.RefreshMissiles(SceneContext);

    private StaticSpaceObject? NavigationTarget(long id) => Scene.NavigationTarget(SceneContext, id);

    public void RefreshWrecks() => Scene.RefreshWrecks(SceneContext);

    public void RefreshNpcs() => Scene.RefreshNpcs(SceneContext);

    public void NotifyNpcWeapon(long npcId, NpcWeaponContact weapon) => Scene.NotifyNpcWeapon(npcId, weapon);

    public void BeginShipLoss()
    {
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        drones?.Release(Pilot.PilotId, Pilot.ShipId);
        tackle?.Cancel(Pilot.PilotId, Pilot.ShipId);
        electronicWarfare?.Cancel(Pilot.PilotId, Pilot.ShipId, incoming: true);
        _destroying = true;
        space.Leave(Pilot.ShipId);
    }

    public void ReplaceDestroyedShip(DockedPilot capsule, IReadOnlyList<StaticSpaceObject>? destination = null)
    {
        long destroyed = Pilot.ShipId;
        Scene.StopNpcWeapons(destroyed);
        Pilot = capsule;
        ModuleEffects.AdoptHull(capsule.Flight);
        _navigation.AdoptHull();
        Scene.ForgetMissiles();
        _destroying = false;
        if (capsule.Position is null)
        {
            // A dead pod has no replacement ego in this ballpark. The session
            // delta loads the home station after the old pod's terminal removal.
            Update(new DestinyCommand.RemoveBall(destroyed, true));
            InBallpark = false;
            Scene.LeaveSpace();
            Scene.ReplaceObjects(destination ?? throw new InvalidOperationException("The respawn system is missing."));
            _navigation.Dock();
            _travel.Reset();
            return;
        }
        // The client can adopt an already loaded capsule as ego during the ship
        // session delta. Keep the old hull until the capsule ball has arrived.
        var contact = CreateContact();
        Update(new DestinyCommand.AddBalls(Ballpark.AddShip(contact, Stamp)));
        Update(new DestinyCommand.RemoveBall(destroyed, true));
        space.Publish(contact);
    }

    public void NotifyHealth(ShipHealth current) => Scene.NotifyHealth(current);

    internal void RefreshAnomalies(IReadOnlyList<StaticSpaceObject> objects) => Scene.RefreshAnomalies(SceneContext, objects);

    public void RefreshAsteroids(IReadOnlyList<StaticSpaceObject> asteroids) => Scene.RefreshAsteroids(SceneContext, asteroids);

    internal void RefreshStructures(IReadOnlyList<StaticSpaceObject> structures) => Scene.RefreshStructures(SceneContext, structures);

    private void RefreshVisibleAsteroids() => Scene.RefreshVisibleAsteroids(SceneContext);

    private ShipMotion Motion() =>
        new(
            Pilot.ShipId,
            Pilot.SystemId,
            _simulatedStamp,
            Pilot.Position!,
            Pilot.Flight,
            _navigation,
            _travel.Gate is null && !_destroying && !Cloaked,
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
        using (space.UseSystem(Pilot.SystemId))
        {
            RefreshTackle();
            int now = Stamp;
            if (Pilot.Position is not null && world is ISystemMotionStore motionStore)
            {
                var history = space.Advance(Motion(), now, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), motionStore, Scene.Objects);
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

    internal IDisposable? UseSystem() => space.UseSystem(Pilot.SystemId);

    private void CorrectPosition(long id, SpacePosition position, SpacePosition velocity, int stamp) =>
        Scene.CorrectPosition(id, position, velocity, stamp);

    public void RefreshTackle(bool force = false)
    {
        if (tackle is null || !InBallpark || Pilot.Position is null || _destroying)
        {
            return;
        }
        if (
            !ModuleEffects.RefreshTackle(
                tackle,
                Pilot,
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                force,
                _navigation.WarpDestination is not null
            )
        )
        {
            return;
        }
        ApplyFlightProperties();
        bool blocked = ModuleEffects.WarpBlocked || _accelerationGateWarp && ModuleEffects.IncomingTackle.Any(t => t.Module.Strength > 0);
        if (blocked && _navigation.WarpDestination is not null)
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
            Scene.Objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup).ToArray()
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
            _travel.Gate?.Destination.SourceId,
            _travel.IsArriving(Stamp),
            ModuleEffects.Modules,
            ModuleEffects.Mining,
            health?.Read(Pilot.PilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            ModuleEffects.Weapons,
            ModuleEffects.ShieldBoosts,
            ModuleEffects.Propulsion,
            _navigation.FollowMode,
            ModuleEffects.LocalRepairs,
            ModuleEffects.Hardeners,
            ModuleEffects.Support,
            ModuleEffects.TackleEffects,
            _simulatedStamp,
            DirtTime: DirtTimestamp(),
            ElectronicWarfare: ModuleEffects.ElectronicWarfare,
            RemoteRepairs: ModuleEffects.RemoteRepairs,
            CapacitorEffects: ModuleEffects.CapacitorEffects,
            SignatureSuppressions: ModuleEffects.SignatureSuppressions,
            AssaultDamageControls: ModuleEffects.AssaultDamageControls,
            GateArrival: _travel.Arrival,
            DepartureAt: _travel.DepartureAt,
            Cloaked: Cloaked,
            MicroJump: _microJump
        );

    public void RefreshFitting(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        Checkpoint();
        Pilot = ModuleEffects.RefreshFitting(Pilot, attributes);
        ApplyFlightProperties();
    }

    public void SetBaseSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed <= 0 || speed > float.MaxValue)
        {
            throw new InvalidDataException("Invalid ship navigation speed.");
        }
        Checkpoint();
        ModuleEffects.SetBaseSpeed(speed);
        ApplyFlightProperties();
    }

    public void SetPropulsionEffect(PropulsionContact? effect)
    {
        Checkpoint();
        var change = ModuleEffects.SetPropulsion(effect);
        ApplyFlightProperties();
        ModuleEffects.ReplayPropulsion(Pilot.ShipId, change);
        PublishContact();
    }

    private void ApplyFlightProperties()
    {
        var properties = ModuleEffects.Flight;
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
        ModuleEffects.SetMiningEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void SetElectronicWarfareEffect(ElectronicWarfareSpaceContact effect, bool active)
    {
        ModuleEffects.SetElectronicWarfareEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void SetTackleEffect(TackleContact effect, bool active)
    {
        ModuleEffects.SetTackleEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void RefreshModules(IReadOnlyList<InventoryItem> items) => ModuleEffects.RefreshModules(items);

    public void SetWeaponEffect(WeaponContact effect, bool active)
    {
        ModuleEffects.SetWeaponEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void RepeatWeaponEffect(WeaponContact previous, WeaponContact current)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("weapon.publish");
        ModuleEffects.RepeatWeaponEffect(Pilot.ShipId, previous, current);
        PublishContact();
    }

    public void SetRemoteRepairEffect(RemoteRepairCycle repair, bool active)
    {
        ModuleEffects.SetRemoteRepairEffect(Pilot.ShipId, repair, active);
        PublishContact();
    }

    public void SetCapacitorEffect(CapacitorCycle effect, bool active)
    {
        ModuleEffects.SetCapacitorEffect(effect, active);
        PublishContact();
    }

    public void SetLocalRepairEffect(LocalRepairContact effect, bool active)
    {
        ModuleEffects.SetLocalRepairEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void SetHardenerEffect(HardenerContact effect, bool active)
    {
        ModuleEffects.SetHardenerEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void SetSignatureSuppressionEffect(SignatureSuppressionContact effect, bool active)
    {
        ModuleEffects.SetSignatureSuppressionEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void SetAssaultDamageControlEffect(AssaultDamageControlContact effect, bool active)
    {
        ModuleEffects.SetAssaultDamageControlEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void SetSupportEffect(SupportContact effect, bool active)
    {
        ModuleEffects.SetSupportEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void SetShieldBoostEffect(ShieldBoostContact effect, bool active)
    {
        ModuleEffects.SetShieldBoostEffect(Pilot.ShipId, effect, active);
        PublishContact();
    }

    public void RefreshPeers()
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("contact.peers");
        using (space.UseSystem(Pilot.SystemId))
        {
            RefreshPeersCore();
            RefreshDrones();
        }
    }

    private void RefreshPeersCore()
    {
        Checkpoint();
        PublishMotion();
        var contact = CreateContact();
        space.Publish(contact);
        Scene.RefreshPeers(SceneContext, contact);
    }

    public IEnumerable<PilotContact> VisiblePilots() => space.Peers(Pilot).Select(ship => ship.Pilot).Prepend(PilotContact.From(Pilot));

    private void LeaveSpace(bool keepBallpark = false)
    {
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        drones?.Release(Pilot.PilotId, Pilot.ShipId);
        _accelerationGateWarp = false;
        if (_disconnecting)
        {
            tackle?.Disconnect(Pilot.PilotId, Pilot.ShipId);
            electronicWarfare?.Cancel(Pilot.PilotId, Pilot.ShipId);
        }
        else
        {
            tackle?.Cancel(Pilot.PilotId, Pilot.ShipId);
            electronicWarfare?.Cancel(Pilot.PilotId, Pilot.ShipId, incoming: true);
        }
        ModuleEffects.ReconcileReleasedTackle(tackle, Pilot.PilotId);
        if (!keepBallpark)
        {
            InBallpark = false;
            Scene.LeaveSpace();
        }
        ModuleEffects.ClearEffects();
        Pilot = Pilot with { Flight = ModuleEffects.BaseFlight };
        space.Leave(Pilot.ShipId);
    }

    public void Dispose()
    {
        try
        {
            Checkpoint();
            PrepareDisconnect();
        }
        finally
        {
            try
            {
                _travel.CancelTranslocation();
                LeaveSpace();
            }
            finally
            {
                try
                {
                    _humanPresence?.Dispose();
                }
                finally
                {
                    presentation?.Refresh();
                }
            }
        }
    }

    public void PrepareDisconnect()
    {
        if (!_disconnecting && crimewatch is not null)
        {
            crimewatch.Disconnect(Pilot.PilotId, Pilot.ShipId);
            _disconnecting = true;
        }
    }

    public void Relocate(DockedPilot destination, IReadOnlyList<StaticSpaceObject> destinationObjects)
    {
        using (space.UseSystem(Pilot.SystemId))
        {
            bool enteringSpace = Pilot.Position is null && destination.Position is not null;
            bool keepBallpark = InBallpark && destination.Position is not null && Pilot.SystemId == destination.SystemId;
            bool wasWarping = _navigation.Warp is not null || _navigation.WarpDestination is not null;
            LeaveSpace(keepBallpark);
            if (!keepBallpark)
            {
                Updates.Clear();
            }
            Pilot = destination;
            Scene.ReplaceObjects(destinationObjects);
            ModuleEffects.SetBaseFlight(destination.Flight);
            _navigation = new();
            _simulatedStamp = Stamp;
            _travel.Reset();
            if (enteringSpace)
            {
                _travel.ExpectArrival();
            }
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
                _travel.ArrivedAt(_simulatedStamp);
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
        using (space.UseSystem(Pilot.SystemId))
        {
            bool wasWarping = _navigation.Warp is not null || _navigation.WarpDestination is not null;
            _navigation = new();
            _simulatedStamp = Stamp;
            _travel.BeginTranslocation(travel, target, destinationObjects, Stamp);
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
        _travel.QueueTranslocation(travel, target, destinationObjects);
    }

    public void SourceStatePublished() => _travel.SourceStatePublished(Stamp);

    public void StartPendingTranslocation()
    {
        using (space.UseSystem(Pilot.SystemId))
        {
            if (_travel.DepartureReady(Stamp) is { } pending)
            {
                StartTranslocation(pending.Travel, pending.Target, pending.Objects);
            }
        }
    }

    public PilotTranslocation? CompleteTranslocation()
    {
        using (space.UseSystem(Pilot.SystemId))
        {
            if (_travel.TranslocationDue(Stamp) is not { } transit)
            {
                return null;
            }
            var moved = transit.Travel.Move(Pilot.PilotId, Pilot.ShipId, transit.Target);
            Relocate(moved.Pilot, transit.Objects);
            _travel.CompleteTranslocation();
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
        using (space.UseSystem(Pilot.SystemId))
        {
            SnapshotCore();
        }
    }

    private void SnapshotCore()
    {
        // A new source SetState would run the client's asynchronous DoBallClear
        // and finalize the active jump camera before destination arrival.
        if (_travel.Translocation is { } transfer && (!transfer.AwaitingSourceState || transfer.PublishedSourceStamp is not null))
        {
            return;
        }
        Checkpoint();
        if (!InBallpark && Pilot.Position is { } position)
        {
            // A legacy save or a disconnect during warp can leave the ship inside a rigid body.
            foreach (var body in Scene.Objects.Where(o => o.GroupId != AsteroidDefinitions.BeltGroup))
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
        bool arriving = _travel.BeginArrival(entering, Stamp);
        PublishMotion();
        var contact = CreateContact();
        space.Publish(contact);
        Snapshot(space.Peers(Pilot), contact);
        if (arriving)
        {
            if (_travel.TranslocationArrival)
            {
                Update(new DestinyCommand.SpecialEffect(Pilot.ShipId, null, null, null, null, "effects.JumpDriveIn", false, true, false));
                _travel.ArrivalEffectSent();
            }
            else
            {
                if (_travel.WormholeArrival is long wormhole)
                {
                    Update(
                        new DestinyCommand.SpecialEffect(wormhole, null, null, null, null, "effects.WormholeActivity", false, true, false)
                    );
                }
                else if (_travel.Arrival is { } arrival)
                {
                    Effects.GateActivity(arrival.GateId, arriving: true);
                }
                ArrivalEffect(Pilot.ShipId);
            }
        }
    }

    private void Snapshot(ShipContact[] peers, ShipContact own) => Scene.Snapshot(SceneContext, peers, own);

    private void ApplyCourse(ShipContact ship) => Scene.ApplyCourse(SceneContext, ship);

    private void WarpEffect(long id, bool start) => Effects.WarpEffect(id, start);

    private void SetAtRest() => _navigation.SetAtRest();

    private void Update(DestinyCommand command)
    {
        var update = new StampedDestinyUpdate(_simulatedStamp, command);
        Updates.Add(update);
        space.RecordOrder(Pilot.ShipId, update);
    }

    private PilotCallResult.Failure? TravelFailure(CrimewatchTravel action, int? destination = null) =>
        FlightTravel.CheckPermission(crimewatch, Pilot.PilotId, action, destination);

    public PilotCallResult StartJump(FlightCommand.Jump request, ISystemObjects systems)
    {
        Checkpoint();
        var departure = _travel.PrepareGate(
            new(Pilot, InBallpark, InWarp, _navigation.WarpDestination is not null),
            request,
            Scene.Objects,
            systems,
            crimewatch
        );
        if (departure.Destination is not { } transit)
        {
            return departure.Result;
        }
        // Keep the source scene alive for the camera handoff before transferring the pilot.
        _travel.BeginGate(transit, Stamp, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        drones?.Release(Pilot.PilotId, Pilot.ShipId);
        tackle?.Cancel(Pilot.PilotId, Pilot.ShipId);
        electronicWarfare?.Cancel(Pilot.PilotId, Pilot.ShipId, incoming: true);
        SetAtRest();
        Update(new DestinyCommand.Stop(Pilot.ShipId));
        DepartureEffect(Pilot.ShipId, transit.SourceId, transit.System.Id);
        space.Publish(CreateContact());
        return PilotCallResult.None;
    }

    internal void StartWormholeJump(long entrance, IWormholeTravel travel, ISystemObjects systems)
    {
        Checkpoint();
        if (!CanUseSelfModules || InWarp || PreparingWarp || MicroJumpSpooling)
        {
            throw new InvalidDataException("Wait until your ship is ready to enter the wormhole.");
        }
        var destination = travel.Prepare(Pilot.PilotId, Pilot.ShipId, entrance);
        var objects = systems.Load(destination.System.Id);
        _travel.BeginGate(
            new(entrance, destination.Exit, destination.System, objects, travel),
            Stamp,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        drones?.Release(Pilot.PilotId, Pilot.ShipId);
        tackle?.Cancel(Pilot.PilotId, Pilot.ShipId);
        electronicWarfare?.Cancel(Pilot.PilotId, Pilot.ShipId, incoming: true);
        SetAtRest();
        Update(new DestinyCommand.Stop(Pilot.ShipId));
        Update(
            new DestinyCommand.WormholeJumpOut(
                Pilot.ShipId,
                entrance,
                destination.DestinationClass,
                destination.System.Id,
                JumpDurationTicks * 1000
            )
        );
        space.Publish(CreateContact());
    }

    public bool CompleteJump()
    {
        if (_travel.GateDue(Stamp) is not { } jump)
        {
            return false;
        }
        var position = GateTravelRules.Arrival(Pilot.ShipId, jump.Exit.Position, jump.Exit.Radius, Pilot.Flight.ShipRadius);
        if (jump.Wormhole is { } wormhole)
        {
            try
            {
                position = wormhole.Transfer(Pilot.PilotId, Pilot.ShipId, jump.SourceId, jump.Exit.Id).Position!;
            }
            catch (InvalidDataException)
            {
                _travel.Reset();
                _wormholeJumpCancelled = true;
                return false;
            }
        }
        else if (gates is not null)
        {
            position = gates.Transfer(Pilot.PilotId, Pilot.ShipId, jump.SourceId, jump.Exit.Id).Position!;
        }
        else
        {
            world.SetPosition(Pilot.ShipId, position, jump.System.Id);
        }
        LeaveSpace();
        Scene.ReplaceObjects(jump.Objects);
        if (jump.Wormhole is not null)
        {
            Scene.RefreshAnomalies(SceneContext, [jump.Exit]);
        }
        Pilot = Pilot with { Position = position, CurrentSystem = jump.System };
        _travel.CompleteGate(jump.Exit.Id, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), jump.Wormhole is not null);
        return true;
    }

    // SubwayService reads graphicInfo[0] to preload the skybox and attach the destination camera.
    private void DepartureEffect(long shipId, long gateId, int systemId) =>
        Effects.DepartureEffect(shipId, gateId, systemId, JumpDurationTicks * 1000);

    // SubwayService completes the gate transition. JumpIn supplies the ship flash.
    private void ArrivalEffect(long shipId) => Effects.ArrivalEffect(shipId);

    private PilotCallResult StartWarp(long target, double range, SpacePosition? coordinates = null, bool accelerationGate = false)
    {
        if (!double.IsFinite(range) || range is < 0 or > NavigationState.MaximumWarpRange)
        {
            return PilotCallResult.Unsupported;
        }
        if (TravelFailure(CrimewatchTravel.Warp) is { } blocked)
        {
            return blocked;
        }
        RefreshTackle(true);
        if (MicroJumpSpooling || Cloaked && !SpatialStatus.WarpSafe)
        {
            return new PilotCallResult.Failure("The active cloak or micro-jump spool prevents warp.");
        }
        if (ModuleEffects.WarpBlocked || accelerationGate && ModuleEffects.IncomingTackle.Any(t => t.Module.Strength > 0))
        {
            return new PilotCallResult.Failure("Warp disruption is stronger than your ship's warp core strength.");
        }
        var body = Scene.Objects.FirstOrDefault(item => item.Id == target);
        if (body is null && coordinates is null)
        {
            return PilotCallResult.Unsupported;
        }
        if (!accelerationGate && missions is not null)
        {
            try
            {
                var requested = coordinates ?? CelestialWarpPoint.Resolve(body!, Pilot.Position!);
                var destination = missions.WarpDestination(Pilot.SystemId, Pilot.Position!, requested);
                if (destination == requested)
                {
                    var landing = WarpCourse.LandingPoint(Pilot.ShipId, Pilot.Flight.ShipRadius, Pilot.Position!, requested, range);
                    var checkedLanding = missions.WarpDestination(Pilot.SystemId, Pilot.Position!, landing);
                    if (checkedLanding != landing)
                    {
                        destination = checkedLanding;
                    }
                }
                if (destination != requested)
                {
                    coordinates = destination;
                    range = 0;
                }
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        var navigation = _navigation.Copy();
        if (
            !InBallpark
            || !(
                coordinates is { } point
                    ? navigation.StartWarpPoint(Pilot.ShipId, Pilot.Flight, Pilot.Position!, point, target, range, _simulatedStamp)
                    : navigation.StartWarp(Pilot.ShipId, Pilot.Flight, Pilot.Position!, body!, range, _simulatedStamp)
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (accelerationGate)
        {
            var activation = missions!.ActivateGate(Pilot.PilotId, target, coordinates!);
            _gateInventoryChanges.AddRange(activation.Items);
        }
        _navigation = navigation;
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        drones?.Release(Pilot.PilotId, Pilot.ShipId);
        var contact = CreateContact();
        ApplyCourse(contact);
        _accelerationGateWarp = accelerationGate;
        // WarpLoop prepares the tunnel while aligning, then observes Destiny's positive
        // effect stamp to trigger the camera and acceleration sequence on the client.
        WarpEffect(Pilot.ShipId, true);
        space.Publish(contact);
        return PilotCallResult.None;
    }

    public PilotCallResult Execute(FlightCommand command)
    {
        using (space.UseSystem(Pilot.SystemId))
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
        if (MicroJumpSpooling && command is not (FlightCommand.RefreshState or FlightCommand.Speed or FlightCommand.Stop))
        {
            return new PilotCallResult.Failure("Wait for the micro-jump spool to finish before navigating.");
        }
        if (Cloaked && command is FlightCommand.Dock or FlightCommand.Jump or FlightCommand.ActivateAccelerationGate)
        {
            return new PilotCallResult.Failure("Decloak before docking or using a gate.");
        }
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
            (_travel.Gate is not null || _navigation.Warp is not null || _navigation.WarpDestination is not null)
            && command is not FlightCommand.RefreshState
        )
        {
            // The native docking navigator retries its warp order on a timer.
            // Acknowledge the same destination without restarting the active course.
            if (_travel.Gate is null && command is FlightCommand.WarpTo retry)
            {
                return _navigation.WarpOrder == (retry.TargetId, retry.Range) ? PilotCallResult.None : PilotCallResult.Unsupported;
            }
            if (_travel.Gate is null && command is FlightCommand.WarpToPoint pointRetry)
            {
                return _navigation.WarpOrder == (pointRetry.OrderId, pointRetry.Range) ? PilotCallResult.None : PilotCallResult.Unsupported;
            }
            if (_travel.Gate is null && command is FlightCommand.ActivateAccelerationGate gateRetry)
            {
                return _navigation.WarpOrder == (gateRetry.GateId, 0d) ? PilotCallResult.None : PilotCallResult.Unsupported;
            }
            return PilotCallResult.Unsupported;
        }
        switch (command)
        {
            case FlightCommand.ActivateAccelerationGate gate when missions is not null:
                try
                {
                    if (!CanTarget)
                    {
                        return new PilotCallResult.Failure("Wait until your ship can navigate before activating the gate.");
                    }
                    var destination = missions.Gate(Pilot.PilotId, gate.GateId);
                    return StartWarp(gate.GateId, 0, destination.Destination, accelerationGate: true);
                }
                catch (InvalidDataException error)
                {
                    return new PilotCallResult.Failure(error.Message);
                }
            case FlightCommand.WarpTo warp:
                return StartWarp(warp.TargetId, warp.Range);
            case FlightCommand.WarpToPoint point:
                return StartWarp(point.OrderId, point.Range, point.Position);
            case FlightCommand.RefreshState:
                Snapshot();
                return PilotCallResult.None;
            case FlightCommand.AlignTo align:
                var alignTarget =
                    Scene.Objects.FirstOrDefault(body => body.Id == align.TargetId && body.IsGlobal) ?? NavigationTarget(align.TargetId);
                return alignTarget is null ? PilotCallResult.Unsupported : AlignTo(alignTarget.Position);
            case FlightCommand.AlignToPoint align:
                return AlignTo(align.Position);
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
                    && station > 0
                    && Scene.Objects.FirstOrDefault(o =>
                        o.Id == station
                        && o.CategoryId is (int)InventoryCategory.Station or Eden.Gameplay.Structures.StructureRules.CategoryId
                    )
                        is { } destination:
                if (TravelFailure(CrimewatchTravel.Dock) is { } denied)
                {
                    return denied;
                }
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
                    Pilot = docking.Dock(Pilot.PilotId, ship, station);
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

    private PilotCallResult AlignTo(SpacePosition target)
    {
        if (!InBallpark || !_navigation.AlignTo(Pilot.Position!, target))
        {
            return PilotCallResult.Unsupported;
        }
        var heading = _navigation.LastHeading;
        Update(new DestinyCommand.GotoDirection(Pilot.ShipId, heading.X, heading.Y, heading.Z));
        Update(new DestinyCommand.SetSpeedFraction(Pilot.ShipId, _navigation.SpeedFraction));
        return PilotCallResult.None;
    }
}
