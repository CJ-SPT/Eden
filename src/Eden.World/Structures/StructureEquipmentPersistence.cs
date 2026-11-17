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
        double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(structure.SystemId);
        var live = LiveWorld.Find(world.WorldPath);
        if (
            live?.StructureFittings.GetValueOrDefault(structure.Id) is { } cached
            && ReferenceEquals(cached.Hull, hull)
            && ReferenceEquals(cached.HullModifiers, hullModifiers)
            && cached.Security == security
            && cached.Equipment.SequenceEqual(equipment)
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
            StructureEquipmentCatalog.Definitions(catalog, hull, equipment, hullModifiers),
            security
        );
        if (live is not null)
        {
            // Every key contains immutable inputs, so a private rolled-back fitting cannot be reused for the committed fit.
            live.StructureFittings[structure.Id] = new(hull, hullModifiers, security, equipment, state);
        }
        return state;
    }

    internal static void Check(WorldAction world, CatalogDbContext catalog, SavedStructure structure) =>
        StructureFittingRules.Check(
            StructureCatalog.Require(catalog, structure.TypeId),
            Read(world, catalog, structure),
            Equipment(world, catalog, structure)
        );
}
