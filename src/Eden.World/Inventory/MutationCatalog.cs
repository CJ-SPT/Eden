using System.Collections.Immutable;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.World.Inventory;

internal static class MutationCatalog
{
    private const int AbyssalMetaGroup = 15;
    private static readonly CatalogDefinition<int> s_base = new("mutation-base-type", LoadBase);
    private static readonly CatalogDefinition<ImmutableDictionary<DogmaAttributeId, double>> s_attributes = new(
        "mutation-type-attributes",
        LoadAttributes
    );

    internal static int BaseType(CatalogDbContext catalog, int type) =>
        catalog.FittingType(type)?.CategoryId == (int)InventoryCategory.Module
        && catalog.Read(CatalogData.Client.Types, (long)type)?.MetaGroupID == AbyssalMetaGroup
            ? catalog.Definition(s_base, type)
            : type;

    private static int LoadBase(CatalogDbContext catalog, int type)
    {
        var effects = catalog.Read(CatalogData.Client.Typedogma, (long)type)?.DogmaEffects;
        var expected = (effects ?? []).Select(e => e.EffectID).ToHashSet();
        return catalog
            .Records(CatalogData.Client.Dynamicitemattributes)
            .Values.SelectMany(v => v.InputOutputMapping ?? [])
            .Where(m => m.ResultingType == type)
            .SelectMany(m => m.ApplicableTypes ?? [])
            .Distinct()
            .Order()
            .Where(id =>
                catalog.FittingType(id)?.GroupId == catalog.RequireItem(type).GroupId
                && catalog.Read(CatalogData.Client.Types, id)?.MetaGroupID != AbyssalMetaGroup
                && expected.SetEquals((catalog.Read(CatalogData.Client.Typedogma, id)?.DogmaEffects ?? []).Select(e => e.EffectID))
            )
            .Select(id => checked((int)id))
            .FirstOrDefault(type);
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> Attributes(CatalogDbContext catalog, int type) =>
        catalog.Definition(s_attributes, type);

    private static ImmutableDictionary<DogmaAttributeId, double> LoadAttributes(CatalogDbContext catalog, int type)
    {
        var result = catalog.RawFittingAttributes(BaseType(catalog, type)).ToImmutableDictionary().ToBuilder();
        foreach (var pair in catalog.RawFittingAttributes(type))
        {
            result[pair.Key] = pair.Value;
        }
        return result.ToImmutable();
    }
}
