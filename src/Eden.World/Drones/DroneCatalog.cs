using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal static partial class DroneCatalog
{
    private const int AbyssalMetaGroup = 15;
    private static readonly CatalogDefinition<DroneProfile> s_profile = new(
        "combat-drone",
        (catalog, typeId) => ReadProfile(catalog, typeId, null),
        512
    );

    internal static DroneProfile Profile(
        CatalogDbContext catalog,
        int typeId,
        IReadOnlyDictionary<DogmaAttributeId, double>? fittedAttributes = null
    ) => fittedAttributes is null ? catalog.Definition(s_profile, typeId) : ReadProfile(catalog, typeId, fittedAttributes);

    private static DroneProfile ReadProfile(
        CatalogDbContext catalog,
        int typeId,
        IReadOnlyDictionary<DogmaAttributeId, double>? fittedAttributes
    )
    {
        var type = catalog.FittingType(typeId) ?? throw new InvalidDataException("Unknown drone type.");
        var source = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var attributes = fittedAttributes ?? catalog.FittingAttributes(typeId);
        double bandwidth = attributes.GetValueOrDefault(DogmaAttributeId.DroneBandwidthUsed);
        var required = InventoryDefinitions.RequiredSkills.Select(pair => (int)attributes.GetValueOrDefault(pair.Skill)).ToHashSet();
        if (
            type.GroupId != DroneRules.CombatDroneGroup
            || source?.Published != 1
            || source.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || !FittingCatalog.Effects(catalog, typeId).SetEquals([DogmaEffect.LaserFired])
            || FittingCatalog.DefaultEffect(catalog, typeId) != DogmaEffect.LaserFired
        )
        {
            throw new InvalidDataException("Only supported static combat drones can launch.");
        }
        var kind = DroneRules.Kind(bandwidth, required);
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
            kind == CombatDroneKind.Sentry ? 0 : (float)Positive(DogmaAttributeId.EntityCruiseSpeed),
            Positive(DogmaAttributeId.EntityFlyRange),
            Positive(DogmaAttributeId.MaxRange),
            Positive(DogmaAttributeId.AccuracyFalloff),
            Positive(DogmaAttributeId.TrackingSpeed),
            Positive(DogmaAttributeId.WeaponSignatureResolution),
            Positive(DogmaAttributeId.WeaponCycleTime),
            damage,
            ShipHealth.DefenseFrom(attributes),
            kind
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
        if (shipId == 0)
        {
            return DroneRules.ApplySkills(Profile(catalog, typeId), Skills(world, catalog, pilotId), required);
        }
        var key = new FitCacheKey(pilotId, shipId, -typeId);
        string? revision = world.FitRevision(pilotId);
        var fitted = world.CachedFit(key, revision);
        if (fitted is null)
        {
            var ship =
                shipId == pilot.ActiveShipId ? pilot.ActiveShip : world.Ships.SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
            fitted = ship is null
                ? attributes.ToDictionary()
                : FittingCalculation.Read(world, catalog, pilot, ship, typeId).Graph.Attributes(FittingCalculation.OwnerTargetId);
            world.StageFit(key, revision, fitted);
        }
        return DroneRules.ApplySkills(Profile(catalog, typeId, fitted), Skills(world, catalog, pilotId), required);
    }

    internal static int ControlLimit(FittingCalculation fitting) =>
        checked((int)fitting.Graph.Read(fitting.PilotId, DogmaAttributeId.MaxActiveDrones));
}
