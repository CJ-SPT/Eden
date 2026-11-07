using System.Collections.Frozen;
using System.Text.Json;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Repair;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private LiveCombatShip LoadShip(long pilotId, long now)
    {
        using var phase = SessionPerformance.Phase("combat.hydrate.ship");
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        catalog.Database.OpenConnection();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        var health = PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(attributes), now);
        var savedHealth = world.Set<ShipHealthState>().Find(pilot.ActiveShipId);
        var position = pilot.ActiveShip.Position;
        var fitted = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == pilot.ActiveShipId).ToArray();
        var turrets = new Dictionary<long, LiveTurret>();
        foreach (var module in fitted)
        {
            _itemPilots[module.Id] = pilotId;
            if (!InventoryDefinitions.IsHighSlot(module.FlagId))
            {
                continue;
            }
            var type = catalog.Set<ItemType>().Single(t => t.Id == module.TypeId);
            var baseAttributes = PilotFittingStore.ReadAttributes(catalog, module.TypeId);
            bool ammoFed = CombatDefinitions.IsSupportedAmmoTurret(type.GroupId, baseAttributes);
            if (!ammoFed && module.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun)
            {
                continue;
            }
            var charge = PilotAmmunitionStore.Contents(world, module.Id);
            if (charge is not null)
            {
                _itemPilots[charge.Id] = pilotId;
            }
            var cooldown = world.Set<ModuleCooldown>().Find(module.Id);
            bool energy = type.GroupId == CombatDefinitions.EnergyWeaponGroup;
            var values = PilotCombatStore.ModuleAttributes(world, catalog, pilotId, PilotInventoryStore.Read(catalog, module), calculation);
            var damage = charge is null ? values : PilotFittingStore.ReadAttributes(catalog, charge.TypeId);
            int rounds =
                energy ? 1
                : ammoFed ? checked((int)values.GetValueOrDefault(DogmaAttributeId.ChargeRate))
                : 0;
            string? error = null;
            try
            {
                if (!module.Singleton || !module.Online || FittingCatalog.Definition(catalog, module.TypeId) is null)
                {
                    throw new InvalidDataException("Use an online fitted supported turret on a locked NPC while in space.");
                }
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
                if (charge is not null)
                {
                    int group = catalog.Set<ItemType>().Single(t => t.Id == charge.TypeId).GroupId;
                    if (
                        !AmmunitionDefinitions.CompatibleTurretCharge(values, group, damage)
                        || !TurretChargeCatalog.Supported(catalog, charge.TypeId, group)
                    )
                    {
                        throw new InvalidDataException("Load enough compatible ammunition before firing the weapon.");
                    }
                    PilotSkillReader.CheckRequirements(world, catalog, pilotId, charge.TypeId);
                }
                if (
                    values.Values.Any(v => !double.IsFinite(v))
                    || damage.Values.Any(v => !double.IsFinite(v))
                    || values.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) <= 0
                    || values.GetValueOrDefault(DogmaAttributeId.CapacitorNeed) < 0
                    || new[]
                    {
                        DogmaAttributeId.EmDamage,
                        DogmaAttributeId.ExplosiveDamage,
                        DogmaAttributeId.KineticDamage,
                        DogmaAttributeId.ThermalDamage,
                    }.Any(a => damage.GetValueOrDefault(a) < 0)
                    || ammoFed && !energy && rounds != values.GetValueOrDefault(DogmaAttributeId.ChargeRate)
                )
                {
                    throw new InvalidDataException("Invalid weapon or ammunition attributes.");
                }
            }
            catch (InvalidDataException exception)
            {
                error = exception.Message;
            }
            DamageVector vector = new(
                damage.GetValueOrDefault(DogmaAttributeId.EmDamage),
                damage.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
                damage.GetValueOrDefault(DogmaAttributeId.KineticDamage),
                damage.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
            );
            CombatWeapon weapon = new(
                module.Id,
                module.TypeId,
                charge?.TypeId ?? checked((int)values.GetValueOrDefault(DogmaAttributeId.BuiltInCharge)),
                vector.Scale(values.GetValueOrDefault(DogmaAttributeId.DamageMultiplier)),
                values.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime),
                values.GetValueOrDefault(DogmaAttributeId.MaxRange),
                values.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
                values.GetValueOrDefault(DogmaAttributeId.TrackingSpeed),
                values.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution),
                values.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
                (int)(ammoFed && !energy ? DogmaEffect.ProjectileFired : DogmaEffect.LaserFired)
            );
            turrets[module.Id] = new(
                new(weapon, module.FlagId, ammoFed, energy, rounds, damage.ToFrozenDictionary(), error),
                charge?.Id,
                charge?.Quantity ?? 0,
                charge?.CrystalDamage ?? 0,
                cooldown?.ReadyAt ?? 0
            );
        }
        var banks = PilotWeaponBankStore
            .Read(world, pilot.ActiveShipId)
            .ToDictionary(b => b.MasterId, b => (IReadOnlyList<long>)b.SlaveIds.Prepend(b.MasterId).ToArray());
        var repairs = world
            .Set<PendingLocalRepair>()
            .Where(r => r.ShipId == pilot.ActiveShipId)
            .ToArray()
            .Select(r => new LiveRepair(r.ModuleId, r.CompletesAt, r.Amount, r.Layer))
            .ToArray();
        LiveCombatShip result = new(
            pilotId,
            pilot.ActiveShipId,
            pilot.CorporationId,
            pilot.ActiveShip.TypeId,
            position?.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId,
            position is null ? null : new SpacePosition(position.X, position.Y, position.Z),
            hull.Radius ?? 0,
            attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
            ShipResources.Capacitor(world, pilot.ActiveShipId, attributes, now),
            now,
            new(
                health.Defense,
                health.Remaining,
                now,
                ArmorDamage: savedHealth?.ArmorDamage,
                StructureDamage: savedHealth?.StructureDamage
            ),
            repairs,
            turrets,
            banks,
            hull.GroupId,
            attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            Contributions(world, pilot.ActiveShipId),
            world.Set<SavedCrimewatch>().Find(pilotId)?.Timers().Npc ?? new CombatTimer()
        );
        _shipPilots[pilot.ActiveShipId] = pilotId;
        transaction.Commit();
        return result;
    }

    private LiveCombatShip ReloadShip(LiveCombatShip ship, long now)
    {
        using var phase = SessionPerformance.Phase("combat.refresh.ship");
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var health = world.Set<ShipHealthState>().Find(ship.ShipId);
        var energy = world.Set<ShipEnergy>().Find(ship.ShipId);
        var position = world.Positions.Find(ship.ShipId);
        var repairs = world.Set<PendingLocalRepair>().Where(r => r.ShipId == ship.ShipId).ToArray();
        var ids = ship.Turrets.Keys.ToArray();
        var charges = world.Set<OwnedItem>().Where(i => ids.Contains(i.LocationId)).ToDictionary(i => i.LocationId);
        var cooldowns = world.Set<ModuleCooldown>().Where(c => ids.Contains(c.ModuleId)).ToDictionary(c => c.ModuleId);
        var defense = ship.Health.Defense;
        var turrets = ship.Turrets.ToDictionary(
            p => p.Key,
            p =>
            {
                var charge = charges.GetValueOrDefault(p.Key);
                return p.Value with
                {
                    ChargeItemId = charge?.Id,
                    Quantity = charge?.Quantity ?? 0,
                    CrystalDamage = charge?.CrystalDamage ?? 0,
                    ReadyAt = cooldowns.GetValueOrDefault(p.Key)?.ReadyAt ?? 0,
                };
            }
        );
        var contributions = Contributions(world, ship.ShipId);
        transaction.Commit();
        return ship with
        {
            Position = position is null ? null : new(position.X, position.Y, position.Z),
            SystemId = position?.SystemId ?? ship.SystemId,
            Capacitor = energy?.Charge ?? ship.CapacitorCapacity,
            CapacitorAt = energy?.UpdatedAt ?? now,
            Health = new(
                defense,
                health is null
                    ? defense.Undamaged
                    : new(
                        health.ShieldCharge,
                        Math.Max(0, defense.Armor - health.ArmorDamage),
                        Math.Max(0, defense.Structure - health.StructureDamage)
                    ),
                health?.UpdatedAt ?? now,
                ArmorDamage: health?.ArmorDamage,
                StructureDamage: health?.StructureDamage
            ),
            Repairs = repairs.Select(r => new LiveRepair(r.ModuleId, r.CompletesAt, r.Amount, r.Layer)).ToArray(),
            Turrets = turrets,
            Contributions = contributions,
            NpcAggression = world.Set<SavedCrimewatch>().Find(ship.PilotId)?.Timers().Npc ?? ship.NpcAggression,
        };
    }

    private LiveCombatNpc? LoadNpc(long id, long now)
    {
        using var phase = SessionPerformance.Phase("combat.hydrate.npc");
        using WorldDbContext world = new(database.Path);
        var npc = world.Set<WorldNpc>().Find(id);
        if (npc is null)
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        catalog.Database.OpenConnection();
        var profile = _simulationProfiles.Read(catalog, npc.TypeId);
        var type = profile.Type;
        var attributes = profile.Attributes;
        var defense = profile.Defense;
        double missileRange = 0;
        if (attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType) is > 0 and var missile)
        {
            int chargeId = checked((int)missile);
            var charge = PilotFittingStore.ReadAttributes(catalog, chargeId);
            missileRange = NpcMissileRules.Drive(MissileChargeCatalog.Drive(catalog, chargeId, charge), attributes).NominalRange;
        }
        var contributions = Contributions(world, id);
        var mining = world.Set<WorldNpcMiningMember>().Find(id);
        return new(
            npc.Id,
            npc.TypeId,
            npc.SystemId,
            NpcMotion.Position(npc),
            NpcMotion.Velocity(npc),
            type.Radius ?? 0,
            attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            new(
                defense,
                new(npc.ShieldCharge, Math.Max(0, defense.Armor - npc.ArmorDamage), Math.Max(0, defense.Structure - npc.StructureDamage)),
                npc.UpdatedAt,
                ArmorDamage: npc.ArmorDamage,
                StructureDamage: npc.StructureDamage
            ),
            npc.DestroyedAt is null && npc.RetiredAt is null,
            contributions,
            npc.Role,
            npc.EncounterId,
            attributes.GetValueOrDefault(
                DogmaAttributeId.EntityAttackDelayMinimum,
                npc.Role == NpcRole.Pirate ? 0 : NpcGuardRules.ReactionMilliseconds
            ),
            npc.CorporationId,
            npc.FactionId,
            NpcMotion.Home(npc, catalog),
            attributes
        )
        {
            Navigation = npc.Navigation,
            MotionAt = npc.MotionAt,
            Mass = type.Mass ?? 0,
            GroupId = type.GroupId,
            CategoryId = type.Group.CategoryId,
            Name = type.Name,
            MissileRange = missileRange,
            RepairProfile = NpcRepairRules.Profile(attributes),
            RepairState = new(npc.ShieldRepairAt, npc.ArmorRepairAt, npc.RepairRevision),
            ElectronicWarfare = profile.ElectronicWarfare,
            Transit = mining?.Transit(npc),
            Activity = mining?.Activity(
                npc,
                attributes.GetValueOrDefault(DogmaAttributeId.BehaviorMiningDuration),
                now,
                checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType))
            ),
        };
    }

    private static IReadOnlyDictionary<long, CombatParty> Contributions(WorldDbContext world, long id) =>
        world
            .Set<SavedCombatDamage>()
            .Where(d => d.VictimId == id)
            .AsNoTracking()
            .ToArray()
            .ToDictionary(
                d => d.AttackerId,
                d =>
                    (JsonSerializer.Deserialize<CombatParty>(d.PartyJson) ?? throw new InvalidDataException("Invalid saved attacker.")) with
                    {
                        Damage = d.Damage,
                    }
            );
}
