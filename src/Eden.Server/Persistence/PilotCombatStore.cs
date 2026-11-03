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
internal sealed class PilotCombatStore(WorldDatabase database) : IPilotCombat
{
    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        return ModuleAttributes(world, catalog, pilotId, module);
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var levels = Enum.GetValues<CombatSkill>().ToDictionary(s => s, s => PilotSkillReader.Level(world, catalog, pilotId, (int)s));
        var loaded = PilotAmmunitionStore.Contents(world, module.Id);
        var cooldown = world.Set<ModuleCooldown>().Find(module.Id);
        return WeaponRules.ModuleAttributes(
            new(
                module.TypeId,
                module.GroupId,
                PilotFittingStore.ReadAttributes(catalog, module.TypeId),
                levels,
                PilotFittingStore
                    .ReadAttributes(catalog, pilot.ActiveShip.TypeId)
                    .GetValueOrDefault(DogmaAttributeId.RookieHybridOptimalBonus),
                loaded is null ? null : PilotFittingStore.ReadAttributes(catalog, loaded.TypeId),
                cooldown?.Kind == ModuleCooldownKind.Reload ? cooldown.ReadyAt : 0
            )
        );
    }

    public CombatShot Fire(
        long pilotId,
        long moduleId,
        long targetId,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation = null
    )
    {
        if (!double.IsFinite(roll) || roll is < 0 or >= 1)
        {
            throw new InvalidDataException("Invalid weapon accuracy draw.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        var npc = world.Set<WorldNpc>().Find(targetId);
        bool hybrid =
            module is not null
            && CombatDefinitions.IsSupportedHybrid(
                catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId,
                PilotFittingStore.ReadAttributes(catalog, module.TypeId)
            );
        if (
            pilot.ActiveShip.Position is not { } position
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || (!hybrid && module.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun)
            || !module.Singleton
            || !module.Online
            || !InventoryDefinitions.IsHighSlot(module.FlagId)
            || npc is null
            || npc.DestroyedAt.HasValue
        )
        {
            throw new InvalidDataException("Use an online fitted supported turret on a locked NPC while in space.");
        }
        if (activation is not null && activation != (hybrid ? WeaponActivation.Hybrid : WeaponActivation.Civilian))
        {
            throw new InvalidDataException("Use the weapon's default firing effect.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        var npcType = catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var separation = new SpacePosition(npc.X - position.X, npc.Y - position.Y, npc.Z - position.Z);
        double distance = Math.Max(0, separation.DistanceTo(SpacePosition.Zero) - (hull.Radius ?? 0) - (npcType.Radius ?? 0));
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        if (npc.SystemId != system || distance > ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
        {
            throw new InvalidDataException("The NPC is outside targeting range.");
        }
        var attributes = ModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, module));
        if (attributes.Values.Any(v => !double.IsFinite(v)) || attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) <= 0)
        {
            throw new InvalidDataException("Invalid weapon cycle attributes.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && now < cooldown.ReadyAt)
        {
            throw new InvalidDataException("The weapon is still cycling.");
        }
        var loadedAmmo = hybrid ? PilotAmmunitionStore.Contents(world, moduleId) : null;
        if (hybrid && loadedAmmo is null)
        {
            throw new InvalidDataException("Load ammunition before firing the weapon.");
        }
        var damageAttributes = loadedAmmo is null ? attributes : PilotFittingStore.ReadAttributes(catalog, loadedAmmo.TypeId);
        int rounds = hybrid ? checked((int)attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate)) : 0;
        if (
            hybrid
            && (
                rounds <= 0
                || rounds != attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate)
                || loadedAmmo!.Quantity < rounds
                || damageAttributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) != AmmunitionDefinitions.SmallChargeSize
                || catalog.Set<ItemType>().Single(t => t.Id == loadedAmmo.TypeId).GroupId != AmmunitionDefinitions.HybridChargeGroup
            )
        )
        {
            throw new InvalidDataException("Load enough compatible ammunition before firing the weapon.");
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
            (int)(hybrid ? DogmaEffect.ProjectileFired : DogmaEffect.CivilianWeapon)
        );
        var target = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
        var defense = ShipHealth.DefenseFrom(target);
        HullDamage health = new(
            ChargedResource.Recharged(
                npc.ShieldCharge,
                defense.Shield,
                defense.ShieldRechargeMilliseconds,
                Math.Max(0, (now - npc.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
            ),
            Math.Max(0, defense.Armor - npc.ArmorDamage),
            Math.Max(0, defense.Structure - npc.StructureDamage)
        );
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
        var result = CombatDamage.Apply(health, defense, weapon.Damage.Scale(quality));
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, weapon.CapacitorNeed, now);
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond));
        npc.ShieldCharge = result.Remaining.Shield;
        npc.ArmorDamage = defense.Armor - result.Remaining.Armor;
        npc.StructureDamage = defense.Structure - result.Remaining.Structure;
        npc.UpdatedAt = Math.Max(now, npc.UpdatedAt);
        NpcBounty? reward = null;
        var aggression = world.Set<NpcAggression>().Find(npc.Id);
        if (result.Remaining.Destroyed)
        {
            if (aggression is not null)
            {
                world.Remove(aggression);
            }
            npc.DestroyedAt = now;
            WreckStore.Create(world, catalog, npc, pilot, now);
            double bounty = target.GetValueOrDefault(DogmaAttributeId.EntityKillBounty);
            if (!double.IsFinite(bounty) || bounty < 0)
            {
                throw new InvalidDataException("Invalid NPC bounty.");
            }
            long cents = checked((long)Math.Round(bounty * MarketDefinitions.CentsPerIsk, MidpointRounding.ToEven));
            if (cents > 0)
            {
                var wallet = world.Set<PilotWallet>().Find(pilotId) ?? world.Set<PilotWallet>().Add(new() { PilotId = pilotId }).Entity;
                wallet.Cents = checked(wallet.Cents + cents);
                reward = world
                    .Set<NpcBounty>()
                    .Add(
                        new()
                        {
                            PilotId = pilotId,
                            NpcId = npc.Id,
                            TypeId = npc.TypeId,
                            SystemId = npc.SystemId,
                            Time = now,
                            AmountCents = cents,
                            BalanceCents = wallet.Cents,
                        }
                    )
                    .Entity;
            }
        }
        else if (aggression is null && target.TryGetValue(DogmaAttributeId.EntityAttackDelayMinimum, out double delay))
        {
            // A player attack can claim an idle NPC before its detection tick.
            // Existing targets and their paid reaction/cycle deadlines persist.
            world.Set<NpcAggression>().Add(NpcAggression.Start(npc.Id, pilot.ActiveShipId, delay, now));
        }
        Magazine? magazine = null;
        if (loadedAmmo is not null)
        {
            loadedAmmo.Quantity -= rounds;
            magazine = new(module.Id, module.LocationId, module.FlagId, loadedAmmo.TypeId, loadedAmmo.Quantity, 0);
            if (loadedAmmo.Quantity == 0)
            {
                world.Remove(loadedAmmo);
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return new(
            weapon,
            result.Applied,
            quality,
            new(npc.Id, defense, result.Remaining),
            capacitor,
            result.Remaining.Destroyed,
            reward is null ? null : Read(reward),
            magazine
        );
    }

    private static CombatReward Read(NpcBounty b) =>
        new(b.Id, b.PilotId, b.NpcId, b.TypeId, b.SystemId, b.Time, b.AmountCents, b.BalanceCents);

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
