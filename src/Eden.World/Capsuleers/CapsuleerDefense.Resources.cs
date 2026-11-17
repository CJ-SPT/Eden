using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;
using Eden.World.Thermal;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerChargeProfile(int Group, IReadOnlyDictionary<DogmaAttributeId, double> Attributes);

internal sealed record CapsuleerAmmunitionReload(long ReadyAt, long[] ModuleIds);

internal sealed partial class CapsuleerDefense
{
    private static readonly CatalogDefinition<OverloadProfile?> s_overloads = new("capsuleer-overload", OverloadCatalog.Definition);
    private static readonly CatalogDefinition<CapsuleerChargeProfile?> s_charges = new(
        "capsuleer-charge",
        (catalog, type) =>
        {
            if (catalog.FittingType(type) is not { CategoryId: (int)InventoryCategory.Charge } charge)
            {
                return null;
            }
            int group = charge.GroupId;
            return TurretChargeCatalog.Supported(catalog, type, group) ? new(group, catalog.FittingAttributes(type)) : null;
        }
    );

    private CapsuleerAmmunitionReload? ChooseAmmunition(
        DockedPilot pilot,
        CapsuleerDefenseEquipment module,
        CombatWeapon weapon,
        bool loaded,
        CapsuleerAttacker target,
        Eden.Gameplay.Flight.SpacePosition velocity,
        long now
    )
    {
        if (
            weapon.ChargeTypeId <= 0
            || weapon.Spool is not null
            || weapon.Vorton is not null
            || module.Effect is not (DogmaEffect.LaserFired or DogmaEffect.ProjectileFired)
            || database.Catalog.Definition(s_charges, weapon.ChargeTypeId) is not { } current
        )
        {
            return null;
        }
        List<PvpAmmunitionOption> choices = [];
        foreach (
            var stack in inventory
                .List(pilot.PilotId, pilot.ShipId)
                .Where(item => item.FlagId == (int)InventoryFlag.Cargo && item.CategoryId == (int)InventoryCategory.Charge)
        )
        {
            var charge = database.Catalog.Definition(s_charges, stack.TypeId);
            if (charge is null || !AmmunitionDefinitions.CompatibleTurretCharge(module.Attributes, charge.Group, charge.Attributes))
            {
                continue;
            }
            double Ratio(DogmaAttributeId attribute) =>
                charge.Attributes.GetValueOrDefault(attribute, 1) / Math.Max(.001, current.Attributes.GetValueOrDefault(attribute, 1));
            double scale = module.Attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier);
            var attributes = charge.Attributes;
            var candidate = weapon with
            {
                ChargeTypeId = stack.TypeId,
                Damage = new DamageVector(
                    attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
                    attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
                    attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
                    attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
                ).Scale(scale),
                Optimal = weapon.Optimal * Ratio(DogmaAttributeId.WeaponRangeMultiplier),
                Falloff = weapon.Falloff * Ratio(DogmaAttributeId.ChargeFalloffMultiplier),
                Tracking = weapon.Tracking * Ratio(DogmaAttributeId.ChargeTrackingMultiplier),
            };
            int rounds = Math.Max(1, (int)module.Attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate));
            choices.Add(new(stack.Id, stack.TypeId, stack.Quantity / rounds, candidate));
        }
        double distance = Math.Max(0, pilot.Position!.DistanceTo(target.Position) - pilot.Flight.ShipRadius - target.Radius);
        double angular = CombatDamage.AngularVelocity(target.Position - pilot.Position!, target.Velocity - velocity);
        long selected = PvpResources.Ammunition(
            weapon,
            loaded,
            choices,
            distance,
            angular,
            target.Signature,
            module.Attributes.GetValueOrDefault(DogmaAttributeId.ReloadTime)
        );
        if (selected == 0)
        {
            return null;
        }
        try
        {
            var transfer = ammunition.Load(pilot.PilotId, pilot.ShipId, [module.Item.Id], [selected], pilot.ShipId, now);
            long ready = checked(now + Math.Max(1, (long)Math.Ceiling(transfer.ReloadMilliseconds * TimeSpan.TicksPerMillisecond)));
            return new(ready, transfer.Magazines.Select(change => change.Current?.ModuleId).OfType<long>().Distinct().ToArray());
        }
        catch (InvalidDataException)
        {
            // Skills, bank membership and the current paid cycle remain the normal loader's authority.
            return null;
        }
    }

    private static double HeatedRange(CapsuleerDefenseEquipment module)
    {
        double range = module.Attributes.GetValueOrDefault(DogmaAttributeId.MaxRange);
        var bonus = module.Overload?.Bonuses.SingleOrDefault(bonus => bonus.Attribute == DogmaAttributeId.MaxRange);
        return bonus is null ? range : range * (1 + module.Attributes.GetValueOrDefault(bonus.SourceAttribute) / 100);
    }

    private bool Heat(
        DockedPilot pilot,
        CapsuleerDefenseEquipment module,
        CapsuleerModuleBudget budget,
        CapsuleerAttacker target,
        bool emergency,
        long now
    )
    {
        if (
            target.Npc
            || module.Overload is null
            || budget.HeatedCycles >= PvpResources.MaximumHeatedCycles
            || module.Effect
                is not (
                    DogmaEffect.LaserFired
                    or DogmaEffect.ProjectileFired
                    or DogmaEffect.UseMissiles
                    or DogmaEffect.WarpDisruptor
                    or DogmaEffect.WarpScrambler
                )
        )
        {
            return false;
        }
        double distance = Math.Max(0, pilot.Position!.DistanceTo(target.Position) - pilot.Flight.ShipRadius - target.Radius);
        return PvpResources.Heat(
            module.Effect,
            budget.HeatedCycles,
            shipHeat.Read(pilot.PilotId, pilot.ShipId, now),
            module.Item.Id,
            emergency,
            distance,
            module.Attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            HeatedRange(module)
        );
    }

    private void SelectHeat(long pilot, long ship, long module, int type, bool selected, long now)
    {
        if (overloading.Requested(pilot, ship).Contains(module) == selected)
        {
            return;
        }
        if (database.Catalog.Definition(s_overloads, type) is { } profile)
        {
            overloading.Set(new(pilot, ship, module, profile.EffectId, OverloadScope.Module, selected, now));
        }
    }
}
