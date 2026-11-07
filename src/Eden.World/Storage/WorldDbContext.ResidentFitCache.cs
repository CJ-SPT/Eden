using System.Collections.Frozen;
using System.Data.Common;
using Eden.Gameplay.Dogma;
using Eden.World.Fittings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Eden.World.Storage;

public sealed partial class WorldDbContext
{
    private readonly Dictionary<FitCacheKey, FitCacheEntry> _pendingFits = [];
    private Guid _fitTransaction;
    private readonly ResidentFitCache? _residentFits = ResidentFitCache.Find(path);

    internal string? FitRevision(long pilotId)
    {
        if (_residentFits is null || Database.CurrentTransaction is null || ChangeTracker.HasChanges())
        {
            return null;
        }
        if (_fitTransaction != Database.CurrentTransaction.TransactionId)
        {
            _pendingFits.Clear();
            _fitTransaction = Database.CurrentTransaction.TransactionId;
        }
        return Database.SqlQuery<string>($"SELECT Stamp AS Value FROM WorldFitRevision WHERE PilotId={pilotId}").FirstOrDefault()
            ?? "initial";
    }

    internal Dictionary<DogmaAttributeId, double>? CachedFit(FitCacheKey key, string? revision)
    {
        if (revision is null)
        {
            return null;
        }
        if (_pendingFits.TryGetValue(key, out var pending) && pending.Revision == revision)
        {
            return pending.Values.ToDictionary();
        }
        return _residentFits?.Read(key, revision);
    }

    internal void StageFit(FitCacheKey key, string? revision, IReadOnlyDictionary<DogmaAttributeId, double> values)
    {
        if (revision is not null)
        {
            _pendingFits[key] = new(revision, values.ToFrozenDictionary());
        }
    }

    private sealed class FitTransactions(WorldDbContext world) : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            if (eventData.TransactionId == world._fitTransaction)
            {
                world._residentFits?.Publish(world._pendingFits);
            }
            world._pendingFits.Clear();
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
            world._pendingFits.Clear();
    }
}
