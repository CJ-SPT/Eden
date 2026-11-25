using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Structures;

[Injectable]
internal sealed partial class PilotStructureWeaponStore(
    WorldDatabase database,
    TimeProvider clock,
    IPilotStructureControl control,
    PilotStructureTargetStore targeting,
    NpcProfileCatalog npcProfiles,
    ICapacitorEvents capacitorEvents,
    INpcEwarRoll ewarRolls,
    IPilotCombatPresence presence,
    IPilotDamageEvents damageEvents,
    PilotLossSettlement losses,
    SqliteWorld lookup,
    SecurityStatusSignal securityStatus
) : IPilotStructureWeapons
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public StructureWeaponActivation Activate(
        StructureControlToken token,
        long moduleId,
        long targetId,
        int repeats,
        int stamp,
        DogmaEffect effect
    ) =>
        Execute(
            token,
            lease =>
            {
                if (repeats < 0)
                {
                    throw new InvalidDataException("Choose a valid defensive weapon repeat count.");
                }
                if (lease.Weapons.TryGetValue(moduleId, out var current))
                {
                    if (!current.Active(Now))
                    {
                        throw new InvalidDataException("The structure module is waiting for reactivation.");
                    }
                    if (current.TargetId != targetId || current.Effect != effect)
                    {
                        throw new InvalidDataException("Stop the current defensive weapon cycle before changing targets.");
                    }
                    return new(current, null);
                }
                var result = Fire(token, moduleId, targetId, repeats, stamp, effect);
                lease.Weapons = lease.Weapons.Add(moduleId, result.Cycle);
                return result;
            }
        );

    public void Deactivate(StructureControlToken token, long moduleId, DogmaEffect? effect) =>
        Execute(
            token,
            lease =>
            {
                if (lease.Weapons.TryGetValue(moduleId, out var cycle))
                {
                    if (effect.HasValue && effect != cycle.Effect)
                    {
                        throw new InvalidDataException("The requested effect does not belong to this active module.");
                    }
                    lease.Weapons = lease.Weapons.SetItem(moduleId, cycle with { Stopping = true });
                }
                return true;
            }
        );

    public IReadOnlyList<StructureWeaponCycle> Read(StructureControlToken token) =>
        Execute(token, lease => lease.Weapons.Values.Where(cycle => cycle.Active(Now)).OrderBy(cycle => cycle.ModuleId).ToArray());

    public StructureWeaponFrame Advance(StructureControlToken token, int stamp) =>
        Execute<StructureWeaponFrame>(
            token,
            lease =>
            {
                List<StructureWeaponActivation> started = [];
                List<StructureWeaponCycle> stopped = [];
                var locked = targeting.Read(token).Targets.Where(target => target.Locked).Select(target => target.TargetId).ToHashSet();
                long now = Now;
                foreach (var cycle in lease.Weapons.Values.OrderBy(cycle => cycle.ModuleId).ToArray())
                {
                    bool targetLost = cycle.Effect != DogmaEffect.StructurePointDefense && !locked.Contains(cycle.TargetId);
                    if (!targetLost && now < cycle.ReadyAt)
                    {
                        continue;
                    }
                    if (TackleDefinitions.StructureTackle(cycle.Effect))
                    {
                        EndTackle(cycle);
                    }
                    if (StructureWeaponRules.IsElectronicWarfare(cycle.Effect))
                    {
                        EndElectronicWarfare(cycle);
                    }
                    if (!targetLost && !cycle.Stopping && cycle.Repeats > 0)
                    {
                        try
                        {
                            // One paid cycle at the current boundary. Delayed callbacks never replay missed launches.
                            var next = Fire(token, cycle.ModuleId, cycle.TargetId, cycle.Repeats - 1, stamp, cycle.Effect);
                            lease.Weapons = lease.Weapons.SetItem(cycle.ModuleId, next.Cycle);
                            started.Add(next);
                            continue;
                        }
                        catch (Exception error) when (error is InvalidDataException or OverflowException) { }
                    }
                    lease.Weapons = lease.Weapons.Remove(cycle.ModuleId);
                    stopped.Add(cycle);
                }
                return new(started, stopped);
            }
        );

    private T Execute<T>(StructureControlToken token, Func<ResidentStructureControl, T> action)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Structure weapons require the resident world.");
        using var ownership = live.EnterPersistence(nameof(PilotStructureWeaponStore));
        _ = control.Require(token);
        if (!live.StructureControllers.TryGetValue(token.StructureId, out var lease) || lease.Token != token)
        {
            throw new InvalidDataException("This structure control lease is no longer active.");
        }
        return lease.WithActive(() => action(lease));
    }

    private StructureWeaponActivation Fire(
        StructureControlToken token,
        long moduleId,
        long targetId,
        int repeats,
        int stamp,
        DogmaEffect effect
    )
    {
        bool area = effect == DogmaEffect.StructurePointDefense;
        if (area)
        {
            if (targetId != 0)
            {
                throw new InvalidDataException("Activate point defense without a target.");
            }
        }
        else
        {
            // Acquisition revalidates motion and shared lock breaks before opening the paid transaction.
            targeting.RequireLocked(token, targetId);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        PointDefenseCandidates? candidates = null;
        using WorldActionScope transaction = new(
            database,
            select: world =>
            {
                if (area)
                {
                    candidates = FindPointDefenseCandidates(world, catalog, token.StructureId, moduleId, now);
                    return PointDefenseParticipants(world, token, candidates);
                }
                return new(
                    systems: [world.Structure(token.StructureId)!.SystemId],
                    pilots: [token.PilotId],
                    ships: [targetId],
                    resources: NpcHostility
                        .Participants(world, targetId)
                        .Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
                );
            }
        );
        var world = transaction.World;
        var source = world.Set<SavedStructure>().Find(token.StructureId)!;
        if (!StructureTargetingRules.Active(source.HasCore, source.State))
        {
            throw new InvalidDataException("The structure cannot operate defensive weapons in this state.");
        }
        var module = world.Set<OwnedItem>().Find(moduleId);
        if (
            module is null
            || module.LocationId != source.Id
            || module.OwnerId != source.CorporationId
            || !module.Online
            || !(
                TackleDefinitions.StructureTackle(effect) || StructureWeaponRules.IsElectronicWarfare(effect)
                    ? InventoryDefinitions.IsMediumSlot(module.FlagId)
                    : InventoryDefinitions.IsHighSlot(module.FlagId)
            )
        )
        {
            throw new InvalidDataException("Choose an online defense in the appropriate rack fitted to this structure.");
        }
        var profile = StructureEquipmentCatalog.Require(catalog, module.TypeId);
        if (FittingCatalog.DefaultEffect(catalog, module.TypeId) != effect)
        {
            throw new InvalidDataException("The requested effect does not belong to this module.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The structure module is still cycling or reloading.");
        }
        var fit = StructureEquipmentPersistence.Read(world, catalog, source);
        if (candidates is not null && profile.GroupId == StructureWeaponRules.PointDefenseGroup)
        {
            var pulse = FirePointDefense(world, catalog, token, source, module, profile, fit, candidates, repeats, now);
            SetCooldown(world, moduleId, cooldown, pulse.Activation.Cycle.ReadyAt);
            transaction.Commit();
            transaction.Dispose();
            if (pulse.DestroyedNpc)
            {
                securityStatus.Publish();
            }
            foreach (var notification in pulse.Notifications)
            {
                damageEvents.Publish(notification);
            }
            return pulse.Activation;
        }
        var target =
            targeting.Find(world, catalog, source.SystemId, targetId, now)
            ?? throw new InvalidDataException("The defensive weapon target is unavailable.");
        var victim = CheckTarget(world, catalog, source, target, now);
        StructureWeaponActivation result;
        if (effect == DogmaEffect.StructureEnergyNeutralizer && profile.GroupId == StructureWeaponRules.NeutralizerGroup)
        {
            result = Neutralize(world, catalog, token, source, module, target, victim, fit, repeats, now);
        }
        else if (TackleDefinitions.StructureTackle(effect) && StructureWeaponRules.UsesElectronicSystems(profile.GroupId))
        {
            result = ApplyTackle(world, catalog, token, source, module, target, victim, fit, effect, now);
        }
        else if (StructureWeaponRules.MatchesElectronicWarfare(profile.GroupId, effect))
        {
            result = ApplyElectronicWarfare(world, catalog, token, source, module, target, victim, fit, effect, repeats, now);
        }
        else if (effect == DogmaEffect.UseMissiles && StructureWeaponRules.MissileLauncher(profile.GroupId))
        {
            result = Launch(world, catalog, token, source, module, target, profile, fit, repeats, stamp, now);
        }
        else
        {
            throw new InvalidDataException("This defensive module effect is unsupported.");
        }
        long ready = result.Cycle.ReadyAt;
        SetCooldown(world, moduleId, cooldown, Math.Max(ready, result.Cycle.ReactivationAt));
        StructureCombatTargets.Record(world, source.Id, targetId, now, ready);
        if (target.Kind == StructureTargetKind.Npc)
        {
            // Ordinary NPC retaliation currently owns ship targets; do not create a fake ship or corrupt that ledger.
            Eden.World.Exploration.CombatAnomalyStore.SleeperAggressed(world, catalog, world.Set<WorldNpc>().Find(targetId)!, now);
            CrimewatchPersistence.Npc(world, token.PilotId, source.SystemId, ready);
        }
        else
        {
            var timers = CrimewatchPersistence.State(world, token.PilotId);
            timers.SystemId = source.SystemId;
            timers.Apply(CrimewatchRules.Player(timers.Timers(), ready, CrimewatchFlag.None, true, now));
            if (victim is not null)
            {
                if (target.Kind == StructureTargetKind.Drone)
                {
                    StructureCombatTargets.Record(world, source.Id, victim.ActiveShipId, now, ready);
                }
                CrimewatchPersistence.PlayerImpact(world, token.PilotId, victim.Id, source.SystemId, now);
            }
        }
        transaction.Commit();
        if (result.Neutralization is not null && victim is not null)
        {
            capacitorEvents.Publish(new(victim.Id, targetId));
        }
        return result;
    }

    private static void SetCooldown(WorldAction world, long moduleId, ModuleCooldown? cooldown, long ready)
    {
        cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = ready;
    }

    private IReadOnlyDictionary<DogmaAttributeId, double> TargetAttributes(
        WorldAction world,
        CatalogDbContext catalog,
        StructureTarget target,
        Pilot? victim,
        long now
    )
    {
        var profile = victim is null ? npcProfiles.Read(catalog, target.TypeId) : null;
        var attributes = profile is not null
            ? PlayerEwarStore.NpcAttributes(world, target.Id, profile.Attributes)
            : PilotFittingStore.ShipAttributes(world, catalog, victim!, incursionTime: now);
        if (
            profile is not null
                ? world.Set<WorldNpc>().Find(target.Id)!.StructureDamage >= profile.Defense.Structure
                : PilotHealthStore.Read(world, target.Id, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed
                    || world.Set<ShipLoss>().Any(loss => loss.ShipId == target.Id)
        )
        {
            throw new InvalidDataException("A destroyed target cannot receive hostile effects.");
        }
        if (attributes.GetValueOrDefault(DogmaAttributeId.DisallowOffensiveModifiers) != 0)
        {
            throw new InvalidDataException("This target is immune to offensive modifiers.");
        }
        return attributes;
    }

    private static double SurfaceDistance(CatalogDbContext catalog, SavedStructure source, StructureTarget target) =>
        Math.Max(
            0,
            StructureCombatTargets.Position(source).DistanceTo(target.Position)
                - StructureCatalog.Require(catalog, source.TypeId).Radius
                - target.Radius
        );

    private static StructureWeaponActivation Launch(
        WorldAction world,
        CatalogDbContext catalog,
        StructureControlToken token,
        SavedStructure source,
        OwnedItem module,
        StructureTarget target,
        StructureEquipmentProfile profile,
        StructureFittingState fit,
        int repeats,
        int stamp,
        long now
    )
    {
        long moduleId = module.Id;
        long targetId = target.Id;
        var attributes = fit.Equipment[moduleId];
        var loaded = world.Filter<OwnedItem>(item => item.LocationId == moduleId).SingleOrDefault();
        if (loaded is null || loaded.OwnerId != source.CorporationId || loaded.Quantity <= 0)
        {
            throw new InvalidDataException("Load compatible structure missiles before firing.");
        }
        var chargeProfile = StructureChargeCatalog.Require(catalog, loaded.TypeId);
        if (!StructureAmmunitionRules.Compatible(profile, chargeProfile))
        {
            throw new InvalidDataException("The defensive launcher contains incompatible ammunition.");
        }
        // These pinned launcher families consume ammunition only. Capacitor-using defenses have a separate resource authority.
        if (attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed) != 0)
        {
            throw new InvalidDataException("This defensive launcher requires an unsupported capacitor consumer.");
        }
        int rounds = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate));
        if (rounds <= 0 || rounds != attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate) || loaded.Quantity < rounds)
        {
            throw new InvalidDataException("Load enough missiles for this defensive launcher cycle.");
        }
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
        long ready = StructureWeaponRules.ReadyAt(duration, now);
        var charge = fit.Charges[moduleId];
        var drive = MissileChargeCatalog.Drive(catalog, loaded.TypeId, charge);
        int level = PilotSkillReader.Level(world, catalog, token.PilotId, StructureWeaponRules.MissileSystemsSkill);
        double bonus = catalog
            .FittingAttributes(StructureWeaponRules.MissileSystemsSkill)
            .GetValueOrDefault(StructureWeaponRules.MissileSkillBonus);
        var warhead = MissileRules.Warhead(charge, StructureWeaponRules.DamageMultiplier(level, bonus));
        var targetMotion = new MissileTarget(target.Position, target.Velocity, (float)target.Radius);
        var flight = MissileFlightRules.Launch(
            drive,
            new(StructureCombatTargets.Position(source), SpacePosition.Zero, 0, new(-Math.Sin(source.Yaw), 0, -Math.Cos(source.Yaw))),
            targetMotion,
            charge.GetValueOrDefault(DogmaAttributeId.AimedMissileLaunch) != 0
        );
        var sourceParty = PilotInteractionHistoryStore.Player(world, world.Pilots.Find(token.PilotId)!, source.Id, module.TypeId) with
        {
            ShipTypeId = source.TypeId,
            CorporationId = source.CorporationId,
        };
        MissilePayload payload = new(flight, warhead, Target: targetMotion, Source: sourceParty);
        var projectile = world
            .Add(
                new WorldMissile
                {
                    SourceKind = MissileSourceKind.Structure,
                    SourceTypeId = source.TypeId,
                    SourceShipId = source.Id,
                    PilotId = token.PilotId,
                    CorporationId = source.CorporationId,
                    ModuleId = moduleId,
                    LauncherTypeId = module.TypeId,
                    ChargeTypeId = loaded.TypeId,
                    GroupId = chargeProfile.GroupId,
                    SystemId = source.SystemId,
                    TargetId = targetId,
                    TargetKind = target.Kind switch
                    {
                        StructureTargetKind.Npc => MissileTargetKind.Npc,
                        StructureTargetKind.Pilot => MissileTargetKind.Pilot,
                        StructureTargetKind.Structure => MissileTargetKind.Structure,
                        StructureTargetKind.Drone => MissileTargetKind.Drone,
                        _ => throw new InvalidDataException("Unknown structure missile target."),
                    },
                    TargetRadius = (float)target.Radius,
                    LaunchStamp = stamp,
                    LaunchedAt = now,
                    UpdatedAt = now,
                    ExpiresAt = StructureWeaponRules.ReadyAt(drive.LifetimeMilliseconds, now),
                    Snapshot = JsonSerializer.Serialize(payload, MissileJson.Default.MissilePayload),
                }
            )
            .Entity;
        loaded.Quantity -= rounds;
        Magazine magazine = new(moduleId, source.Id, module.FlagId, loaded.TypeId, loaded.Quantity, 0);
        if (loaded.Quantity == 0)
        {
            world.Remove(loaded);
        }
        return new(
            new(moduleId, targetId, module.TypeId, now, ready, duration, repeats),
            new(new(moduleId, module.TypeId, loaded.TypeId, duration, false), projectile.Contact(payload), magazine)
        );
    }

    private static Pilot? CheckTarget(WorldAction world, CatalogDbContext catalog, SavedStructure source, StructureTarget target, long now)
    {
        double security = CrimewatchPersistence.Security(catalog, source.SystemId);
        bool retaliation = StructureCombatTargets.Hostile(world, target.Id, source.Id, now);
        if (target.Kind == StructureTargetKind.Npc)
        {
            if (world.Set<WorldNpc>().Find(target.Id)?.Role == NpcRole.Concord)
            {
                throw new InvalidDataException("Structure green safety prevents attacking CONCORD.");
            }
            return null;
        }
        if (target.Kind is StructureTargetKind.Pilot or StructureTargetKind.Drone)
        {
            var drone = target.Kind == StructureTargetKind.Drone ? world.Set<WorldDrone>().Find(target.Id) : null;
            var victim = drone is null
                ? world.Filter<Pilot>(pilot => pilot.ActiveShipId == target.Id).Single()
                : world.Pilots.Find(drone.PilotId);
            if (victim is null || drone is not null && drone.ControllerShipId != victim.ActiveShipId)
            {
                // Uncontrolled drones carry no active controller's retaliation rights or combat timers.
                StructureWeaponRules.CheckPlayer(security, CrimewatchFlag.None, false, false);
                return null;
            }
            if (drone is not null)
            {
                retaliation = StructureCombatTargets.Hostile(world, drone.ControllerShipId, source.Id, now);
            }
            var flag = world.Set<SavedCrimewatch>().Find(victim.Id)?.Timers().Flag(now) ?? CrimewatchFlag.None;
            bool outlaw = (world.Set<SavedSecurityStatus>().Find(victim.Id)?.Value ?? 0) <= CrimewatchRules.OutlawSecurity;
            bool war = security > 0 && CorporationWarStore.HasActiveWar(world, source.CorporationId, victim.CorporationId, now);
            StructureWeaponRules.CheckPlayer(security, flag, outlaw, retaliation, war);
            return victim;
        }
        if (target.Kind != StructureTargetKind.Structure)
        {
            throw new InvalidDataException("This defensive launcher cannot attack this target kind yet.");
        }
        var structure = world.Set<SavedStructure>().Find(target.Id)!;
        bool warTarget = security > 0 && CorporationWarStore.HasActiveWar(world, source.CorporationId, structure.CorporationId, now);
        StructureCombatRules.CheckAttack(
            StructureCatalog.Require(catalog, structure.TypeId),
            security,
            StructureBattlePersistence.Read(world, catalog, structure),
            retaliation,
            warTarget
        );
        StructureWeaponRules.CheckPlayer(security, CrimewatchFlag.None, false, retaliation, warTarget);
        return null;
    }
}
