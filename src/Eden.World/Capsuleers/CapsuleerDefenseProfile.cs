using Eden.Data.World.Fittings;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
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
        return new(
            CapsuleerDefenseRules.CombatShip((HullClass)hull.GroupId, tech, true)
                && pilot.ActiveShip.TypeId != CapsuleerCommunityStore.VentureType
                && modules.All(module => module.Effect != DogmaEffect.MiningLaser),
            modules
        );
    }
}
