using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Structures;
using Eden.World.Agents;
using Eden.World.Catalog;

namespace Eden.World.Mining;

internal static class AutoMiningOreCatalog
{
    private static readonly CatalogDefinition<ImmutableArray<AutoMiningOre>> s_oreTypes = new("auto-mining-ore-types", Load);

    internal static ImmutableArray<AutoMiningOre> Read(CatalogProvider catalog) => catalog.Definition(s_oreTypes, 0);

    private static ImmutableArray<AutoMiningOre> Load(CatalogDbContext catalog, int unused)
    {
        return catalog
            .PopulatedBeltOreTypes()
            .Concat(MoonMiningRules.OreTypes)
            .Concat(MissionCatalog.MiningObjectiveTypes(catalog))
            .Distinct()
            .Select(id => catalog.Definitions.Items.GetValueOrDefault(id))
            .OfType<CatalogItemDefinition>()
            .Where(type => MiningDefinitions.MatchesFamily(MiningFamily.Ore, type.CategoryId, type.GroupId))
            .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(type => type.Id)
            .Select(type => new AutoMiningOre(type.Id, type.GroupId, type.Name))
            .ToImmutableArray();
    }
}
