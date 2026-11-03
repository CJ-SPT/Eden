using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class HullCatalog
{
    internal static bool CanAssemble(CatalogDbContext catalog, ItemType type, int category)
    {
        var definition = catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(type.Id))?.Read<ClientTypes>();
        // Existing Venture saves and the historical fixtures precede techLevel metadata.
        int techLevel = checked((int)(definition?.TechLevel ?? (type.Id == (int)StarterShipType.Venture ? 1 : 0)));
        return TechOneHullRules.CanAssemble(new(type.Id, (InventoryCategory)category, definition?.Published == 1, techLevel));
    }

    internal static bool HasBonuses(int typeId) =>
        typeId
            is (int)TechOneHullType.Merlin
                or (int)TechOneHullType.Punisher
                or (int)TechOneHullType.Incursus
                or (int)TechOneHullType.Rifter;

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        HasBonuses(typeId) ? PassiveCatalog.Modifiers(catalog, typeId) : [];

    internal static IReadOnlySet<int> Skills(CatalogDbContext catalog, int typeId)
    {
        if (!HasBonuses(typeId))
        {
            return new HashSet<int>();
        }
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        return InventoryDefinitions
            .RequiredSkills.Select(pair => attributes.GetValueOrDefault(pair.Skill))
            .Where(value => value > 0)
            .Select(value => checked((int)value))
            .ToHashSet();
    }
}
