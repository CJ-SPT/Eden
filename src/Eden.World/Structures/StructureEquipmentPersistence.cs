using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal sealed record StructureFittingCacheEntry(
    IReadOnlyDictionary<DogmaAttributeId, double> Hull,
    IReadOnlyList<PassiveModifier> HullModifiers,
    double Security,
    IReadOnlyList<StructureFittedEquipment> Equipment,
    IReadOnlyList<StructureLoadedCharge> Charges,
    StructureFittingState State
);

internal static class StructureEquipmentPersistence
{
    internal static IReadOnlyList<StructureFittedEquipment> Equipment(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure
    ) =>
        world
            .Filter<OwnedItem>(item => item.LocationId == structure.Id && item.OwnerId == structure.CorporationId)
            .AsEnumerable()
            .Where(item =>
                InventoryDefinitions.IsFittedSlot(item.FlagId)
                || item.FlagId >= StructureRules.FirstServiceSlot
                    && item.FlagId < StructureRules.FirstServiceSlot + StructureRules.MaximumServiceSlots
            )
            .OrderBy(item => item.Id)
            .Select(item => new StructureFittedEquipment(
                item.Id,
                item.FlagId,
                item.Online,
                StructureEquipmentCatalog.Require(catalog, item.TypeId)
            ))
            .ToArray();

    internal static StructureFittingState Read(WorldAction world, CatalogDbContext catalog, SavedStructure structure)
    {
        var hull = catalog.FittingAttributes(structure.TypeId);
        var hullModifiers = StructureEquipmentCatalog.HullModifiers(catalog, structure.TypeId);
        var equipment = Equipment(world, catalog, structure);
        var charges = Charges(world, catalog, structure, equipment);
        double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(structure.SystemId);
        var live = LiveWorld.Find(world.WorldPath);
        if (
            live?.StructureFittings.GetValueOrDefault(structure.Id) is { } cached
            && ReferenceEquals(cached.Hull, hull)
            && ReferenceEquals(cached.HullModifiers, hullModifiers)
            && cached.Security == security
            && cached.Equipment.SequenceEqual(equipment)
            && cached.Charges.SequenceEqual(charges)
        )
        {
            return cached.State;
        }
        SessionPerformance.FittingCalculated();
        var state = StructureFittingRules.Calculate(
            structure.Id,
            hull,
            equipment,
            hullModifiers,
            StructureEquipmentCatalog.Definitions(catalog, hull, equipment, hullModifiers, charges),
            security,
            charges
        );
        if (live is not null)
        {
            // Every key contains immutable inputs, so a private rolled-back fitting cannot be reused for the committed fit.
            live.StructureFittings[structure.Id] = new(hull, hullModifiers, security, equipment, charges, state);
        }
        return state;
    }

    private static IReadOnlyList<StructureLoadedCharge> Charges(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure,
        IReadOnlyList<StructureFittedEquipment> equipment
    )
    {
        List<StructureLoadedCharge> result = [];
        foreach (var module in equipment)
        {
            var item = world.Filter<OwnedItem>(item => item.LocationId == module.ItemId).SingleOrDefault();
            if (item is null)
            {
                continue;
            }
            if (item.OwnerId != structure.CorporationId || item.Quantity <= 0)
            {
                throw new InvalidDataException("The structure magazine has invalid ownership or quantity.");
            }
            result.Add(new(item.Id, module.ItemId, StructureChargeCatalog.Require(catalog, item.TypeId)));
        }
        return result;
    }

    internal static void Check(WorldAction world, CatalogDbContext catalog, SavedStructure structure) =>
        StructureFittingRules.Check(
            StructureCatalog.Require(catalog, structure.TypeId),
            Read(world, catalog, structure),
            Equipment(world, catalog, structure)
        );
}
