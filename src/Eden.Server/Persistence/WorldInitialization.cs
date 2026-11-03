using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public static class WorldInitialization
{
    public static void Initialize(WorldDatabase database, TimeProvider? clock = null)
    {
        if (!File.Exists(database.CatalogPath))
        {
            throw new FileNotFoundException(
                "Build the local game catalog with tools/catalog/build.py before starting Eden.",
                database.CatalogPath
            );
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        WorldMigration.Open(world, catalog, clock ?? TimeProvider.System);
        // Live hardening belongs to a connection. A restart retains paid
        // capacitor/cooldowns but never leaves an orphaned resistance bonus.
        world.Set<ActiveHardener>().ExecuteDelete();
    }

    // Explicit development/test open: validate and migrate before creating a reader.
    public static SqliteWorld Open(string path) => Open(new WorldDatabase(path));

    public static SqliteWorld Open(WorldDatabase database)
    {
        Initialize(database);
        return new(database);
    }
}
