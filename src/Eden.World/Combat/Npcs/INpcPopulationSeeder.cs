using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Combat.Npcs;

internal interface INpcPopulationSeeder
{
    long NextUpdate(WorldDbContext world, int systemId, long now) => checked(now + TimeSpan.TicksPerSecond);

    void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, long now);
}
