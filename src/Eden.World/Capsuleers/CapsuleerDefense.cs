using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Targeting;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerDefenseResult(
    DockedPilot Pilot,
    CapsuleerDefenseState? State,
    bool HoldsNavigation,
    bool Escape,
    bool NewThreat,
    bool ResumeNavigation = false,
    bool? WarpBlocked = null
);

[Injectable(InjectionType.Singleton)]
internal sealed partial class CapsuleerDefense(
    WorldDatabase database,
    IPilotFitting fitting,
    IPilotInventory inventory,
    IPilotCombat weapons,
    IPilotMissiles missiles,
    IPilotDrones drones,
    IPilotCombatPresence presence,
    IPilotInterdiction interdiction,
    IPilotTargetLocks locks,
    IPilotGateCloak gateCloaks,
    IPilotTackle tackle,
    IPilotElectronicWarfare warfare,
    INpcElectronicWarfare npcWarfare,
    IPilotCapacitorWarfare capacitorWarfare,
    IPilotPropulsion propulsion,
    IPilotHardening hardeners,
    IPilotShieldBoosting shields,
    IPilotLocalRepairing repairs,
    IPilotCapacitorBoosting boosters,
    IPilotSupportModules support,
    IPilotSignatureSuppression suppression,
    IPilotAssaultDamageControls damageControls,
    IPilotAmmunition ammunition,
    IPilotOverloading overloading,
    IPilotShipHeat shipHeat,
    IPilotHealth health
)
{
    internal long LaunchProbe(DockedPilot pilot, long launcher, long now)
    {
        if (pilot.Position is not { } position || interdiction.Resolve(pilot.ShipId, pilot.SystemId, position, position, now).Blocked)
        {
            return TimeSpan.FromSeconds(15).Ticks;
        }
        try
        {
            var launch = interdiction.Launch(pilot.PilotId, pilot.ShipId, launcher, now);
            // The ordinary launcher owner enforces the charge, capacitor and reactivation costs.
            return Math.Max(TimeSpan.FromSeconds(30).Ticks, launch.ReadyAt - now);
        }
        catch (InvalidDataException)
        {
            return TimeSpan.FromMinutes(1).Ticks;
        }
    }

    internal bool NavigateField(
        DockedPilot pilot,
        NavigationState course,
        StaticSpaceObject target,
        bool escaping,
        int stamp,
        long now,
        bool pointed,
        double standOff = 0
    )
    {
        if (course.Warp is not null || course.WarpDestination is null && !escaping)
        {
            return false;
        }
        var origin = pilot.Position!;
        if (course.WarpDestination is null && !pointed)
        {
            course.StartWarp(pilot.ShipId, pilot.Flight, origin, target, standOff, stamp);
        }
        var outcome = interdiction.Resolve(pilot.ShipId, pilot.SystemId, origin, course.WarpDestination ?? target.Position, now);
        if (outcome.EscapePoint is { } escape)
        {
            course.CancelAlignment();
            course.AlignTo(origin, escape);
            return true;
        }
        if (course.WarpDestination is { } destination && outcome.Destination != destination)
        {
            course.RedirectWarp(outcome.Destination);
        }
        if (escaping && course.WarpDestination is null)
        {
            course.Follow(pilot.ShipId, stamp, origin, pilot.Flight.ShipRadius, target, standOff, false, true);
        }
        return false;
    }

    internal CapsuleerGateCamps Camps { get; } = new();

    private const double MinimumCombatRange = 500;

    private const int RetrySeconds = 30;
    private static readonly CatalogDefinition<double> s_observedSignature = new(
        "capsuleer-observed-signature",
        (catalog, type) => Math.Max(0, catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.SignatureRadius))
    );

    internal PilotSighting[] Observe(DockedPilot pilot, long elapsed) =>
        pilot.Position is null
            ? []
            : presence
                .Observe(pilot.SystemId, pilot.ShipId, pilot.Position, elapsed / TimeSpan.TicksPerSecond)
                .Select(contact => contact with { SignatureEstimate = database.Catalog.Definition(s_observedSignature, contact.TypeId) })
                .ToArray();

    internal CivilianDanger ObserveCivilianDanger(DockedPilot pilot, SpacePosition velocity, long elapsed, StaticSpaceObject? gate = null)
    {
        if (
            TrafficRules.SecurityBand(database.Catalog.Definition(CapsuleerProvisioningCatalog.Security, pilot.SystemId))
            == TrafficSecurityBand.High
        )
        {
            return CivilianDanger.Clear;
        }
        int? group =
            database.Settings.Capsuleers.RegionalConflicts && LiveWorld.Find(database.Path) is { } live
                ? RegionalConflictStore.GroupForPilot(live, database.Catalog, pilot.PilotId)
                : null;
        return PilotAwareness.Assess(pilot, velocity, Observe(pilot, elapsed), group, gate);
    }

    internal CapsuleerAttacker? PatrolTarget(DockedPilot pilot, long target, SpacePosition anchor, long now) =>
        LiveCombatRuntime.Find(database.Path)?.PatrolTarget(pilot, target, anchor, presence, now);

    internal CapsuleerDefenseResult Advance(
        DockedPilot pilot,
        NavigationState navigation,
        CapsuleerDefenseState? saved,
        long now,
        int stamp,
        IReadOnlyList<StaticSpaceObject> obstacles,
        bool forceRetreat = false,
        bool canFight = true,
        bool escapeFirst = false,
        CapsuleerAttacker? proactive = null,
        PvpRole? pvpRole = null,
        RoamingGang? gang = null
    )
    {
        return LiveWorld.Execute(
            database.Path,
            () =>
                AdvanceOwned(
                    pilot,
                    navigation,
                    saved,
                    now,
                    stamp,
                    obstacles,
                    forceRetreat,
                    canFight,
                    escapeFirst,
                    proactive,
                    pvpRole,
                    gang
                ),
            nameof(Advance)
        );
    }

    private CapsuleerDefenseResult AdvanceOwned(
        DockedPilot pilot,
        NavigationState navigation,
        CapsuleerDefenseState? saved,
        long now,
        int stamp,
        IReadOnlyList<StaticSpaceObject> obstacles,
        bool forceRetreat = false,
        bool canFight = true,
        bool escapeFirst = false,
        CapsuleerAttacker? proactive = null,
        PvpRole? pvpRole = null,
        RoamingGang? gang = null
    )
    {
        var world = LiveWorld.Find(database.Path);
        using var ownership = world?.Enter();
        var runtime = LiveCombatRuntime.Find(database.Path);
        var state = saved?.ShipId == pilot.ShipId ? saved : new CapsuleerDefenseState { ShipId = pilot.ShipId };
        if (saved is not null && saved.ShipId != pilot.ShipId)
        {
            locks.RemoveShip(saved.ShipId);
            overloading.Clear(pilot.PilotId, saved.ShipId);
        }
        if (pilot.Position is null || navigation.Warp is not null || pilot.ShipGroupId == FleetDefinitions.CapsuleGroup || !canFight)
        {
            Stop(pilot.PilotId, state);
            if ((pilot.Position is null || navigation.Warp is not null) && drones.HasControlled(pilot.PilotId, pilot.ShipId))
            {
                drones.Release(pilot.PilotId, pilot.ShipId);
            }
            return new(pilot, null, false, false, false);
        }
        var cycles = state.Cycles.ToArray();
        for (int index = 0; index < cycles.Length; index++)
        {
            var cycle = cycles[index];
            if (
                cycle.Effect == DogmaEffect.WarpCoreStabilizer
                && cycle.Tackle is { } core
                && cycle.StartedAt + core.DurationMilliseconds * TimeSpan.TicksPerMillisecond <= now
            )
            {
                StopModule(pilot.PilotId, pilot.ShipId, cycle);
                cycles[index] = cycle with { Effect = DogmaEffect.Online };
            }
        }
        state = state with { Cycles = cycles };
        var incoming = tackle.Incoming(pilot.ShipId);
        bool blocked = incoming.Sum(effect => effect.Module.Strength) > 0;
        var attackers = runtime is null ? PlayerAttackers(pilot, now) : runtime.DefenseAttackers(pilot.PilotId, presence, now);
        var jams = npcWarfare.Incoming(pilot.ShipId);
        var npcAttackers = runtime?.DefenseNpcAttackers(pilot.PilotId, now, jams) ?? [];
        var candidates = attackers
            .Where(attacker => runtime?.IsCapsuleerCorporationAlly(pilot.PilotId, attacker.PilotId) != true)
            .Concat(npcAttackers)
            .Select(attacker =>
                attacker with
                {
                    Tackling =
                        attacker.Tackling || incoming.Any(effect => effect.SourceShipId == attacker.ShipId && effect.Module.Strength > 0),
                }
            )
            .ToArray();
        var target = proactive ?? CapsuleerDefenseRules.Select(candidates);
        if (target is null)
        {
            Stop(pilot.PilotId, state);
            var recallState = Recall(pilot, state, now);
            if (drones.HasControlled(pilot.PilotId, pilot.ShipId))
            {
                drones.Advance(pilot.PilotId, now, stamp);
                if (recallState.RecallUntil > now && drones.HasControlled(pilot.PilotId, pilot.ShipId))
                {
                    navigation.CancelAlignment();
                    navigation.Stop();
                    if (recallState.MotionStamp != stamp)
                    {
                        var tick = navigation.Advance(pilot.ShipId, stamp, pilot.Flight, pilot.Position, _ => null, obstacles);
                        pilot = pilot with { Position = tick.Position };
                        runtime?.PublishPilotPosition(pilot.ShipId, pilot.SystemId, tick.Position);
                    }
                    return new(pilot, recallState with { TargetId = 0, Cycles = [], Locks = [], MotionStamp = stamp }, true, false, false);
                }
            }
            return new(pilot, null, false, false, false, saved?.TargetId > 0 || saved?.RecallUntil > 0);
        }
        var profile = Profile(pilot.PilotId);
        // Initialization clears sustained effects for all pilots. Retain paid deadlines without advertising
        // a bonus that no longer exists or charging another cycle before it is due.
        state = state with
        {
            Cycles = state
                .Cycles.Select(cycle =>
                    IsSustained(cycle.Effect) && !profile.Modules.Any(module => module.Item.Id == cycle.ModuleId && module.Active)
                        ? cycle with
                        {
                            Effect = DogmaEffect.Online,
                        }
                        : cycle
                )
                .ToArray(),
        };
        var live = runtime?.DefenseShip(pilot.PilotId, now);
        var attributes = profile.ShipAttributes;
        blocked = attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus) > 0;
        var currentHealth = health.Read(pilot.PilotId, now);
        bool hasDamage =
            profile.Modules.Any(module =>
                module.Item.GroupId != InterdictionRules.LauncherGroup
                && IsWeapon(module.Effect)
                && !state.Cycles.Any(cycle => cycle.ModuleId == module.Item.Id && cycle.Unavailable)
            )
            || drones.HasControlled(pilot.PilotId, pilot.ShipId)
            || !state.DroneLaunchAttempted
                && DroneRules.SupportsHull(pilot.ShipTypeId)
                && attributes.GetValueOrDefault(DogmaAttributeId.DroneBandwidth) > 0;
        if (live is not null)
        {
            bool turrets = live.Turrets.Any(pair =>
                pair.Value.Definition.Error is null && !state.Cycles.Any(cycle => cycle.ModuleId == pair.Key && cycle.Unavailable)
            );
            bool launchers = profile.Modules.Any(module =>
                module.Item.GroupId != InterdictionRules.LauncherGroup
                && module.Effect is DogmaEffect.UseMissiles or DogmaEffect.AutoTargetingMissile
                && !state.Cycles.Any(cycle => cycle.ModuleId == module.Item.Id && cycle.Unavailable)
            );
            hasDamage =
                turrets
                || launchers
                || drones.HasControlled(pilot.PilotId, pilot.ShipId)
                || !state.DroneLaunchAttempted
                    && DroneRules.SupportsHull(pilot.ShipTypeId)
                    && attributes.GetValueOrDefault(DogmaAttributeId.DroneBandwidth) > 0;
        }
        bool combatShip = profile.Combat && !escapeFirst;
        bool hasSupport = profile.HasSupport && (gang is not null || !profile.Modules.Any(module => IsWeapon(module.Effect)));
        bool exhausted =
            !target.Npc
            && !hasSupport
            && live is not null
            && PvpTactics.Exhausted(
                CapsuleerDefenseRules.Fraction(
                    ChargedResource.Recharged(
                        live.Capacitor,
                        live.CapacitorCapacity,
                        live.CapacitorRechargeMilliseconds,
                        Math.Max(0, (now - live.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
                    ),
                    live.CapacitorCapacity
                ),
                profile.RattingWeapons
            );
        bool retreat =
            forceRetreat
            || state.Retreating
            || CapsuleerDefenseRules.Retreat(currentHealth, hasDamage || hasSupport)
            || !combatShip
            || exhausted;
        if (retreat && blocked)
        {
            target = CapsuleerDefenseRules.Select(candidates.Where(attacker => attacker.Tackling).ToArray()) ?? target;
        }
        bool alliedTackle =
            gang is not null
            && tackle
                .Incoming(target.ShipId)
                .Any(effect =>
                    effect.SourceShipId != pilot.ShipId
                    && effect.ExpiresAt > now
                    && effect.Module.Strength > 0
                    && gang.Members.Any(member => member.ShipId == effect.SourceShipId && member.Status == RoamingGangMemberStatus.Active)
                );
        bool offensive = CapsuleerDefenseRules.Offensive(combatShip, retreat, blocked);
        long attackedAt = candidates.Select(attacker => attacker.AttackedAt).DefaultIfEmpty(0).Max();
        bool newThreat = attackedAt > state.LastAttackAt;
        state = state with { TargetId = target.ShipId, LastAttackAt = Math.Max(state.LastAttackAt, attackedAt), Retreating = retreat };
        var moduleTargets = SupportTargets(pilot, profile, state, target, live, gang, now, stamp, retreat);
        bool CanLock(CapsuleerAttacker contact) =>
            pilot.Position.DistanceTo(contact.Position) - pilot.Flight.ShipRadius - contact.Radius
                <= attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange)
            && NpcEwarRules.CanTarget(jams, contact.ShipId)
            && PlayerEwarRules.CanTarget(warfare.Incoming(pilot.ShipId), contact.ShipId);
        var planned = new[] { target }
            .Concat(moduleTargets.Values.OfType<CapsuleerAttacker>())
            .DistinctBy(contact => contact.ShipId)
            .Where(CanLock)
            .Take(Math.Max(0, checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets))))
            .ToArray();
        // A stationary arrival can watch under gate cloak until it chooses to acquire a target.
        if (planned.Length > 0 && world?.Economy.Peek<SpatialNavigationState>(pilot.ShipId)?.GateCloakUntil > now)
        {
            gateCloaks.Cancel(pilot.PilotId, pilot.ShipId);
        }
        foreach (var previous in state.Locks.Where(scan => !planned.Any(contact => contact.ShipId == scan.TargetId)))
        {
            locks.Remove(pilot.ShipId, previous.TargetId);
        }
        List<CapsuleerLock> scans = [];
        foreach (var contact in planned)
        {
            var scan = state.Locks.FirstOrDefault(scan => scan.TargetId == contact.ShipId);
            if (scan is null)
            {
                double seconds = TargetingDefinitions.LockSeconds(
                    attributes.GetValueOrDefault(DogmaAttributeId.ScanResolution),
                    contact.Signature
                );
                if (double.IsFinite(seconds) && seconds > 0)
                {
                    scan = new(contact.ShipId, checked(now + (long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond)));
                }
            }
            if (scan is null)
            {
                continue;
            }
            scans.Add(scan);
            if (scan.ReadyAt <= now)
            {
                locks.Add(pilot.ShipId, contact.ShipId);
            }
        }
        state = state with { Locks = scans.ToArray() };
        bool Locked(CapsuleerAttacker? contact) =>
            contact is not null
            && state.Locks.Any(scan => scan.TargetId == contact.ShipId && scan.ReadyAt <= now)
            && locks.Contains(pilot.ShipId, contact.ShipId);
        bool locked = Locked(target);
        var active = state.Cycles.ToDictionary(cycle => cycle.ModuleId);
        var budgets = state.Budgets.ToDictionary(budget => budget.ModuleId);
        foreach (var module in profile.Modules.OrderBy(module => Priority(module.Effect)))
        {
            if (module.Item.GroupId == InterdictionRules.LauncherGroup)
            {
                continue;
            }
            var previous = active.GetValueOrDefault(module.Item.Id);
            var moduleTarget = moduleTargets.TryGetValue(module.Item.Id, out var assigned) ? assigned : target;
            bool moduleLocked = Locked(moduleTarget);
            if (
                previous?.ReadyAt > now
                && (!IsOffensive(module.Effect) || offensive && moduleLocked && previous.TargetId == moduleTarget!.ShipId)
            )
            {
                // A paid local cycle retains its duration even when that pulse fills the resource.
                continue;
            }
            bool enabled = Enabled(module, profile, currentHealth, live, now, offensive, moduleLocked, blocked, retreat && blocked);
            var budget = budgets.GetValueOrDefault(module.Item.Id) ?? new CapsuleerModuleBudget(module.Item.Id);
            bool heated =
                enabled
                && moduleTarget is not null
                && Heat(pilot, module, budget, moduleTarget, retreat && blocked || CapsuleerDefenseRules.Retreat(currentHealth, true), now);
            if (heated && live?.Banks.Values.FirstOrDefault(bank => bank.Contains(module.Item.Id)) is { } bank)
            {
                heated = !bank.Any(id => budgets.GetValueOrDefault(id)?.HeatedCycles >= PvpResources.MaximumHeatedCycles);
            }
            if (IsOffensive(module.Effect) && !IsWeapon(module.Effect))
            {
                enabled &=
                    moduleTarget is not null
                    && PvpSupport.InRange(
                        SupportModule(module) with
                        {
                            Optimal = heated ? HeatedRange(module) : SupportModule(module).Optimal,
                        },
                        Math.Max(0, pilot.Position.DistanceTo(moduleTarget.Position) - pilot.Flight.ShipRadius - moduleTarget.Radius)
                    );
            }
            if (!target.Npc && live is not null && module.Effect is DogmaEffect.Afterburner or DogmaEffect.Microwarpdrive)
            {
                double desired = PvpTactics.Range(
                    profile.PvpRange,
                    profile.TargetRange,
                    profile.TackleRange,
                    pvpRole ?? profile.PvpRole,
                    alliedTackle
                );
                double available = ChargedResource.Recharged(
                    live.Capacitor,
                    live.CapacitorCapacity,
                    live.CapacitorRechargeMilliseconds,
                    Math.Max(0, (now - live.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
                );
                enabled &= PvpTactics.Propel(
                    Math.Max(0, pilot.Position.DistanceTo(target.Position) - pilot.Flight.ShipRadius - target.Radius),
                    desired,
                    CapsuleerDefenseRules.Fraction(available, live.CapacitorCapacity),
                    retreat
                );
            }
            if (!enabled)
            {
                SelectHeat(pilot.PilotId, pilot.ShipId, module.Item.Id, module.Item.TypeId, false, now);
                if (previous is not null)
                {
                    StopModule(pilot.PilotId, pilot.ShipId, previous);
                    active.Remove(module.Item.Id);
                }
                continue;
            }
            if (previous?.ReadyAt > now)
            {
                continue;
            }
            try
            {
                if (
                    !target.Npc
                    && moduleLocked
                    && budget.AmmoCheckAt <= now
                    && live?.Turrets.GetValueOrDefault(module.Item.Id) is { Definition.Error: null } turret
                    && turret.ChargeItemId is not null
                    && turret.ReadyAt <= now
                )
                {
                    budgets[module.Item.Id] = budget = budget with { AmmoCheckAt = checked(now + PvpResources.AmmunitionCheckTicks) };
                    var reloaded = ChooseAmmunition(
                        pilot,
                        module,
                        turret.Definition.Weapon,
                        turret.Quantity >= turret.Definition.Rounds,
                        moduleTarget!,
                        navigation.Velocity,
                        now
                    );
                    if (reloaded is not null)
                    {
                        SelectHeat(pilot.PilotId, pilot.ShipId, module.Item.Id, module.Item.TypeId, false, now);
                        foreach (long reloadedModule in reloaded.ModuleIds)
                        {
                            var item = profile.Modules.First(item => item.Item.Id == reloadedModule).Item;
                            active[reloadedModule] = new(item.Id, item.TypeId, DogmaEffect.Online, 0, now, reloaded.ReadyAt)
                            {
                                FlagId = item.FlagId,
                            };
                            var changed = budgets.GetValueOrDefault(item.Id) ?? new CapsuleerModuleBudget(item.Id);
                            budgets[item.Id] = changed with { AmmoCheckAt = budget.AmmoCheckAt };
                        }
                        continue;
                    }
                }
                SelectHeat(pilot.PilotId, pilot.ShipId, module.Item.Id, module.Item.TypeId, heated, now);
                var renewed = runtime?.RenewDefense(pilot.PilotId, module, previous, now);
                foreach (var cycle in renewed is null ? Start(pilot, navigation, module, moduleTarget ?? target, now, stamp) : [renewed])
                {
                    active[cycle.ModuleId] = cycle with
                    {
                        FlagId = profile.Modules.First(item => item.Item.Id == cycle.ModuleId).Item.FlagId,
                    };
                    if (overloading.Requested(pilot.PilotId, pilot.ShipId).Contains(cycle.ModuleId))
                    {
                        var used = budgets.GetValueOrDefault(cycle.ModuleId) ?? new CapsuleerModuleBudget(cycle.ModuleId);
                        budgets[cycle.ModuleId] = used with { HeatedCycles = checked(used.HeatedCycles + 1) };
                    }
                }
            }
            catch (InvalidDataException)
            {
                SelectHeat(pilot.PilotId, pilot.ShipId, module.Item.Id, module.Item.TypeId, false, now);
                if (previous is not null)
                {
                    StopModule(pilot.PilotId, pilot.ShipId, previous);
                }
                // Ordinary action owners reject illegal, empty, disabled and unpaid cycles without side effects.
                long? retry = TryReload(pilot, module, now);
                active[module.Item.Id] = new(
                    module.Item.Id,
                    module.Item.TypeId,
                    DogmaEffect.Online,
                    0,
                    now,
                    retry ?? checked(now + TimeSpan.FromSeconds(RetrySeconds).Ticks)
                )
                {
                    Unavailable = retry is null && IsWeapon(module.Effect),
                };
            }
            live = runtime?.DefenseShip(pilot.PilotId, now);
            currentHealth = health.Read(pilot.PilotId, now);
        }
        state = state with { Cycles = active.Values.ToArray(), Budgets = budgets.Values.OrderBy(budget => budget.ModuleId).ToArray() };
        if (state.Cycles.Any(cycle => cycle.Effect is DogmaEffect.UseMissiles or DogmaEffect.AutoTargetingMissile))
        {
            missiles.Advance(pilot.SystemId, now);
            _ = missiles.TakeImpacts(pilot.PilotId);
        }
        if (offensive && locked)
        {
            state = EngageDrones(pilot, target, state, now);
        }
        else if (retreat)
        {
            state = Recall(pilot, state, now);
        }
        if (drones.HasControlled(pilot.PilotId, pilot.ShipId))
        {
            drones.Advance(pilot.PilotId, now, stamp);
        }
        bool recalling =
            retreat
            && !CapsuleerDefenseRules.Retreat(currentHealth, true)
            && state.RecallUntil > now
            && !blocked
            && drones.HasControlled(pilot.PilotId, pilot.ShipId);
        bool hold = !retreat || recalling;
        if (hold)
        {
            navigation.CancelAlignment();
            StaticSpaceObject body = new(
                target.ShipId,
                target.TypeId,
                0,
                (int)InventoryCategory.Ship,
                0,
                target.Position,
                (float)target.Radius
            )
            {
                Solid = false,
                IsGlobal = false,
            };
            if (recalling)
            {
                navigation.Stop();
            }
            else
            {
                double fallback = profile.RattingWeapons.Length == 0 ? drones.Control(pilot.PilotId).Range * .75 : MinimumCombatRange;
                double range = PilotAwareness.FightingRange(profile.RattingWeapons, profile.TargetRange, fallback);
                if (!target.Npc)
                {
                    var role = pvpRole ?? profile.PvpRole;
                    var band = PvpTactics.Band(profile.PvpRange, profile.TargetRange, profile.TackleRange, role, alliedTackle);
                    double signature = database.Catalog.Definition(s_observedSignature, target.TypeId);
                    var movement = PvpTactics.Movement(
                        profile.RattingWeapons,
                        target.Position - pilot.Position,
                        navigation.Velocity,
                        target.Velocity,
                        Math.Max(1, signature > 0 ? signature : target.Radius),
                        pilot.Flight.MaxSpeed,
                        band,
                        role,
                        pilot.Flight.ShipRadius + target.Radius,
                        WeaponObserved(live, target.ShipId).Kind
                    );
                    navigation.Follow(
                        pilot.ShipId,
                        stamp,
                        pilot.Position,
                        pilot.Flight.ShipRadius,
                        body,
                        band.Desired,
                        movement.Kind == PvpMovementKind.Orbit,
                        true
                    );
                    if (movement.Direction is { } direction)
                    {
                        if (direction.Length > 0)
                        {
                            navigation.AlignTo(pilot.Position, pilot.Position + direction);
                            navigation.SetSpeed(movement.SpeedFraction);
                        }
                        else
                        {
                            navigation.Stop();
                        }
                    }
                }
                else
                {
                    bool orbit = proactive is not null && range < PvpTactics.KitingRange;
                    navigation.Follow(pilot.ShipId, stamp, pilot.Position, pilot.Flight.ShipRadius, body, range, orbit, true);
                }
            }
            if (state.MotionStamp != stamp)
            {
                var tick = navigation.Advance(
                    pilot.ShipId,
                    stamp,
                    pilot.Flight,
                    pilot.Position,
                    id => id == body.Id ? body : null,
                    obstacles
                );
                pilot = pilot with { Position = tick.Position };
                runtime?.PublishPilotPosition(pilot.ShipId, pilot.SystemId, tick.Position);
                state = state with { MotionStamp = stamp };
            }
        }
        // Active propulsion uses the normal fitted speed and mass, including scram suppression.
        attributes = Profile(pilot.PilotId).ShipAttributes;
        pilot = pilot with
        {
            Flight = pilot.Flight with
            {
                MaxSpeed = (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity, pilot.Flight.MaxSpeed),
                Mass = attributes.GetValueOrDefault(DogmaAttributeId.Mass, pilot.Flight.Mass),
            },
        };
        return new(
            pilot,
            state,
            hold,
            retreat,
            newThreat,
            WarpBlocked: attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus) > 0
        );
    }

    internal CapsuleerDefenseProfile Profile(long pilot)
    {
        CapsuleerDefenseProfile Load()
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            if (LiveWorld.Find(database.Path) is { } live)
            {
                return live.Economy.Read(world => CapsuleerDefenseProfile.Read(world, catalog, pilot), new(pilots: [pilot]));
            }
            using WorldDbContext world = new(database.Path);
            return CapsuleerDefenseProfile.Read(world, catalog, pilot);
        }
        return LiveReadModels.Find(database.Path)?.DefenseProfile(pilot, Load) ?? Load();
    }

    private CapsuleerAttacker[] PlayerAttackers(DockedPilot pilot, long now)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var attacks = WorldActionScope.Inspect(
            database,
            world =>
                world
                    .Filter<SavedPilotHostility>(attack =>
                        attack.TargetShipId == pilot.ShipId && attack.ExpiresAt > now && attack.LastAttackAt > 0
                    )
                    .AsEnumerable()
                    .Select(attack =>
                        (Attack: attack, Pilot: world.Filter<Pilot>(value => value.ActiveShipId == attack.SourceShipId).SingleOrDefault())
                    )
                    .ToArray()
        );
        List<CapsuleerAttacker> result = [];
        foreach (var (attack, other) in attacks)
        {
            if (other is null || presence.Find(pilot.SystemId, attack.SourceShipId) is not { } contact)
            {
                continue;
            }
            var type = catalog.RequireItem(other.ActiveShip.TypeId);
            result.Add(
                new(
                    other.Id,
                    other.ActiveShipId,
                    type.Id,
                    contact.Position,
                    contact.Velocity,
                    type.Radius ?? 0,
                    fitting.SignatureRadius(other.ActiveShipId, type.Id),
                    attack.LastAttackAt,
                    false,
                    false
                )
            );
        }
        return result.ToArray();
    }

    private static bool IsWeapon(DogmaEffect effect) =>
        effect is DogmaEffect.LaserFired or DogmaEffect.ProjectileFired or DogmaEffect.UseMissiles or DogmaEffect.AutoTargetingMissile;

    private static bool IsSustained(DogmaEffect effect) =>
        effect
            is DogmaEffect.ActiveShieldResistance
                or DogmaEffect.ActiveArmorResistance
                or DogmaEffect.Afterburner
                or DogmaEffect.Microwarpdrive
                or DogmaEffect.SensorBooster
                or DogmaEffect.TrackingComputer
                or DogmaEffect.MissileGuidanceComputer
                or DogmaEffect.OmnidirectionalTrackingLink;

    private static bool IsOffensive(DogmaEffect effect) =>
        IsWeapon(effect)
        || effect
            is DogmaEffect.StasisWebifier
                or DogmaEffect.WarpDisruptor
                or DogmaEffect.WarpScrambler
                or DogmaEffect.PlayerEcm
                or DogmaEffect.SensorDampener
                or DogmaEffect.TrackingDisruptor
                or DogmaEffect.GuidanceDisruptor
                or DogmaEffect.TargetPainter
                or DogmaEffect.EnergyNeutralizer
                or DogmaEffect.EnergyNosferatu;

    private static int Priority(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.CapacitorBoosting => 0,
            DogmaEffect.ActiveShieldResistance or DogmaEffect.ActiveArmorResistance => 1,
            DogmaEffect.ShieldBoosting
            or DogmaEffect.AncillaryShieldBoosting
            or DogmaEffect.ArmorRepair
            or DogmaEffect.AncillaryArmorRepair
            or DogmaEffect.StructureRepair => 2,
            DogmaEffect.AssaultDamageControl or DogmaEffect.SignatureSuppression or DogmaEffect.WarpCoreStabilizer => 3,
            DogmaEffect.Afterburner or DogmaEffect.Microwarpdrive => 4,
            DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler => 5,
            DogmaEffect.StasisWebifier or DogmaEffect.EnergyNosferatu => 6,
            _ => 7,
        };

    private static bool Enabled(
        CapsuleerDefenseEquipment module,
        CapsuleerDefenseProfile profile,
        ShipHealth health,
        LiveCombatShip? live,
        long now,
        bool offensive,
        bool locked,
        bool blocked,
        bool fightingRetreat
    )
    {
        double capacitor = live is null
            ? double.MaxValue
            : ChargedResource.Recharged(
                live.Capacitor,
                live.CapacitorCapacity,
                live.CapacitorRechargeMilliseconds,
                Math.Max(0, (now - live.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
            );
        bool armorTank = profile.Modules.Any(value =>
            value.Effect is DogmaEffect.ArmorRepair or DogmaEffect.AncillaryArmorRepair or DogmaEffect.ActiveArmorResistance
        );
        bool shieldTank = profile.Modules.Any(value =>
            value.Effect is DogmaEffect.ShieldBoosting or DogmaEffect.AncillaryShieldBoosting or DogmaEffect.ActiveShieldResistance
        );
        return CapsuleerDefenseRules.ShouldActivate(
            module.Effect,
            module.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            health,
            capacitor,
            live?.CapacitorCapacity ?? double.MaxValue,
            offensive,
            locked,
            blocked,
            shieldTank || !armorTank && health.Defense.Shield >= health.Defense.Armor,
            fightingRetreat
        );
    }

    private CapsuleerModuleCycle[] Start(
        DockedPilot pilot,
        NavigationState navigation,
        CapsuleerDefenseEquipment equipment,
        CapsuleerAttacker target,
        long now,
        int stamp
    )
    {
        long id = pilot.PilotId;
        long module = equipment.Item.Id;
        var effect = equipment.Effect;
        HashSet<long> locked = locks.Contains(pilot.ShipId, target.ShipId) ? [target.ShipId] : [];
        TackleTarget targeted = new(target.ShipId, target.Npc ? TackleTargetKind.Npc : TackleTargetKind.Ship);
        CapsuleerModuleCycle Cycle(double milliseconds, int charge = 0, long? ready = null) =>
            new(
                module,
                equipment.Item.TypeId,
                effect,
                target.ShipId,
                now,
                ready ?? checked(now + (long)Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond)),
                charge
            );
        var residentRepair = LiveCombatRuntime.Find(database.Path)?.DefenseRepair(id, equipment, now);
        if (residentRepair is not null)
        {
            return [residentRepair];
        }
        switch (effect)
        {
            case DogmaEffect.LaserFired:
            case DogmaEffect.ProjectileFired:
                double roll = (TravelerRoutineRules.Mix(unchecked((ulong)module ^ (ulong)now)) >> 11) / 9007199254740992d;
                var volley = target.Npc
                    ? weapons.FireBank(id, module, target.ShipId, navigation.Velocity, now, roll)
                    : weapons.FirePlayerBank(id, module, new(target.ShipId, target.Velocity), navigation.Velocity, now, roll);
                return volley
                    .Weapons.Select(weapon => new CapsuleerModuleCycle(
                        weapon.ItemId,
                        weapon.TypeId,
                        effect,
                        target.ShipId,
                        now,
                        checked(now + (long)Math.Ceiling(weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond)),
                        weapon.ChargeTypeId
                    ))
                    .ToArray();
            case DogmaEffect.UseMissiles:
            case DogmaEffect.AutoTargetingMissile:
                return missiles
                    .LaunchBank(
                        id,
                        module,
                        target.ShipId,
                        locked,
                        new(navigation.Velocity, pilot.Flight.MaxSpeed, navigation.LastHeading * -1, stamp),
                        now
                    )
                    .Select(launch => new CapsuleerModuleCycle(
                        launch.Launcher.ItemId,
                        launch.Launcher.TypeId,
                        effect,
                        target.ShipId,
                        now,
                        checked(now + (long)Math.Ceiling(launch.Launcher.CycleMilliseconds * TimeSpan.TicksPerMillisecond)),
                        launch.Launcher.ChargeTypeId
                    ))
                    .ToArray();
            case DogmaEffect.ShieldBoosting:
            case DogmaEffect.AncillaryShieldBoosting:
                var boost = shields.Boost(id, module, now, effect == DogmaEffect.AncillaryShieldBoosting);
                return [Cycle(boost.Booster.DurationMilliseconds)];
            case DogmaEffect.ArmorRepair:
            case DogmaEffect.AncillaryArmorRepair:
            case DogmaEffect.StructureRepair:
                var repair = repairs.Start(
                    id,
                    module,
                    now,
                    effect == DogmaEffect.StructureRepair ? RepairLayer.Hull : RepairLayer.Armor,
                    effect == DogmaEffect.AncillaryArmorRepair
                );
                return [Cycle(repair.Schedule.DurationMilliseconds)];
            case DogmaEffect.CapacitorBoosting:
                var injection = boosters.Inject(id, module, now);
                return [Cycle(injection.Module.DurationMilliseconds)];
            case DogmaEffect.ActiveShieldResistance:
            case DogmaEffect.ActiveArmorResistance:
                var hardener = hardeners.Activate(id, module, effect, now);
                return [Cycle(hardener.Module.DurationMilliseconds) with { Hardener = hardener.Module }];
            case DogmaEffect.Afterburner:
            case DogmaEffect.Microwarpdrive:
                var speed = propulsion.Activate(id, module, now, effect);
                return [Cycle(speed.Module.DurationMilliseconds) with { Propulsion = speed.Module }];
            case DogmaEffect.AssaultDamageControl:
                var damage = damageControls.Activate(id, module, now);
                return
                [
                    Cycle(damage.Module.DurationMilliseconds, ready: damage.Schedule.ReadyAt) with
                    {
                        AssaultDamageControl = damage.Module,
                        ControlSchedule = damage.Schedule,
                    },
                ];
            case DogmaEffect.SignatureSuppression:
                var signature = suppression.Activate(id, module, now);
                return
                [
                    Cycle(signature.Module.DurationMilliseconds, ready: signature.Schedule.ReadyAt) with
                    {
                        SignatureSuppression = signature.Module,
                        SuppressionSchedule = signature.Schedule,
                    },
                ];
            case DogmaEffect.StasisWebifier:
            case DogmaEffect.WarpDisruptor:
            case DogmaEffect.WarpScrambler:
            case DogmaEffect.WarpCoreStabilizer:
                var hold = tackle.Activate(id, module, effect, effect == DogmaEffect.WarpCoreStabilizer ? null : targeted, locked, now);
                return [Cycle(hold.Module.DurationMilliseconds, ready: hold.ReadyAt) with { Tackle = hold.Module }];
            case DogmaEffect.PlayerEcm:
            case DogmaEffect.SensorDampener:
            case DogmaEffect.TrackingDisruptor:
            case DogmaEffect.GuidanceDisruptor:
            case DogmaEffect.TargetPainter:
                var ewar = warfare.Activate(id, module, effect, targeted, locked, now);
                return [Cycle(ewar.Module.DurationMilliseconds) with { ElectronicWarfare = ewar.Contacts.ToArray() }];
            case DogmaEffect.EnergyNeutralizer:
            case DogmaEffect.EnergyNosferatu:
                var capacitor = capacitorWarfare.Activate(id, module, effect, target.ShipId, locked, now);
                return [Cycle(capacitor.Cycle.Module.DurationMilliseconds) with { Capacitor = capacitor.Cycle }];
            default:
                var applied = support.Activate(id, module, effect, now);
                return [Cycle(applied.Module.DurationMilliseconds)];
        }
    }

    private long? TryReload(DockedPilot pilot, CapsuleerDefenseEquipment equipment, long now)
    {
        if (
            IsWeapon(equipment.Effect)
            || equipment.Effect is DogmaEffect.CapacitorBoosting or DogmaEffect.AncillaryShieldBoosting or DogmaEffect.AncillaryArmorRepair
        )
        {
            var charges = inventory
                .List(pilot.PilotId, pilot.ShipId)
                .Where(item => item.FlagId == (int)InventoryFlag.Cargo && item.CategoryId == (int)InventoryCategory.Charge)
                .OrderBy(item => item.Id);
            foreach (var charge in charges)
            {
                try
                {
                    var loaded = ammunition.Load(pilot.PilotId, pilot.ShipId, [equipment.Item.Id], [charge.Id], pilot.ShipId, now);
                    return checked(now + Math.Max(1, (long)Math.Ceiling(loaded.ReloadMilliseconds * TimeSpan.TicksPerMillisecond)));
                }
                catch (InvalidDataException)
                {
                    // The normal loader validates charge family, size and remaining capacity.
                }
            }
        }
        return null;
    }

    private CapsuleerDefenseState EngageDrones(DockedPilot pilot, CapsuleerAttacker target, CapsuleerDefenseState state, long now)
    {
        if (!state.DroneLaunchAttempted && DroneRules.SupportsHull(pilot.ShipTypeId))
        {
            var control = drones.Control(pilot.PilotId);
            var stacks = inventory
                .List(pilot.PilotId, pilot.ShipId)
                .Where(item => item.FlagId == (int)InventoryFlag.DroneBay)
                .OrderBy(item => item.Id)
                .ToArray();
            int available = control.MaximumDrones;
            foreach (var stack in stacks)
            {
                int quantity = Math.Min(available, stack.Quantity);
                if (quantity <= 0)
                {
                    break;
                }
                try
                {
                    var launch = drones.Launch(pilot.PilotId, [new(stack.Id, quantity)], now);
                    available -= launch.Launches.Sum(value => value.DroneIds.Count);
                }
                catch (InvalidDataException)
                {
                    // An absent, unsupported or over-bandwidth flight does not prevent other defenses.
                }
            }
            state = state with { DroneLaunchAttempted = true };
        }
        var flight = drones
            .List(pilot.SystemId, now)
            .Where(drone =>
                drone.PilotId == pilot.PilotId
                && drone.ShipId == pilot.ShipId
                && (drone.TargetId != target.ShipId || drone.Activity != DroneActivity.Fighting)
            )
            .Select(drone => drone.Id)
            .ToArray();
        if (flight.Length > 0)
        {
            try
            {
                drones.Order(pilot.PilotId, flight, DroneOrder.Engage, target.ShipId, new HashSet<long> { target.ShipId }, now);
            }
            catch (InvalidDataException)
            {
                // Target disappearance, control range and legality are checked at the command boundary again.
            }
        }
        return state.DroneLaunchAttempted ? state : state with { DroneLaunchAttempted = true };
    }

    private CapsuleerDefenseState Recall(DockedPilot pilot, CapsuleerDefenseState state, long now)
    {
        if (!drones.HasControlled(pilot.PilotId, pilot.ShipId) || state.RecallUntil > 0)
        {
            return state;
        }
        var flight = drones
            .List(pilot.SystemId, now)
            .Where(drone => drone.PilotId == pilot.PilotId && drone.ShipId == pilot.ShipId)
            .Select(drone => drone.Id)
            .ToArray();
        if (flight.Length > 0)
        {
            drones.Order(pilot.PilotId, flight, DroneOrder.ReturnToBay, null, new HashSet<long>(), now);
        }
        return state with { RecallUntil = checked(now + CapsuleerDefenseRules.RecallTimeout.Ticks) };
    }

    private void Stop(long pilot, CapsuleerDefenseState state)
    {
        overloading.Clear(pilot, state.ShipId);
        // Defense owns outgoing locks. Clearing incoming locks here makes peaceful pilots
        // lose every player's lock on each maintenance update, including during alignment.
        foreach (var scan in state.Locks)
        {
            locks.Remove(state.ShipId, scan.TargetId);
        }
        foreach (var cycle in state.Cycles)
        {
            StopModule(pilot, state.ShipId, cycle);
        }
    }

    private void StopModule(long pilot, long ship, CapsuleerModuleCycle cycle)
    {
        SelectHeat(pilot, ship, cycle.ModuleId, cycle.TypeId, false, cycle.ReadyAt);
        switch (cycle.Effect)
        {
            case DogmaEffect.ActiveShieldResistance:
            case DogmaEffect.ActiveArmorResistance:
                hardeners.Stop(pilot, ship, cycle.ModuleId);
                break;
            case DogmaEffect.Afterburner:
            case DogmaEffect.Microwarpdrive:
                propulsion.Stop(pilot, ship, cycle.ModuleId);
                break;
            case DogmaEffect.StasisWebifier:
            case DogmaEffect.WarpDisruptor:
            case DogmaEffect.WarpScrambler:
            case DogmaEffect.WarpCoreStabilizer:
                tackle.Stop(pilot, ship, cycle.ModuleId);
                break;
            case DogmaEffect.PlayerEcm:
            case DogmaEffect.SensorDampener:
            case DogmaEffect.TrackingDisruptor:
            case DogmaEffect.GuidanceDisruptor:
            case DogmaEffect.TargetPainter:
                warfare.Stop(pilot, ship, cycle.ModuleId);
                break;
            case DogmaEffect.EnergyNeutralizer:
            case DogmaEffect.EnergyNosferatu:
                capacitorWarfare.Stop(pilot, ship, cycle.ModuleId);
                break;
            case DogmaEffect.SensorBooster:
            case DogmaEffect.TrackingComputer:
            case DogmaEffect.MissileGuidanceComputer:
            case DogmaEffect.OmnidirectionalTrackingLink:
                support.Stop(pilot, ship, cycle.ModuleId);
                break;
            case DogmaEffect.SignatureSuppression:
                suppression.Stop(pilot, ship, cycle.ModuleId);
                break;
        }
    }
}
