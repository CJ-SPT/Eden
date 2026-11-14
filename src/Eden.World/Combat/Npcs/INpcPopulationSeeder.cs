using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat.Npcs;

internal interface INpcPopulationSeeder
{
    long NextUpdate(WorldAction world, int systemId, long now) => checked(now + TimeSpan.TicksPerSecond);

    void Seed(WorldAction world, CatalogDbContext catalog, int systemId, long now);
}
