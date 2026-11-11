using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.World.Repair;

internal sealed record AncillaryFuelUse(AncillaryRepairPayment Payment, Magazine? Magazine);

internal static partial class AncillaryFuelPersistence
{
    internal static AncillaryFuelUse Consume(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        OwnedItem module,
        AncillaryRepairer repairer,
        DogmaAttributeId chargeGroupAttribute = DogmaAttributeId.ChargeGroup2
    )
    {
        var loaded = PilotAmmunitionStore.Contents(world, module.Id);
        if (loaded is null)
        {
            return new(AncillaryRepairRules.Payment(repairer, 0), null);
        }
        var type = catalog.FittingType(loaded.TypeId);
        double volume = type is null ? 0 : PilotInventoryStore.Volume(catalog, loaded.TypeId);
        if (
            type is null
            || loaded.OwnerId != pilotId
            || loaded.Singleton
            || loaded.Quantity <= 0
            || !AmmunitionCatalog.Supported(catalog, loaded.TypeId, type.GroupId)
            || !AncillaryRepairRules.Compatible(
                repairer.Kind,
                type.GroupId,
                catalog.FittingAttributes(module.TypeId),
                catalog.FittingAttributes(loaded.TypeId),
                chargeGroupAttribute
            )
            || !double.IsFinite(volume)
            || volume <= 0
            || loaded.Quantity * volume > (catalog.FittingType(module.TypeId)!.Capacity ?? 0) + InventoryDefinitions.ResourceTolerance
        )
        {
            throw new InvalidDataException("Load valid owned ancillary repair fuel within the module's magazine capacity.");
        }
        var payment = AncillaryRepairRules.Payment(repairer, loaded.Quantity);
        loaded.Quantity -= payment.ChargesUsed;
        var magazine = PilotAmmunitionStore.Read(world, module, loaded) with { ReadyAt = 0 };
        if (loaded.Quantity == 0)
        {
            world.Remove(loaded);
        }
        return new(payment, magazine);
    }
}
