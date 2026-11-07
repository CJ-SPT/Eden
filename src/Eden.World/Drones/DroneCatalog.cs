using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Drones;

internal static class DroneCatalog
{
    private const int AbyssalMetaGroup = 15;

    internal static DroneProfile Profile(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.FittingType(typeId) ?? throw new InvalidDataException("Unknown drone type.");
        var source = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = catalog.FittingAttributes(typeId);
        double bandwidth = attributes.GetValueOrDefault(DogmaAttributeId.DroneBandwidthUsed);
        if (
            type.GroupId != DroneRules.CombatDroneGroup
            || source?.Published != 1
            || source.MetaGroupID == AbyssalMetaGroup
            || bandwidth is not (DroneRules.LightBandwidth or DroneRules.MediumBandwidth)
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || !FittingCatalog.Effects(catalog, typeId).SetEquals([DogmaEffect.LaserFired])
            || FittingCatalog.DefaultEffect(catalog, typeId) != DogmaEffect.LaserFired
        )
        {
            throw new InvalidDataException("Only supported static light and medium combat drones can launch.");
        }
        double Positive(DogmaAttributeId id)
        {
            double value = attributes.GetValueOrDefault(id);
            return double.IsFinite(value) && value > 0 ? value : throw new InvalidDataException("Invalid drone catalog profile.");
        }
        var damage = new DamageVector(
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        ).Scale(Positive(DogmaAttributeId.DamageMultiplier));
        if (
            !double.IsFinite(damage.Total)
            || damage.Total <= 0
            || type.Mass is not > 0
            || type.Radius is not > 0
            || source.Volume is not > 0
        )
        {
            throw new InvalidDataException("Invalid drone damage, mass, radius or volume.");
        }
        return new(
            typeId,
            source.Volume.Value,
            bandwidth,
            type.Mass.Value,
            (float)type.Radius.Value,
            (float)Positive(DogmaAttributeId.Agility),
            (float)Positive(DogmaAttributeId.MaxVelocity),
            (float)Positive(DogmaAttributeId.EntityCruiseSpeed),
            Positive(DogmaAttributeId.EntityFlyRange),
            Positive(DogmaAttributeId.MaxRange),
            Positive(DogmaAttributeId.AccuracyFalloff),
            Positive(DogmaAttributeId.TrackingSpeed),
            Positive(DogmaAttributeId.WeaponSignatureResolution),
            Positive(DogmaAttributeId.WeaponCycleTime),
            damage,
            ShipHealth.DefenseFrom(attributes)
        );
    }

    internal static IReadOnlyDictionary<DroneSkill, int> Skills(WorldDbContext world, CatalogDbContext catalog, long pilotId) =>
        PilotSkillReader
            .Levels(world, catalog, pilotId, Enum.GetValues<DroneSkill>().Select(s => (int)s).ToArray())
            .ToDictionary(pair => (DroneSkill)pair.Key, pair => pair.Value);

    internal static DroneProfile Skilled(WorldDbContext world, CatalogDbContext catalog, long pilotId, int typeId)
    {
        var attributes = catalog.FittingAttributes(typeId);
        var required = InventoryDefinitions.RequiredSkills.Select(pair => (int)attributes.GetValueOrDefault(pair.Skill)).ToHashSet();
        return DroneRules.ApplySkills(Profile(catalog, typeId), Skills(world, catalog, pilotId), required);
    }
}
