using Eden.Data.World.Economy;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

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
            .AsNoTracking()
            .Where(b => b.NextRenewal <= now)
            .OrderBy(b => b.NextRenewal)
            .ThenBy(b => b.BeltId)
            .Take(limit)
            .ToArray();
        using CatalogDbContext catalog = new(database.CatalogPath);
        AsteroidFieldStore.Renew(world, catalog, due);
        if (due.Length > 0)
        {
            int[] belts = due.Select(belt => belt.BeltId).ToArray();
            // Multiple missed renewals replenish once, then retain the next future boundary.
            long next = ResourceRenewalRules.Next(now, policy.RenewalHourUtc);
            world
                .Set<SavedBeltRenewal>()
                .Where(belt => belts.Contains(belt.BeltId))
                .ExecuteUpdate(setters => setters.SetProperty(belt => belt.NextRenewal, next));
        }
        transaction.Commit();
        return due.Length;
    }
}
