using Eden.World.Catalog;

namespace Eden.World.Exploration;

internal static class ExplorationCatalogPreparation
{
    internal static void Prepare(CatalogDbContext catalog)
    {
        _ = CombatAnomalyCatalog.Read(catalog);
        _ = DedComplexCatalog.Read(catalog);
        _ = GhostSiteCatalog.Read(catalog);
        _ = HackingCatalog.Sites;
        _ = HackingCatalog.LootGroups(catalog);
        _ = SleeperSiteCatalog.Exploration(catalog);
        _ = WormholeCatalog.Read(catalog);
        _ = WormholeEnvironmentStore.Environments(catalog);
        _ = AgencyDirectory.IceSystems(catalog);
        IncursionStore.PrepareCatalog(catalog);
    }
}
