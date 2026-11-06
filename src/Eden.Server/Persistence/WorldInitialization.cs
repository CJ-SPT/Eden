using Eden.Gameplay.Combat;
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
        // Live module bonuses belong to a connection. A restart retains paid
        // capacitor/cooldowns but never leaves an orphaned active bonus.
        world.Set<ActiveFittedModule>().ExecuteDelete();
        world.Set<WorldTackle>().ExecuteDelete();
        // NPC target ownership is connection activity, not offline combat.
        world.Set<WorldNpcEwar>().ExecuteDelete();
        world.Set<NpcAggression>().ExecuteDelete();
        // Downtime never replays active NPC repair cycles. Saved health and the
        // notification revision survive; damaged layers start a fresh interval.
        world
            .Set<WorldNpc>()
            .Where(n => n.ShieldRepairAt != 0 || n.ArmorRepairAt != 0)
            .ExecuteUpdate(set => set.SetProperty(n => n.ShieldRepairAt, 0).SetProperty(n => n.ArmorRepairAt, 0));
        // Downtime cancels live projectiles without refunding ammunition or
        // cooldowns. Saved impacts and rewards are never applied again.
        long now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc();
        world
            .Set<WorldMissile>()
            .Where(m => m.Status == MissileFlightStatus.Flying)
            .ExecuteUpdate(set => set.SetProperty(m => m.Status, MissileFlightStatus.Expired).SetProperty(m => m.EndedAt, now));
    }

    // Explicit development/test open: validate and migrate before creating a reader.
    public static SqliteWorld Open(string path) => Open(new WorldDatabase(path));

    public static SqliteWorld Open(WorldDatabase database)
    {
        Initialize(database);
        return new(database);
    }
}
