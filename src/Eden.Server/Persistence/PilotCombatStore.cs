using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Protocol;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotCombatStore(WorldDatabase database, SecurityStatusSignal security) : IPilotCombat
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

    private CombatVolley FireVolley(
        long pilotId,
        long moduleId,
        long targetId,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation,
        double? crystalRoll,
        bool banked
    )
    {
        using var phase = Eden.Protocol.PilotSessions.SessionPerformance.Phase("bank.volley");
        if (
            LiveCombatRuntime
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
                ) is
            { } resident
        )
        {
            return resident;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        catalog.Database.OpenConnection();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var members = banked ? PilotWeaponBankStore.Members(world, pilot.ActiveShipId, moduleId) : [moduleId];
        List<PaidShot> paid = [];
        foreach (long member in members)
        {
            double accuracy =
                member == members[0]
                    ? roll
                    : System.Security.Cryptography.RandomNumberGenerator.GetInt32(CrystalProbabilityResolution)
                        / (double)CrystalProbabilityResolution;
            paid.Add(Prepare(world, catalog, pilot, pilotId, member, targetId, velocity, now, accuracy, activation, crystalRoll));
        }
        if (paid.Select(p => p.Weapon.ChargeTypeId).Distinct().Count() > 1)
        {
            throw new InvalidDataException("Load the same ammunition in every grouped weapon.");
        }
        var npc = world.Set<WorldNpc>().Find(targetId)!;
        var damage = paid.Select(p => p.Weapon.Damage.Scale(p.Quality)).ToArray();
        DamageVector combined = new(
            damage.Sum(d => d.Em),
            damage.Sum(d => d.Explosive),
            damage.Sum(d => d.Kinetic),
            damage.Sum(d => d.Thermal)
        );
        var settlement = NpcDamagePersistence.Apply(
            world,
            catalog,
            npc,
            pilot,
            pilot.ActiveShipId,
            paid[0].TargetAttributes,
            combined,
            now,
            paid[0].Weapon.TypeId
        );
        world.SaveChanges();
        transaction.Commit();
        if (settlement.Destroyed)
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
        return new(shot, paid.Select(p => p.Weapon).ToArray(), paid.Where(p => p.Magazine is not null).Select(p => p.Magazine!).ToArray());
    }

    private static PaidShot Prepare(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        long pilotId,
        long moduleId,
        long targetId,
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
        var npc = world.Set<WorldNpc>().Find(targetId);
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
            || npc is null
            || npc.DestroyedAt.HasValue
            || npc.RetiredAt.HasValue
            || npc.Navigation == NpcNavigationState.Warping
        )
        {
            throw new InvalidDataException("Use an online fitted supported turret on a locked NPC while in space.");
        }
        bool energy = catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId == CombatDefinitions.EnergyWeaponGroup;
        if (activation is not null && activation != (ammoFed && !energy ? WeaponActivation.AmmoFed : WeaponActivation.Laser))
        {
            throw new InvalidDataException("Use the weapon's default firing effect.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        var npcType = catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var separation = new SpacePosition(npc.X - position.X, npc.Y - position.Y, npc.Z - position.Z);
        double distance = Math.Max(0, separation.DistanceTo(SpacePosition.Zero) - (hull.Radius ?? 0) - (npcType.Radius ?? 0));
        string? revision = world.FitRevision(pilotId);
        var cachedShip = world.CachedFit(new(pilotId, pilot.ActiveShipId, 0), revision);
        var cachedWeapon = world.CachedFit(new(pilotId, pilot.ActiveShipId, moduleId), revision);
        var calculation = cachedShip is not null && cachedWeapon is not null ? null : FittingCalculation.Read(world, catalog, pilot);
        var ship = cachedShip ?? PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (npc.SystemId != system || distance > ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
        {
            throw new InvalidDataException("The NPC is outside targeting range.");
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
        var target = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
        double chance = CombatDamage.HitChance(
            distance,
            CombatDamage.AngularVelocity(separation, velocity - NpcMotion.Velocity(npc)),
            target.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
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
        return new(weapon, quality, capacitor, magazine, target);
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

    internal static IReadOnlyList<CombatReward> Rewards(WorldDbContext world, long pilotId)
    {
        return world
            .Set<NpcBounty>()
            .AsNoTracking()
            .Where(b => b.PilotId == pilotId)
            .OrderByDescending(b => b.Time)
            .ThenByDescending(b => b.Id)
            .AsEnumerable()
            .Select(Read)
            .ToArray();
    }
}
