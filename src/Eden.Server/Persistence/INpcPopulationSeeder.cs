using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal interface INpcPopulationSeeder
{
    long NextUpdate(WorldDbContext world, int systemId, long now) => checked(now + TimeSpan.TicksPerSecond);

    void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, long now);
}
