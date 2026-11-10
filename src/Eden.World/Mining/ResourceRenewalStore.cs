using Eden.Data.World.Economy;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Runtime;
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
        var live = LiveWorld.Find(database.Path);
        using var owner = live?.Enter();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (live?.Reads is { } reads && reads.TryResourceDeadline(out long deadline) && deadline > now)
        {
            return 0;
        }
        using WorldDbContext world = new(database.Path) { Participants = TransactionParticipants.None };
        using var transaction = world.Database.BeginTransaction();
        var policy = EconomicStore.Policy(world);
        if (!policy.RenewBelts)
        {
            live?.Reads?.ScheduleResources(long.MaxValue);
            return 0;
        }
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
        long? nextDeadline = due.Length == 0 ? world.Set<SavedBeltRenewal>().Min(b => (long?)b.NextRenewal) ?? long.MaxValue : null;
        transaction.Commit();
        if (nextDeadline is long scheduled)
        {
            live?.Reads?.ScheduleResources(scheduled);
        }
        return due.Length;
    }
}
