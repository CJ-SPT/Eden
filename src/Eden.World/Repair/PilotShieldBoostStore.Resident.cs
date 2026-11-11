using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

internal sealed partial class PilotShieldBoostStore
{
    internal static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module
    ) => PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, module);
}
