using Eden.Data.Catalog;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;

namespace Eden.World.Structures;

internal static class StructureCompressionCatalog
{
    private static readonly CatalogDefinition<InventoryTypeList> s_allowed = new(
        "structure-compressible-types",
        (catalog, _) => CatalogTypeLists.Read(catalog, StructureCompressionRules.StructureTypeList)
    );

    internal static int Compressed(CatalogDbContext catalog, int sourceTypeId)
    {
        var source = catalog.RequireItem(sourceTypeId);
        var allowed = catalog.Definition(s_allowed, 0);
        if (
            !allowed.Contains(source.Id, source.GroupId, source.CategoryId)
            || !catalog.Definitions.Pin(CatalogData.Client.Compressibletypes).TryGetValue(sourceTypeId, out long output)
            || output is <= 0 or > int.MaxValue
            || catalog.FittingType((int)output) is null
        )
        {
            throw new InvalidDataException("This resource cannot be compressed in a structure.");
        }
        return (int)output;
    }

    internal static int DecompressedGas(CatalogDbContext catalog, int sourceTypeId)
    {
        var compressed = catalog.RequireItem(sourceTypeId);
        var sources = catalog
            .Definitions.Pin(CatalogData.Client.Compressibletypes)
            .Where(pair => pair.Value == sourceTypeId)
            .Select(pair => pair.Key)
            .ToArray();
        if (
            compressed.CategoryId != MiningDefinitions.GasCloudCategory
            || compressed.GroupId != (int)HoldCargoGroup.CompressedGas
            || sources is not [long source]
            || source is <= 0 or > int.MaxValue
            || catalog.FittingType((int)source)
                is not { CategoryId: MiningDefinitions.GasCloudCategory, GroupId: MiningDefinitions.GasCloudGroup }
        )
        {
            throw new InvalidDataException("Choose a uniquely mapped compressed harvestable gas to decompress.");
        }
        return (int)source;
    }
}
