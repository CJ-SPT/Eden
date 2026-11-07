using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal static class DroneCatalog
{
    private const int AbyssalMetaGroup = 15;

    internal static DroneProfile Profile(
        CatalogDbContext catalog,
        int typeId,
        IReadOnlyDictionary<DogmaAttributeId, double>? fittedAttributes = null
    )
    {
        var type = catalog.FittingType(typeId) ?? throw new InvalidDataException("Unknown drone type.");
        var source = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = fittedAttributes ?? catalog.FittingAttributes(typeId);
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

    internal static DroneProfile Skilled(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        int typeId,
        long? controllerShipId = null
    )
    {
        var attributes = catalog.FittingAttributes(typeId);
        var required = InventoryDefinitions.RequiredSkills.Select(pair => (int)attributes.GetValueOrDefault(pair.Skill)).ToHashSet();
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        long shipId = controllerShipId ?? pilot.ActiveShipId;
        var key = new FitCacheKey(pilotId, shipId, -typeId);
        string? revision = world.FitRevision(pilotId);
        var fitted = world.CachedFit(key, revision);
        if (fitted is null)
        {
            fitted =
                shipId == pilot.ActiveShipId && HasRigs(world, pilot)
                    ? FittingCalculation.Read(world, catalog, pilot, ownerTypeId: typeId).Graph.Attributes(FittingCalculation.OwnerTargetId)
                    : attributes.ToDictionary();
            world.StageFit(key, revision, fitted);
        }
        return DroneRules.ApplySkills(Profile(catalog, typeId, fitted), Skills(world, catalog, pilotId), required);
    }

    internal static double ControlRange(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        double bonus = HasRigs(world, pilot)
            ? FittingCalculation.Read(world, catalog, pilot).Graph.Read(pilotId, DogmaAttributeId.DroneControlDistance)
            : 0;
        return DroneRules.ControlRange(Skills(world, catalog, pilotId)) + bonus;
    }

    private static bool HasRigs(WorldDbContext world, Pilot pilot) =>
        world
            .Set<OwnedItem>()
            .Any(i =>
                i.OwnerId == pilot.Id
                && i.LocationId == pilot.ActiveShipId
                && i.FlagId >= (int)InventoryFlag.RigSlot0
                && i.FlagId <= (int)InventoryFlag.RigSlot2
            );
}
