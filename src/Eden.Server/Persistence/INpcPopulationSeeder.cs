using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal interface INpcPopulationSeeder
{
    void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, long now);
}
