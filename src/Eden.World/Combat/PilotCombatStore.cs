using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Combat;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PilotCombatStore(
    WorldDatabase database,
    SecurityStatusSignal security,
    SqliteWorld lookup,
    IShipLootRoll lootRoll,
    IPilotDamageEvents damageEvents
) : IPilotCombat
{
    private const int CrystalProbabilityResolution = 1_000_000;

    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        catalog.Database.OpenConnection();
        return ModuleAttributes(world, catalog, pilotId, module);
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module,
        FittingCalculation? calculation = null
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var combatSkills = Enum.GetValues<CombatSkill>();
        var skillLevels = PilotSkillReader.Levels(world, catalog, pilotId, combatSkills.Select(s => (int)s).ToArray());
        var levels = combatSkills.ToDictionary(skill => skill, skill => skillLevels[(int)skill]);
        var loaded = PilotAmmunitionStore.Contents(world, module.Id);
        var cooldown = world.Set<ModuleCooldown>().Find(module.Id);
        IReadOnlyDictionary<DogmaAttributeId, double> hull = HullCatalog.UsesCatalogBonuses(catalog, pilot.ActiveShip.TypeId)
            ? new Dictionary<DogmaAttributeId, double>()
            : PilotFittingStore.ReadAttributes(catalog, pilot.ActiveShip.TypeId);
        return WeaponRules.ModuleAttributes(
            new(
                module.TypeId,
                module.GroupId,
                PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, module, calculation),
                levels,
                hull.GetValueOrDefault(DogmaAttributeId.RookieHybridOptimalBonus),
                loaded is null ? null : PilotFittingStore.ReadAttributes(catalog, loaded.TypeId),
                cooldown?.Kind == ModuleCooldownKind.Reload ? cooldown.ReadyAt : 0,
                hull.GetValueOrDefault(DogmaAttributeId.RookieProjectileDamageBonus),
                hull.GetValueOrDefault(DogmaAttributeId.RookieEnergyDamageBonus),
                hull.GetValueOrDefault(DogmaAttributeId.RookieEnergyCapacitorBonus)
            )
        );
    }

    private sealed record PaidShot(
        CombatWeapon Weapon,
        double Quality,
        double Capacitor,
        Magazine? Magazine,
        IReadOnlyDictionary<DogmaAttributeId, double> TargetAttributes
    );

    public CombatShot Fire(
        long pilotId,
        long moduleId,
        long targetId,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation = null,
        double? crystalRoll = null
    ) => FireVolley(pilotId, moduleId, targetId, velocity, now, roll, activation, crystalRoll, false).Shot;

    public CombatVolley FireBank(
        long pilotId,
        long moduleId,
        long targetId,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation = null
    ) => FireVolley(pilotId, moduleId, targetId, velocity, now, roll, activation, null, true);

    public CombatVolley FirePlayerBank(
        long pilotId,
        long moduleId,
        PilotWeaponTarget target,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation = null
    ) => FireVolley(pilotId, moduleId, target.ShipId, velocity, now, roll, activation, null, true, target);

    private sealed record TurretTarget(
        int SystemId,
        int TypeId,
        SpacePosition Position,
        SpacePosition Velocity,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes
    );

    private CombatVolley FireVolley(
        long pilotId,
        long moduleId,
        long targetId,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation,
        double? crystalRoll,
        bool banked,
        PilotWeaponTarget? playerTarget = null
    )
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("bank.volley");
        if (
            playerTarget is null
            && LiveCombatRuntime
                .Find(database.Path)
                ?.Fire(
                    pilotId,
                    moduleId,
                    targetId,
                    velocity,
                    now,
                    roll,
                    activation,
                    crystalRoll,
                    banked,
                    plan => SettleLiveFatal(plan, now)
                )
                is { } resident
        )
        {
            return resident;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        catalog.Database.OpenConnection();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var npc = playerTarget is null ? world.Set<WorldNpc>().Find(targetId) : null;
        Pilot? victim = null;
        AggressionDecision? aggression = null;
        TurretTarget target;
        if (playerTarget is not null)
        {
            victim = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.ActiveShipId == targetId);
            if (
                victim?.ActiveShip.Position is not { } saved
                || victim.Id == pilotId
                || catalog.Set<ItemType>().Single(t => t.Id == victim.ActiveShip.TypeId).GroupId == FleetDefinitions.CapsuleGroup
                || world.Set<SavedLogoff>().Any(l => l.PilotId == victim.Id && l.ExpiresAt <= now)
                || !double.IsFinite(playerTarget.Velocity.DistanceTo(SpacePosition.Zero))
            )
            {
                throw new InvalidDataException("The target ship is unavailable; capsule destruction is not supported.");
            }
            int system = CrimewatchPersistence.System(catalog, victim.ActiveShip);
            aggression = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim, system, now);
            var attributes = PilotFittingStore.ShipAttributes(world, catalog, victim.Id);
            if (PilotHealthStore.Read(world, targetId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
            {
                throw new InvalidDataException("The target ship is destroyed.");
            }
            target = new(system, victim.ActiveShip.TypeId, new(saved.X, saved.Y, saved.Z), playerTarget.Velocity, attributes);
        }
        else
        {
            if (npc is null || npc.DestroyedAt.HasValue || npc.RetiredAt.HasValue || npc.Navigation == NpcNavigationState.Warping)
            {
                throw new InvalidDataException("The target NPC is unavailable.");
            }
            target = new(
                npc.SystemId,
                npc.TypeId,
                NpcMotion.Position(npc),
                NpcMotion.Velocity(npc),
                PilotFittingStore.ReadAttributes(catalog, npc.TypeId)
            );
        }
        var members = banked ? PilotWeaponBankStore.Members(world, pilot.ActiveShipId, moduleId) : [moduleId];
        List<PaidShot> paid = [];
        foreach (long member in members)
        {
            double accuracy =
                member == members[0]
                    ? roll
                    : System.Security.Cryptography.RandomNumberGenerator.GetInt32(CrystalProbabilityResolution)
                        / (double)CrystalProbabilityResolution;
            paid.Add(Prepare(world, catalog, pilot, pilotId, member, target, velocity, now, accuracy, activation, crystalRoll));
        }
        if (paid.Select(p => p.Weapon.ChargeTypeId).Distinct().Count() > 1)
        {
            throw new InvalidDataException("Load the same ammunition in every grouped weapon.");
        }
        long cycleEnd = checked(now + (long)Math.Ceiling(paid.Max(p => p.Weapon.CycleMilliseconds) * TimeSpan.TicksPerMillisecond));
        if (victim is not null)
        {
            CrimewatchPersistence.Player(world, pilotId, victim.Id, target.SystemId, aggression!, now, cycleEnd);
        }
        else
        {
            CrimewatchPersistence.Npc(world, pilotId, target.SystemId, cycleEnd);
        }
        var damage = paid.Select(p => p.Weapon.Damage.Scale(p.Quality)).ToArray();
        DamageVector combined = new(
            damage.Sum(d => d.Em),
            damage.Sum(d => d.Explosive),
            damage.Sum(d => d.Kinetic),
            damage.Sum(d => d.Thermal)
        );
        CombatDamageSettlement settlement;
        InsurancePayout? insurance = null;
        if (victim is not null)
        {
            var defense = ShipHealth.DefenseFrom(target.Attributes);
            var applied = PilotHealthStore.Apply(world, targetId, defense, combined, now);
            var attacker = PilotInteractionHistoryStore.Player(world, pilot, pilot.ActiveShipId, paid[0].Weapon.TypeId);
            PilotInteractionHistoryStore.Damage(world, targetId, attacker, applied.Applied);
            if (applied.Remaining.Destroyed)
            {
                insurance = ShipLossPersistence.Apply(
                    world,
                    catalog,
                    victim,
                    null,
                    targetId,
                    target.SystemId,
                    victim.ActiveShip.Position!,
                    now,
                    lootRoll,
                    attacker
                );
            }
            settlement = new(new(targetId, defense, applied.Remaining), applied.Applied, applied.Remaining.Destroyed, null);
        }
        else
        {
            settlement = NpcDamagePersistence.Apply(
                world,
                catalog,
                npc!,
                pilot,
                pilot.ActiveShipId,
                paid[0].TargetAttributes,
                combined,
                now,
                paid[0].Weapon.TypeId
            );
        }
        world.SaveChanges();
        transaction.Commit();
        if (settlement.Destroyed && victim is null)
        {
            security.Publish();
        }
        var first = paid[0];
        double quality = paid.Average(p => p.Quality);
        // Preserve the damage/quality relation consumed by the native combat message adapter.
        var weapon =
            paid.Count == 1
                ? first.Weapon
                : first.Weapon with
                {
                    Damage = quality > 0 ? combined.Scale(1 / quality) : combined,
                    CycleMilliseconds = paid.Max(p => p.Weapon.CycleMilliseconds),
                };
        CombatShot shot = new(
            weapon,
            settlement.Applied,
            quality,
            settlement.Health,
            paid[^1].Capacitor,
            settlement.Destroyed,
            settlement.Reward is null ? null : Read(settlement.Reward),
            first.Magazine
        );
        if (victim is not null)
        {
            damageEvents.Publish(
                new(
                    victim.Id,
                    pilotId,
                    pilot.ActiveShipId,
                    shot.Health,
                    shot.Weapon.Damage.Scale(shot.Quality),
                    shot.Applied,
                    shot.Quality,
                    shot.Weapon.TypeId,
                    paid.Count > 1,
                    settlement.Destroyed ? lookup.FindPilot(victim.AccountId, victim.Id)! : null,
                    insurance
                )
            );
        }
        return new(shot, paid.Select(p => p.Weapon).ToArray(), paid.Where(p => p.Magazine is not null).Select(p => p.Magazine!).ToArray());
    }

    private static PaidShot Prepare(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        long pilotId,
        long moduleId,
        TurretTarget target,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation = null,
        double? crystalRoll = null
    )
    {
        if (!double.IsFinite(roll) || roll is < 0 or >= 1)
        {
            throw new InvalidDataException("Invalid weapon accuracy draw.");
        }
        var module = world.Set<OwnedItem>().Find(moduleId);
        bool ammoFed =
            module is not null
            && CombatDefinitions.IsSupportedAmmoTurret(
                catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId,
                PilotFittingStore.ReadAttributes(catalog, module.TypeId)
            );
        if (
            pilot.ActiveShip.Position is not { } position
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || (!ammoFed && module.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun)
            || !module.Singleton
            || !module.Online
            || !InventoryDefinitions.IsHighSlot(module.FlagId)
            || FittingCatalog.Definition(catalog, module.TypeId) is null
        )
        {
            throw new InvalidDataException("Use an online fitted supported turret on a locked ship while in space.");
        }
        bool energy = catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId == CombatDefinitions.EnergyWeaponGroup;
        if (activation is not null && activation != (ammoFed && !energy ? WeaponActivation.AmmoFed : WeaponActivation.Laser))
        {
            throw new InvalidDataException("Use the weapon's default firing effect.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        var targetType = catalog.Set<ItemType>().Single(t => t.Id == target.TypeId);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var separation = target.Position - new SpacePosition(position.X, position.Y, position.Z);
        double distance = Math.Max(0, separation.DistanceTo(SpacePosition.Zero) - (hull.Radius ?? 0) - (targetType.Radius ?? 0));
        string? revision = world.FitRevision(pilotId);
        var cachedShip = world.CachedFit(new(pilotId, pilot.ActiveShipId, 0), revision);
        var cachedWeapon = world.CachedFit(new(pilotId, pilot.ActiveShipId, moduleId), revision);
        var calculation = cachedShip is not null && cachedWeapon is not null ? null : FittingCalculation.Read(world, catalog, pilot);
        var ship = cachedShip ?? PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (target.SystemId != system || distance > ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
        {
            throw new InvalidDataException("The target is outside targeting range.");
        }
        var attributes = cachedWeapon ?? ModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, module), calculation);
        if (cachedWeapon is null)
        {
            world.StageFit(new(pilotId, pilot.ActiveShipId, moduleId), revision, attributes);
        }
        if (attributes.Values.Any(v => !double.IsFinite(v)) || attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) <= 0)
        {
            throw new InvalidDataException("Invalid weapon cycle attributes.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The weapon is still cycling.");
        }
        var loadedAmmo = ammoFed ? PilotAmmunitionStore.Contents(world, moduleId) : null;
        if (ammoFed && loadedAmmo is null)
        {
            throw new InvalidDataException("Load ammunition before firing the weapon.");
        }
        var damageAttributes = loadedAmmo is null ? attributes : PilotFittingStore.ReadAttributes(catalog, loadedAmmo.TypeId);
        int rounds =
            energy ? 1
            : ammoFed ? checked((int)attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate))
            : 0;
        if (
            ammoFed
            && (
                rounds <= 0
                || !energy && rounds != attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate)
                || loadedAmmo!.Quantity < rounds
                || !AmmunitionDefinitions.CompatibleTurretCharge(
                    attributes,
                    catalog.Set<ItemType>().Single(t => t.Id == loadedAmmo!.TypeId).GroupId,
                    damageAttributes
                )
                || !TurretChargeCatalog.Supported(
                    catalog,
                    loadedAmmo!.TypeId,
                    catalog.Set<ItemType>().Single(t => t.Id == loadedAmmo.TypeId).GroupId
                )
            )
        )
        {
            throw new InvalidDataException("Load enough compatible ammunition before firing the weapon.");
        }
        if (loadedAmmo is not null)
        {
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, loadedAmmo.TypeId);
        }
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot fire its weapons.");
        }
        if (
            damageAttributes.Values.Any(v => !double.IsFinite(v))
            || new[]
            {
                DogmaAttributeId.EmDamage,
                DogmaAttributeId.ExplosiveDamage,
                DogmaAttributeId.KineticDamage,
                DogmaAttributeId.ThermalDamage,
            }.Any(a => damageAttributes.GetValueOrDefault(a) < 0)
        )
        {
            throw new InvalidDataException("Invalid ammunition damage attributes.");
        }
        DamageVector damage = new(
            damageAttributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            damageAttributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            damageAttributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            damageAttributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        );
        CombatWeapon weapon = new(
            moduleId,
            module.TypeId,
            loadedAmmo?.TypeId ?? checked((int)attributes.GetValueOrDefault(DogmaAttributeId.BuiltInCharge)),
            damage.Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier)),
            attributes[DogmaAttributeId.WeaponCycleTime],
            attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
            attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed),
            attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            (int)(ammoFed && !energy ? DogmaEffect.ProjectileFired : DogmaEffect.LaserFired)
        );
        double chance = CombatDamage.HitChance(
            distance,
            CombatDamage.AngularVelocity(separation, velocity - target.Velocity),
            target.Attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            weapon.Optimal,
            weapon.Falloff,
            weapon.Tracking,
            weapon.SignatureResolution
        );
        double quality = CombatDamage.ShotQuality(chance, roll);
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, weapon.CapacitorNeed, now);
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond));
        Magazine? magazine = null;
        if (loadedAmmo is not null)
        {
            if (energy)
            {
                var wear = CrystalRules.Use(
                    damageAttributes,
                    loadedAmmo.CrystalDamage,
                    crystalRoll
                        ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32(CrystalProbabilityResolution)
                            / (double)CrystalProbabilityResolution
                );
                loadedAmmo.CrystalDamage = wear.Damage;
                if (wear.Destroyed)
                {
                    loadedAmmo.Quantity = 0;
                }
            }
            else
            {
                loadedAmmo.Quantity -= rounds;
            }
            magazine = new(
                module.Id,
                module.LocationId,
                module.FlagId,
                loadedAmmo.TypeId,
                loadedAmmo.Quantity,
                0,
                loadedAmmo.CrystalDamage
            );
            if (loadedAmmo.Quantity == 0)
            {
                world.Remove(loadedAmmo);
            }
        }
        world.SaveChanges();
        return new(weapon, quality, capacitor, magazine, target.Attributes);
    }

    private static CombatReward Read(NpcBounty b) => NpcDamagePersistence.Read(b);

    private CombatVolley SettleLiveFatal(Eden.Gameplay.Combat.LiveVolley plan, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == plan.Ship.PilotId);
        var npc = world.Set<WorldNpc>().Find(plan.Target.Id)!;
        // Previous object volleys have been projected. This transaction pays every member
        // and settles destruction, loot, history and rewards together.
        var before = LiveCombatRuntime.Find(database.Path)!.CurrentShip(plan.Ship.PilotId);
        LiveCombatRuntime.ProjectShip(world, before, plan.Ship);
        var attributes = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
        var settled = NpcDamagePersistence.Apply(
            world,
            catalog,
            npc,
            pilot,
            plan.Ship.ShipId,
            attributes,
            plan.Damage,
            now,
            plan.Volley.Shot.Weapon.TypeId
        );
        world.SaveChanges();
        transaction.Commit();
        security.Publish();
        return plan.Volley with
        {
            Shot = plan.Volley.Shot with
            {
                Health = settled.Health,
                Applied = settled.Applied,
                Destroyed = settled.Destroyed,
                Reward = settled.Reward is null ? null : Read(settled.Reward),
            },
        };
    }

    public IReadOnlyList<CombatReward> Rewards(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return Rewards(world, pilotId);
    }

    internal static IReadOnlyList<CombatReward> Rewards(WorldDbContext world, long pilotId, HistoryPeriod? period = null)
    {
        long from = period?.From ?? 0;
        long until = period?.Until ?? long.MaxValue;
        return world
            .Set<NpcBounty>()
            .AsNoTracking()
            .Where(b => b.PilotId == pilotId && b.Time >= from && b.Time < until)
            .OrderByDescending(b => b.Time)
            .ThenByDescending(b => b.Id)
            .AsEnumerable()
            .Select(Read)
            .ToArray();
    }
}
