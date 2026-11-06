using Eden.Gameplay.Mining;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable(InjectionType.Singleton)]
internal sealed class ResourceRenewalStore(WorldDatabase database, TimeProvider clock) : IResourceRenewal
{
    public int Renew(int limit = ResourceRenewalRules.BatchSize)
    {
        if (limit is < 1 or > ResourceRenewalRules.BatchSize)
        {
            throw new InvalidDataException("Invalid resource renewal batch size.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var policy = EconomicStore.Policy(world);
        if (!policy.RenewBelts)
        {
            return 0;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var due = world
            .Set<SavedBeltRenewal>()
            .Where(b => b.NextRenewal <= now)
            .OrderBy(b => b.NextRenewal)
            .ThenBy(b => b.BeltId)
            .Take(limit)
            .ToArray();
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var belt in due)
        {
            AsteroidFieldStore.Populate(world, catalog, belt.SystemId, now, belt.BeltId);
            // Catch up once to the next future boundary, without accumulating ore.
            belt.NextRenewal = ResourceRenewalRules.Next(now, policy.RenewalHourUtc);
        }
        world.SaveChanges();
        transaction.Commit();
        return due.Length;
    }
}
