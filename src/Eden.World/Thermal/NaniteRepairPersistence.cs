using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

// Preparation for the paid repair command. Quoting never consumes paste,
// changes damage, starts a timer, or admits a native repair RPC.
internal static class NaniteRepairPersistence
{
    internal static NaniteRepairQuote Quote(WorldDbContext world, CatalogDbContext catalog, long pilotId, long moduleId)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).SingleOrDefault(p => p.Id == pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot is null
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || ShipHeatPersistence.Rack(item.FlagId) is null
        )
        {
            throw new InvalidDataException("Quote nanite repairs only for a module fitted to your active ship.");
        }
        var condition = ModuleConditionPersistence.Read(world, catalog, item);
        var price = NaniteRepairCatalog.Price(catalog, item.TypeId);
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        return NaniteRepairRules.Quote(
            condition,
            price.ComputedBasePrice,
            price.PasteBasePrice,
            new(
                calculation.Graph.Read(pilotId, DogmaAttributeId.ModuleRepairRate),
                calculation.Graph.Read(pilotId, DogmaAttributeId.ModuleRepairCostMultiplier)
            )
        );
    }
}
