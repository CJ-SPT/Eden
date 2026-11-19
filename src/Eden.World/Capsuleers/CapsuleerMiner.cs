using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
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
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerMinerFactory(
    WorldDatabase database,
    SqliteWorld ships,
    CapsuleerGeometry systems,
    IAsteroidField belts,
    IPilotFitting fitting,
    TimeProvider clock,
    INpcCombat combat,
    INpcSimulation encounters,
    INpcMissiles missiles,
    INpcElectronicWarfare warfare,
    IPilotTackle tackle,
    IPilotHealth health,
    MarketChangeSignal marketSignal,
    CapsuleerDefense defense,
    CapsuleerIndustry industry
)
{
    internal CapsuleerMiner Create(long pilotId, IReadOnlyCollection<long> crew, Func<long, bool> suspended) =>
        new(
            pilotId,
            crew,
            suspended,
            database,
            ships,
            systems,
            belts,
            fitting,
            clock,
            combat,
            encounters,
            missiles,
            warfare,
            tackle,
            health,
            marketSignal,
            defense,
            industry
        );
}

internal sealed partial class CapsuleerMiner(
    long pilotId,
    IReadOnlyCollection<long> crew,
    Func<long, bool> suspended,
    WorldDatabase database,
    SqliteWorld ships,
    CapsuleerGeometry systems,
    IAsteroidField belts,
    IPilotFitting fitting,
    TimeProvider clock,
    INpcCombat combat,
    INpcSimulation encounters,
    INpcMissiles missiles,
    INpcElectronicWarfare warfare,
    IPilotTackle tackle,
    IPilotHealth health,
    MarketChangeSignal marketSignal,
    CapsuleerDefense defense,
    CapsuleerIndustry industry
)
{
    private static readonly CatalogDefinition<bool> s_miningBelt = new(
        "capsuleer-usable-mining-belt",
        CapsuleerProvisioningCatalog.UsableOre
    );
    private const int RetrySeconds = 60;
    private const double ApproachRange = 500;
    private bool _loaded;
    private TackleEffect[] _incoming = [];
    private bool _warpBlocked;
    private CapsuleerOperation _operation = null!;
    private long _threatenedShip;
    private long _nextLook;

    private bool WatchBelt(long elapsed)
    {
        if (
            !database.Settings.Capsuleers.SituationalAwareness
            || Pilot.Position is null
            || _navigation.Warp is not null
            || Work.Activity is not (MinerActivity.ApproachingOre or MinerActivity.Locking or MinerActivity.Mining)
            || elapsed < _nextLook
        )
        {
            return false;
        }
        _nextLook = elapsed + TimeSpan.FromSeconds(PilotAwareness.LookSeconds(pilotId)).Ticks;
        var danger = defense.ObserveCivilianDanger(Pilot, _navigation.Velocity, elapsed);
        if (danger == CivilianDanger.Clear)
        {
            return false;
        }
        if (danger == CivilianDanger.Escape || PilotAwareness.Cautious(pilotId) || Work.Activity != MinerActivity.Mining)
        {
            return true;
        }
        _work = Work with { LeaveAfterCycle = true };
        return false;
    }

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
    internal DockedPilot? PreparationPilot => _pilot;
    private InventoryItem? _laser;
    private double _miningReach;
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
        RefreshCorporation();
        return _pilot is null ? null : Observation(includeHealth);
    }

    internal CapsuleerJob Resume(CapsuleerJob job, long elapsed, long pausedAt)
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
        long delay = Math.Max(0, elapsed - Math.Max(Work.ActiveTicks, pausedAt));
        var resumed = CapsuleerPauseRules.Resume(Work, delay);
        if (
            Work.WarpReadyAt == 0
            && Pilot.Position is not null
            && _target is not null
            && _navigation.Warp is null
            && _navigation.WarpDestination is not null
        )
        {
            var navigation = NavigationState.Restore(resumed.Navigation, Pilot.Flight);
            long ready = CapsuleerWarpRules.Begin(navigation, new(Pilot, _target, Work.Stamp, ApproachRange), elapsed);
            resumed = resumed with { WarpReadyAt = ready, Navigation = navigation.Capture() };
        }
        long earliest = checked(elapsed + 1);
        var next = job.Next(Math.Max(earliest, checked(job.DueTicks + delay)), job.Action);
        using CapsuleerOperation operation = new(database, job, marketSignal);
        if (
            resumed.Activity == MinerActivity.Mining
            && resumed.PaidUntil != 0
            && resumed.DueTicks > elapsed
            && TryRebasePaidCycle(operation, resumed, elapsed, out var paid)
        )
        {
            resumed = paid;
        }
        operation.Commit(next, new MinerCheckpoint(pilotId, resumed), null);
        _navigation = NavigationState.Restore(resumed.Navigation, Pilot.Flight);
        _work = resumed;
        return next;
    }

    private bool TryRebasePaidCycle(CapsuleerOperation operation, MinerProgress work, long elapsed, out MinerProgress paid)
    {
        paid = work;
        if (
            !operation
                .World.Set<Eden.Data.World.Inventory.OwnedItem>()
                .Any(i => i.Id == work.ModuleId && i.OwnerId == pilotId && i.LocationId == Pilot.ShipId && i.Online)
        )
        {
            return false;
        }
        long completes = checked(Now + work.DueTicks - elapsed);
        var cooldown =
            operation.World.Set<ModuleCooldown>().Find(work.ModuleId)
            ?? operation.World.Add(new ModuleCooldown { ModuleId = work.ModuleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = completes;
        paid = work with { PaidUntil = completes };
        return true;
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
            if (!live.Economy.ActiveShip(pilotId, Pilot.ShipId))
            {
                Load();
            }
        }
        RefreshCorporation();
        ObserveDanger(destinyStamp);
        if (Pilot.Position is not null)
        {
            RefreshTackle();
        }
        var defended = defense.Advance(Pilot, _navigation, Work.Defense, Now, destinyStamp, _obstacles, escapeFirst: true);
        _pilot = defended.Pilot;
        _warpBlocked = defended.WarpBlocked ?? _warpBlocked;
        _work = Work with { Defense = defended.State, LastMotionStamp = defended.HoldsNavigation ? destinyStamp : Work.LastMotionStamp };
        if (defended.Escape || WatchBelt(job.DueTicks))
        {
            ObserveAggression(Pilot.ShipId);
        }
        if (
            database.Settings.Capsuleers.SituationalAwareness
            && Pilot.Position is not null
            && _navigation.Warp is null
            && CapsuleerMiningRules.WorkingField(Work)
            && CrewDanger(job.DueTicks)
        )
        {
            _work = Work with { Activity = MinerActivity.ReadyToReturn, LeaveAfterCycle = false };
        }
        if (
            Interlocked.Exchange(ref _threatenedShip, 0) == Pilot.ShipId
            && Pilot.Position is not null
            && Work.Activity != MinerActivity.Returning
        )
        {
            _work = Work with
            {
                Activity = MinerActivity.ReadyToReturn,
                LeaveAfterCycle = false,
                AvoidedBelts =
                    Work.WorkingBelt > 0 && database.Settings.Capsuleers.SituationalAwareness
                        ? PilotAwareness.Remember(Work.AvoidedBelts, Work.WorkingBelt, job.DueTicks)
                        : Work.AvoidedBelts,
            };
        }
        var beforePilot = Pilot;
        var before = Work;
        var beforeLaser = _laser;
        double beforeReach = _miningReach;
        var beforeTarget = _target;
        var beforeObjects = _objects;
        var beforeObstacles = _obstacles;
        long stepTicks = job.DueTicks;
        _work = Work with { ActiveTicks = stepTicks, Stamp = CapsuleerScheduleRules.Stamp(stepTicks, Work.ClockOffsetTicks) };
        _operation = new(database, job, marketSignal);
        try
        {
            if (defended.NewThreat)
            {
                RegionalConflictStore.Attack(_operation, database, Pilot, Work.Defense, Work.WorkingBelt, Work.ActiveTicks);
            }
            if (_resumeCount != resumes && Work.Activity == MinerActivity.Mining && Work.PaidUntil != 0 && Work.DueTicks > activeNow)
            {
                if (!TryRebasePaidCycle(_operation, Work, activeNow, out var paid))
                {
                    throw new InvalidDataException("The funded mining laser is no longer fitted to this pilot's ship.");
                }
                _work = paid;
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
            _miningReach = beforeReach;
            _operation = new(database, job, marketSignal);
            _navigation.AdoptHull();
            _work = Work with
            {
                ActiveTicks = stepTicks,
                Stamp = CapsuleerScheduleRules.Stamp(stepTicks, Work.ClockOffsetTicks),
                Activity = MinerActivity.Waiting,
                DueStamp = CapsuleerScheduleRules.Stamp(stepTicks, Work.ClockOffsetTicks) + RetrySeconds,
                DueTicks = checked(stepTicks + TimeSpan.FromSeconds(RetrySeconds).Ticks),
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
                _miningReach = beforeReach;
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
            _miningReach = beforeReach;
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
            Live?.CombatForSystem(pilot.SystemId)?.PublishPilotPosition(pilot.ShipId, pilot.SystemId, position);
        }
    }

    private CapsuleerJob Commit(CapsuleerJob job)
    {
        _work = Work with { Navigation = _navigation.Capture() };
        var schedule = Schedule();
        var next = job.Next(schedule.DueTicks, schedule.Action);
        _operation.Commit(next, new MinerCheckpoint(pilotId, Work), null);
        return next;
    }

    private CapsuleerSchedule Schedule()
    {
        var schedule = CapsuleerScheduleRules.Next(Work, Pilot.Position is not null);
        return schedule with { DueTicks = CapsuleerDefenseRules.NextDeadline(Work.Defense, Now, Work.ActiveTicks, schedule.DueTicks) };
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
        live.CombatForSystem(Pilot.SystemId)?.PublishPilotPosition(Pilot.ShipId, Pilot.SystemId, tick.Position);
        live.MinerCheckpoints[pilotId] = new(pilotId, Work);
        live.ObjectsChanged();
        return true;
    }

    private MinerObservation Observation(bool includeHealth)
    {
        using var system = includeHealth ? Live?.UseSystem(Pilot.SystemId) : null;
        return new(Pilot, Work, _laser, Work.WaitingReason) { Health = includeHealth ? health.Read(pilotId, Now) : null };
    }

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
        using var transaction = new WorldActionScope(database, new(pilots: [pilotId]));
        var world = transaction.World;
        var row = world.Set<SavedCapsuleerCommunity>().AsNoTracking().SingleOrDefault(c => c.PilotId == pilotId);
        if (row?.PilotId is long id)
        {
            long account = world.Pilots.Where(p => p.Id == id).Select(p => p.AccountId).Single();
            _pilot = ships.FindPilot(world, account, id)!;
            _seed = row.Seed;
            _work = Live?.MinerCheckpoints.GetValueOrDefault(pilotId)?.Work ?? MinerCheckpoint.Read(row.Progress);
            var previous = _work;
            long anchor =
                previous?.ActiveTicks
                ?? (
                    (
                        Live?.CapsuleerJobs.GetValueOrDefault(pilotId)?.DueTicks
                        ?? world.Set<SavedCapsuleerJob>().Single(j => j.PilotId == pilotId).DueTicks
                    ) - TimeSpan.TicksPerSecond
                );
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
                    DeepCoreProvisioned = previous?.DeepCoreProvisioned == true,
                    Mercoxit = Pilot.Position is null ? null : previous?.Mercoxit,
                    ActiveTicks = anchor,
                    ClockOffsetTicks = previous?.ClockOffsetTicks ?? anchor,
                    Stamp = previous?.Stamp ?? 0,
                };
            }
            // Older mid-shift checkpoints recorded only the rock, including after its last unit was mined.
            if (Work.WorkingBelt == 0 && Work.TargetId > 0 && world.Set<WorldAsteroid>().Find(Work.TargetId) is { BeltId: > 0 } deposit)
            {
                _work = Work with { WorkingBelt = deposit.BeltId };
            }
            _incoming = [];
            _warpBlocked = false;
            _navigation = NavigationState.Restore(Work.Navigation, Pilot.Flight);
            using CatalogDbContext catalog = new(database.CatalogPath);
            _laser = PilotInventoryStore.List(world, catalog, id, Pilot.ShipId).FirstOrDefault(i => i.Id == Work.ModuleId);
            _miningReach = _laser is null ? 0 : MiningReach(world, catalog, _laser);
            transaction.Commit();
            transaction.Dispose();
            RefreshObjects();
            _target = _objects.FirstOrDefault(o => o.Id == Work.TargetId);
            if (Work.WorkingBelt == 0 && _target is { GroupId: AsteroidDefinitions.BeltGroup } belt)
            {
                _work = Work with { WorkingBelt = belt.Id };
            }
        }
        _loaded = true;
    }

    private void RefreshTackle()
    {
        // A demoted system discards its read views. Known-absent hostility is still valid
        // until either owning table commits a change; exact active effects keep their normal reads.
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
            case MinerActivity.FlyingToGate:
            case MinerActivity.Returning:
                Travel();
                break;
            case MinerActivity.Jumping when Work.ActiveTicks >= Work.DueTicks:
                CompleteExpeditionJump();
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
                    JoinCorporationAtDock();
                    _work = Work with { Activity = MinerActivity.Docked };
                }
                else
                {
                    ReturnHome();
                }
                break;
        }
    }

    internal void RefreshCorporation()
    {
        if (
            _pilot is not null
            && Live is { } live
            && live.Economy.CorporationAffiliation(pilotId) != (_pilot.CorpId, _pilot.Identity.AllianceId)
        )
        {
            var current = ships.FindPilot(CapsuleerCommunityStore.MinerAccountId, pilotId)!;
            _pilot = _pilot with { Identity = current.Identity };
        }
    }

    private void JoinCorporationAtDock()
    {
        if (
            Live is not null
            && _operation.World is EconomyTransaction world
            && CapsuleerCorporations.Place(world, _operation.Catalog, database, pilotId, Now)
        )
        {
            _pilot = ships.FindPilot(world, CapsuleerCommunityStore.MinerAccountId, pilotId)!;
        }
    }

    private void PrepareTrip()
    {
        if (Pilot.ShipGroupId == FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Waiting for a replacement mining ship.");
        }
        JoinCorporationAtDock();
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
        FactorySupplyRequest? supply = null;
        if (
            Pilot.CorpId >= Eden.Gameplay.Corporations.CorporationRules.FirstCorporationId
            && _operation.World is EconomyTransaction supplyWorld
        )
        {
            _operation.MarketChanged |= industry.Produce(supplyWorld, _operation.Catalog, Pilot, Now, out supply);
        }
        _work = Work with { FactorySupply = supply };
        var expedition = PrepareMercoxitExpedition(aboard);
        var laserType = expedition is null
            ? CapsuleerMiningRules.Laser((CapsuleerMiningHull)Pilot.ShipTypeId)
            : MiningLaserType.DeepCoreMiningLaserI;
        _laser = aboard.FirstOrDefault(i => i.TypeId == (int)laserType && InventoryDefinitions.IsHighSlot(i.FlagId));
        if (_laser is null)
        {
            var kit =
                PilotInventoryStore
                    .List(_operation.World, _operation.Catalog, Pilot.PilotId, Pilot.StationId)
                    .FirstOrDefault(i => i.TypeId == (int)laserType)
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
        _miningReach = MiningReach(_operation.World, _operation.Catalog, _laser);
        RefreshObjects();
        _work = Work with { StrippedBelts = Work.StrippedBelts.Where(b => b.Until > Work.ActiveTicks).ToArray() };
        if (expedition is not null && expedition.Systems.Length > 1)
        {
            LaunchMiningShip(0);
            ContinueExpedition(expedition);
            return;
        }
        _work = Work with { Mercoxit = expedition };
        var field = NextBelt(out long retryAt, out bool knownBelts);
        if (field is null)
        {
            _work = Work with
            {
                Activity = MinerActivity.Waiting,
                DueTicks = retryAt,
                DueStamp = CapsuleerScheduleRules.Stamp(retryAt, Work.ClockOffsetTicks),
                WaitingReason =
                    !knownBelts ? "Waiting for a known asteroid belt."
                    : Work.StrippedBelts.Length == 0 ? "Sheltering until the belt is clear."
                    : "Waiting to revisit stripped belts.",
            };
            return;
        }
        LaunchMiningShip(field.Id);
        StartCourse(field, MinerActivity.FlyingToBelt);
    }

    private void LaunchMiningShip(long belt)
    {
        var launch = StationUndocking.Launch(Pilot.ShipId, Pilot.Home, Pilot.Flight);
        SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, launch.Position, Pilot.SystemId, Now);
        _operation.TravelChanged = true;
        _pilot = Pilot with { Position = launch.Position };
        _navigation.AdoptHull();
        _navigation.SetVelocity(launch.Velocity);
        _work = Work with { ModuleId = _laser!.Id, Cycles = 0, Trips = Work.Trips + 1, WorkingBelt = belt, LeaveAfterCycle = false };
    }

    private StaticSpaceObject? NextBelt(out long retryAt, out bool knownBelts)
    {
        var fields = _objects
            .Where(o =>
                o.GroupId == AsteroidDefinitions.BeltGroup
                && (
                    Work.Mercoxit is null
                        ? database.Catalog.Definition(s_miningBelt, checked((int)o.Id))
                        : CapsuleerTrafficRoutes.MercoxitBelt(_operation.Catalog, o.Id)
                )
            )
            .OrderBy(o => o.Id)
            .ToArray();
        knownBelts = fields.Length > 0;
        if (!knownBelts)
        {
            // The docked industry action can commit while there is no mining trip available.
            retryAt = checked(Work.ActiveTicks + TimeSpan.FromSeconds(RetrySeconds).Ticks);
            return null;
        }
        var peers = Crewmates().ToArray();
        long elapsed = Work.ActiveTicks;
        BeltMemory[] stripped = [.. Work.StrippedBelts, .. peers.SelectMany(work => work.StrippedBelts)];
        BeltMemory[] avoided = database.Settings.Capsuleers.SituationalAwareness
            ? [.. Work.AvoidedBelts, .. peers.SelectMany(work => work.AvoidedBelts)]
            : [];
        var occupied = peers
            .Where(CapsuleerMiningRules.WorkingField)
            .Select(work => (long?)work.WorkingBelt)
            .Where(belt =>
                !stripped.Any(b => b.BeltId == belt && b.Until > elapsed) && !avoided.Any(b => b.BeltId == belt && b.Until > elapsed)
            )
            .Min();
        if (occupied is long belt && fields.FirstOrDefault(field => field.Id == belt) is { } shared)
        {
            retryAt = 0;
            return shared;
        }
        return PilotAwareness.NextMiningBelt(fields, unchecked(_seed + Work.Trips), stripped, avoided, elapsed, out retryAt);
    }

    private void StartCourse(StaticSpaceObject target, MinerActivity activity)
    {
        _target = target;
        _work = Work with { TargetId = target.Id, Activity = activity };
        if (_warpBlocked)
        {
            throw new InvalidDataException("Warp disruption prevents the miner from leaving.");
        }
        _work = Work with
        {
            WarpReadyAt = CapsuleerWarpRules.Begin(_navigation, new(Pilot, target, Work.Stamp, ApproachRange), Work.ActiveTicks),
        };
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
        if (_target is not null)
        {
            _work = Work with
            {
                WarpReadyAt = CapsuleerWarpRules.Advance(
                    _navigation,
                    new(Pilot, _target, Work.Stamp, ApproachRange),
                    Work.WarpReadyAt,
                    Work.ActiveTicks,
                    _warpBlocked
                ),
            };
        }
        if (_target is not null && Work.ActiveTicks >= Work.WarpReadyAt && (_navigation.WarpDestination is not null || Work.EscapingBubble))
        {
            bool escaping = defense.NavigateField(
                Pilot,
                _navigation,
                _target,
                Work.EscapingBubble,
                Work.Stamp,
                Now,
                _warpBlocked,
                ApproachRange
            );
            _work = Work with { EscapingBubble = escaping };
        }
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
            Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, _operation.Catalog, Pilot.ShipId);
        }
        if (_operation.HasWorld)
        {
            SqliteWorld.SetPosition(_operation.World, _operation.Catalog, Pilot.ShipId, tick.Position, Pilot.SystemId, Now);
        }
        else if (Live?.Combat is { } residentCombat)
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
        if (
            _navigation.Warp is not null
            || _navigation.WarpDestination is not null
            || Work.EscapingBubble
            || Work.WarpReadyAt > Work.ActiveTicks
        )
        {
            return;
        }
        if (Work.Activity == MinerActivity.FlyingToGate)
        {
            ArriveAtExpeditionGate();
            return;
        }
        if (Work.Activity == MinerActivity.Returning)
        {
            if (Distance(_target!) <= DockingDefinitions.MaximumRange)
            {
                _pilot = ships.Dock(
                    _operation.World,
                    _operation.Catalog,
                    Pilot.PilotId,
                    Pilot.ShipId,
                    Work.Mercoxit?.StationId ?? Pilot.StationId
                );
                JoinCorporationAtDock();
                Eden.World.Combat.Npcs.NpcCombatStore.Disengage(_operation.World, _operation.Catalog, Pilot.ShipId);
                _navigation.Dock();
                _operation.TravelChanged = true;
                _work = Work with { Activity = MinerActivity.Docked, TargetId = 0, Mercoxit = null };
            }
            else
            {
                Follow(_target!);
            }
            return;
        }
        // Finish a short subwarp leg before judging whether the destination field is stripped.
        if (_target is { GroupId: AsteroidDefinitions.BeltGroup } belt && Distance(belt) > AsteroidDefinitions.OuterFieldRadius)
        {
            Follow(belt);
            return;
        }
        FindOre();
    }

    private void FindOre()
    {
        var available = _operation.HasWorld
            ? AsteroidFieldStore.List(_operation.World, _operation.Catalog, Pilot.SystemId)
            : belts.List(Pilot.SystemId);
        var rock = SelectRock(available);
        if (rock is null)
        {
            if (Work.WorkingBelt > 0)
            {
                // Keep the whole outbound circuit, including systems with more than six belts.
                _work = Work with
                {
                    StrippedBelts =
                    [
                        .. Work.StrippedBelts.Where(b => b.BeltId != Work.WorkingBelt),
                        new(Work.WorkingBelt, checked(Work.ActiveTicks + PilotAwareness.MemoryTicks)),
                    ],
                };
            }
            var next = NextBelt(out _, out _);
            if (next is null)
            {
                ReturnHome();
                return;
            }
            _work = Work with { WorkingBelt = next.Id };
            StartCourse(next, MinerActivity.FlyingToBelt);
            return;
        }
        _target = rock;
        _navigation.Follow(Pilot.ShipId, Work.Stamp, Pilot.Position!, Pilot.Flight.ShipRadius, rock, _miningReach, false, true);
        _work = Work with { Activity = MinerActivity.ApproachingOre, TargetId = rock.Id };
    }

    private void Approach()
    {
        if (_target is null)
        {
            FindOre();
            return;
        }
        var target = _target;
        if (_laser is null)
        {
            throw new InvalidDataException("Mining target or fitted laser unavailable.");
        }
        if (Distance(target) > _miningReach)
        {
            Move();
            if (Distance(target) > _miningReach)
            {
                return;
            }
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
        if (world.Set<WorldAsteroid>().Find(Work.TargetId) is not { Quantity: > 0 })
        {
            FindOre();
            return;
        }
        long now = Now;
        var start = PilotMiningStore.Begin(world, catalog, Pilot.PilotId, Work.ModuleId, Work.TargetId, now);
        long duration = checked((long)Math.Ceiling(start.Laser.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        int seconds = checked((int)Math.Ceiling(start.Laser.DurationMilliseconds / 1000));
        var next = Work with
        {
            Activity = MinerActivity.Mining,
            DueStamp = Work.Stamp + seconds,
            CycleSeconds = seconds,
            DueTicks = checked(Work.ActiveTicks + duration),
            PaidUntil = checked(now + duration),
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
            StopPaidCycle();
            if (Work.LeaveAfterCycle && database.Settings.Capsuleers.SituationalAwareness)
            {
                LeaveThreatenedBelt();
                return;
            }
            FindOre();
            return;
        }
        var random = new Random(unchecked(_seed + Work.DueStamp));
        MiningRolls rolls = new(random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble());
        var result = PilotMiningStore.Collect(world, catalog, Pilot.PilotId, Work.ModuleId, Work.TargetId, new(1, true, rolls), Now);
        _work = Work with { Activity = MinerActivity.Locking, Cycles = checked(Work.Cycles + 1), PaidUntil = 0 };
        if (Work.LeaveAfterCycle && database.Settings.Capsuleers.SituationalAwareness)
        {
            LeaveThreatenedBelt();
        }
        else if (result.CargoFull)
        {
            _work = Work with { Activity = MinerActivity.ReadyToReturn };
        }
        else if (result.Depleted)
        {
            FindOre();
        }
    }

    private void LeaveThreatenedBelt() =>
        _work = Work with
        {
            Activity = MinerActivity.ReadyToReturn,
            LeaveAfterCycle = false,
            AvoidedBelts = PilotAwareness.Remember(Work.AvoidedBelts, Work.WorkingBelt, Work.ActiveTicks),
        };

    private void StopPaidCycle()
    {
        if (Work.PaidUntil != 0)
        {
            PilotMiningStore.Stop(_operation.World, Pilot.PilotId, Work.ModuleId, Work.PaidUntil);
            _work = Work with { PaidUntil = 0 };
        }
    }

    private void ReturnHome()
    {
        StopPaidCycle();
        if (_objects.Count == 0)
        {
            RefreshObjects();
        }
        if (Work.Mercoxit is { } expedition)
        {
            ContinueExpedition(expedition with { Returning = true });
            return;
        }
        StartCourse(_objects.Single(o => o.Id == Pilot.StationId), MinerActivity.Returning);
    }
}
