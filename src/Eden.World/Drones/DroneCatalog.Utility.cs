using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.World.Drones;

internal static partial class DroneCatalog
{
    private static DroneProfile ReadUtility(CatalogDbContext catalog, int typeId, IReadOnlyDictionary<DogmaAttributeId, double>? fitted)
    {
        var type = catalog.FittingType(typeId)!;
        var source = catalog.Read(CatalogData.Client.Types, (long)typeId)!;
        var attributes = fitted ?? catalog.FittingAttributes(typeId);
        var effects = FittingCatalog.Effects(catalog, typeId);
        bool hybrid = type.GroupId == UtilityDroneRules.HybridGroup;
        var utilityEffects = effects.Where(e => e != DogmaEffect.LaserFired).ToArray();
        if (
            source.Published != 1
            || source.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || utilityEffects.Length != 1
            || effects.Count != (hybrid ? 2 : 1)
            || FittingCatalog.DefaultEffect(catalog, typeId) != (hybrid ? DogmaEffect.LaserFired : utilityEffects[0])
        )
        {
            throw new InvalidDataException("Only verified static utility and mining drones can launch.");
        }
        var utility = UtilityDroneRules.Define(utilityEffects[0], attributes);
        bool workerMotion = utility.Mining || utility.Kind == UtilityDroneKind.Salvaging;
        bool matches = type.GroupId switch
        {
            UtilityDroneRules.MiningGroup => utility.Mining,
            UtilityDroneRules.RepairGroup => utility.Repair,
            UtilityDroneRules.EnergyGroup => utility.Kind == UtilityDroneKind.Neutralizing,
            UtilityDroneRules.WebGroup => utility.Kind == UtilityDroneKind.Webifying,
            UtilityDroneRules.SalvageGroup => utility.Kind == UtilityDroneKind.Salvaging,
            _ => utility.Offensive,
        };
        if (!matches || (workerMotion ? type.Mass is not >= 0 : type.Mass is not > 0) || type.Radius is not > 0 || source.Volume is not > 0)
        {
            throw new InvalidDataException("Invalid utility drone catalog type.");
        }
        double Positive(DogmaAttributeId id)
        {
            double value = attributes.GetValueOrDefault(id);
            return double.IsFinite(value) && value > 0 ? value : throw new InvalidDataException("Invalid drone catalog attribute.");
        }
        var required = InventoryDefinitions.RequiredSkills.Select(pair => (int)attributes.GetValueOrDefault(pair.Skill)).ToHashSet();
        var damage = new DamageVector(
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        ).Scale(hybrid ? Positive(DogmaAttributeId.DamageMultiplier) : 0);
        return new(
            typeId,
            source.Volume.Value,
            Positive(DogmaAttributeId.DroneBandwidthUsed),
            workerMotion ? Math.Max(type.Mass.Value, UtilityDroneRules.MinimumWorkerMass) : type.Mass.Value,
            (float)type.Radius.Value,
            workerMotion
                ? Math.Max((float)attributes.GetValueOrDefault(DogmaAttributeId.Agility), UtilityDroneRules.MinimumWorkerAgility)
                : (float)Positive(DogmaAttributeId.Agility),
            (float)Positive(DogmaAttributeId.MaxVelocity),
            workerMotion ? (float)Positive(DogmaAttributeId.MaxVelocity) : (float)Positive(DogmaAttributeId.EntityCruiseSpeed),
            Positive(workerMotion ? DogmaAttributeId.DroneOrbitRange : DogmaAttributeId.EntityFlyRange),
            hybrid ? Positive(DogmaAttributeId.MaxRange) : utility.Range,
            hybrid ? Positive(DogmaAttributeId.AccuracyFalloff) : 0,
            hybrid ? Positive(DogmaAttributeId.TrackingSpeed) : 0,
            hybrid ? Positive(DogmaAttributeId.WeaponSignatureResolution) : 0,
            hybrid ? Positive(DogmaAttributeId.WeaponCycleTime) : utility.DurationMilliseconds,
            damage,
            ShipHealth.DefenseFrom(attributes),
            hybrid ? DroneRules.Kind(attributes.GetValueOrDefault(DogmaAttributeId.DroneBandwidthUsed), required) : CombatDroneKind.Utility,
            utility
        );
    }
}
