using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class FittingCatalog
{
    internal static ModuleFittingDefinition? Definition(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.Set<ItemType>().Find(typeId);
        if (type is null)
        {
            return null;
        }
        if (HardenerDefinitions.Effect(type.GroupId) is not null && !HardenerCatalog.Supported(catalog, typeId, type.GroupId))
        {
            return null;
        }
        if (
            type.GroupId == PropulsionDefinitions.AfterburnerGroup
            && Effects(catalog, typeId).Contains(DogmaEffect.Microwarpdrive)
            && !MicrowarpdriveCatalog.Supported(catalog, typeId, type.GroupId)
        )
        {
            return null;
        }
        if (
            type.GroupId == CapacitorBoosterDefinitions.Group
            && catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>()
                is not { Published: 1, MetaGroupID: 1 }
        )
        {
            return null;
        }
        var attributes = catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == typeId)
            .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
        return ModuleFittingRules.Define(
            typeId,
            type.GroupId,
            attributes,
            Effects(catalog, typeId),
            PassiveCatalog.Supported(catalog, typeId, type.GroupId)
        );
    }

    internal static HashSet<DogmaEffect> Effects(CatalogDbContext catalog, int typeId) =>
        catalog
            .Set<SourceRecord>()
            .Find("client/typedogma", typeId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientTypedogma>()
            .DogmaEffects?.Select(e => (DogmaEffect)checked((int)e.EffectID!.Value))
            .ToHashSet()
        ?? [];
}
