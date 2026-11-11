using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal static partial class DroneCatalog
{
    internal static DroneProfile Skilled(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int typeId,
        long? controllerShipId = null
    )
    {
        var attributes = catalog.FittingAttributes(typeId);
        var required = InventoryDefinitions.RequiredSkills.Select(pair => (int)attributes.GetValueOrDefault(pair.Skill)).ToHashSet();
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
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

    internal static IReadOnlyDictionary<DroneSkill, int> Skills(WorldAction world, CatalogDbContext catalog, long pilotId) =>
        PilotSkillReader
            .Levels(world, catalog, pilotId, Enum.GetValues<DroneSkill>().Select(s => (int)s).ToArray())
            .ToDictionary(pair => (DroneSkill)pair.Key, pair => pair.Value);
}
