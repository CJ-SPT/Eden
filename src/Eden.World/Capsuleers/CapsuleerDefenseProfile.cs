using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerDefenseEquipment(
    InventoryItem Item,
    DogmaEffect Effect,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    bool Active
);

internal sealed record CapsuleerDefenseProfile(bool Combat, CapsuleerDefenseEquipment[] Modules)
{
    internal RattingWeapon[] RattingWeapons { get; init; } = [];
    internal double TargetRange { get; init; }

    internal static CapsuleerDefenseProfile Read(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        var pilot = world.Pilots.Include(value => value.ActiveShip).Single(value => value.Id == pilotId);
        var fit = FittingCalculation.Read(world, catalog, pilot);
        var active = world
            .Set<ActiveFittedModule>()
            .Where(module => module.ShipId == pilot.ActiveShipId && module.ExpiresAt == 0)
            .Select(module => module.ModuleId)
            .ToHashSet();
        bool Usable(InventoryItem item)
        {
            try
            {
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
        var modules = fit
            .Modules.Where(module => module.Item.Online && Usable(PilotInventoryStore.Read(catalog, module.Item)))
            .OrderBy(module => module.Item.FlagId)
            .Select(module => new CapsuleerDefenseEquipment(
                PilotInventoryStore.Read(catalog, module.Item),
                FittingCatalog.DefaultEffect(catalog, module.Item.TypeId) ?? DogmaEffect.Online,
                fit.Graph.Attributes(module.Item.Id),
                active.Contains(module.Item.Id)
            ))
            .ToArray();
        int tech = (int)catalog.FittingAttributes(pilot.ActiveShip.TypeId).GetValueOrDefault(DogmaAttributeId.TechLevel, 1);
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        return new CapsuleerDefenseProfile(
            CapsuleerDefenseRules.CombatShip((HullClass)hull.GroupId, tech, true)
                && pilot.ActiveShip.TypeId != CapsuleerCommunityStore.VentureType
                && modules.All(module => module.Effect != DogmaEffect.MiningLaser),
            modules
        )
        {
            RattingWeapons = ReadRatting(world, catalog, pilotId, pilot.ActiveShipId, fit, modules),
            TargetRange = fit.Graph.Read(pilot.ActiveShipId, DogmaAttributeId.MaxTargetRange),
        };
    }

    private static RattingWeapon[] ReadRatting(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long ship,
        FittingCalculation fit,
        CapsuleerDefenseEquipment[] modules
    )
    {
        List<RattingWeapon> result = [];
        var cargo = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship).ToArray();
        foreach (var module in modules)
        {
            bool missile = module.Effect == DogmaEffect.UseMissiles;
            if (!missile && module.Effect is not (DogmaEffect.LaserFired or DogmaEffect.ProjectileFired))
            {
                continue;
            }
            var charge = world.Set<OwnedItem>().SingleOrDefault(i => i.LocationId == module.Item.Id);
            var attributes = module.Attributes;
            var damage = charge is null ? attributes : fit.Graph.Attributes(charge.Id);
            double cycle = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
            if (cycle <= 0 || charge is null && module.Item.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun)
            {
                continue;
            }
            MissileWarhead? warhead = null;
            double range = attributes.GetValueOrDefault(DogmaAttributeId.MaxRange);
            DamageVector vector;
            try
            {
                if (missile)
                {
                    warhead = MissileRules.Warhead(damage, fit.Graph.Read(pilotId, DogmaAttributeId.MissileDamageMultiplier));
                    vector = warhead.Damage;
                    range = MissileChargeCatalog.Drive(catalog, charge!.TypeId, damage).NominalRange;
                }
                else
                {
                    vector = new DamageVector(
                        damage.GetValueOrDefault(DogmaAttributeId.EmDamage),
                        damage.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
                        damage.GetValueOrDefault(DogmaAttributeId.KineticDamage),
                        damage.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
                    ).Scale(attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier));
                }
            }
            catch (InvalidDataException)
            {
                continue;
            }
            int rounds = Math.Max(1, (int)attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate));
            bool crystal =
                charge is not null
                && Eden.Gameplay.Ammunition.AmmunitionDefinitions.IsCrystalGroup(catalog.RequireItem(charge.TypeId).GroupId);
            int shots =
                charge is null || crystal
                    ? int.MaxValue
                    : checked(
                        (int)
                            Math.Min(
                                int.MaxValue,
                                (
                                    charge.Quantity
                                    + cargo
                                        .Where(i => i.FlagId == (int)InventoryFlag.Cargo && i.TypeId == charge.TypeId)
                                        .Sum(i => i.Quantity)
                                ) / rounds
                            )
                    );
            if (crystal && damage.GetValueOrDefault(DogmaAttributeId.CrystalsGetDamaged) > 0)
            {
                double wear = damage.GetValueOrDefault(DogmaAttributeId.CrystalVolatilityDamage);
                shots = wear > 0 ? (int)Math.Max(0, Math.Floor((1 - charge!.CrystalDamage) / wear)) : 0;
            }
            result.Add(
                new(
                    new(
                        module.Item.Id,
                        module.Item.TypeId,
                        charge?.TypeId ?? 0,
                        vector,
                        cycle,
                        range,
                        attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
                        attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed),
                        attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution),
                        attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed)
                    ),
                    shots,
                    warhead,
                    charge is null || crystal ? 0 : checked((int)charge.Quantity / rounds)
                )
            );
        }
        if (DroneRules.SupportsHull(world.Ships.Find(ship)!.TypeId))
        {
            double bandwidth = fit.Graph.Read(ship, DogmaAttributeId.DroneBandwidth);
            int remaining = DroneCatalog.ControlLimit(fit);
            foreach (var stack in cargo.Where(i => i.FlagId == (int)InventoryFlag.DroneBay).OrderBy(i => i.Id))
            {
                try
                {
                    PilotSkillReader.CheckRequirements(world, catalog, pilotId, stack.TypeId);
                    var drone = DroneCatalog.Skilled(world, catalog, pilotId, stack.TypeId);
                    int count = (int)Math.Min(stack.Quantity, Math.Min(remaining, Math.Floor(bandwidth / drone.Bandwidth)));
                    if (count > 0 && drone.Kind != CombatDroneKind.Sentry)
                    {
                        result.Add(
                            new(
                                new(
                                    -stack.Id,
                                    stack.TypeId,
                                    0,
                                    drone.Damage.Scale(count),
                                    drone.CycleMilliseconds,
                                    drone.Optimal,
                                    drone.Falloff,
                                    drone.Tracking,
                                    drone.SignatureReference,
                                    0
                                ),
                                int.MaxValue
                            )
                        );
                        remaining -= count;
                        bandwidth -= count * drone.Bandwidth;
                    }
                }
                catch (InvalidDataException)
                {
                    // Unsupported drones never count toward capability.
                }
            }
        }
        return result.ToArray();
    }
}
