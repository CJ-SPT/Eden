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
    private readonly Dictionary<(long Pilot, long Ship), FittingSnapshotEntry> _pendingSnapshots = [];
    private Guid _fitTransaction;
    private readonly ResidentFitCache? _residentFits = string.IsNullOrEmpty(path) ? null : ResidentFitCache.Find(path);

    internal string? FitRevision(long pilotId)
    {
        if (_residentFits is null || Database.CurrentTransaction is null || ChangeTracker.HasChanges())
        {
            return null;
        }
        if (_fitTransaction != Database.CurrentTransaction.TransactionId)
        {
            _pendingFits.Clear();
            _pendingSnapshots.Clear();
            _fitTransaction = Database.CurrentTransaction.TransactionId;
        }
        if (
            Eden.World.Runtime.LiveWorld.Find(WorldPath)?.Economy is { IsLoaded: true } economy
            && !WorldConnections.HasChanges(this, "WorldFitRevision")
        )
        {
            return economy.FitRevision(pilotId);
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

    internal FittingSnapshot? CachedFittingSnapshot(long pilot, long ship, string? revision)
    {
        if (revision is null)
        {
            return null;
        }
        return _pendingSnapshots.TryGetValue((pilot, ship), out var pending) && pending.Revision == revision
            ? pending.Snapshot
            : _residentFits?.ReadSnapshot(pilot, ship, revision);
    }

    internal void StageFittingSnapshot(long pilot, long ship, string? revision, FittingSnapshot snapshot)
    {
        if (revision is not null)
        {
            _pendingSnapshots[(pilot, ship)] = new(revision, snapshot);
        }
    }

    private sealed class FitTransactions(WorldDbContext world) : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        {
            if (eventData.TransactionId == world._fitTransaction)
            {
                world._residentFits?.Publish(world._pendingFits);
                world._residentFits?.PublishSnapshots(world._pendingSnapshots);
            }
            world._pendingFits.Clear();
            world._pendingSnapshots.Clear();
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        {
            world._pendingFits.Clear();
            world._pendingSnapshots.Clear();
        }
    }
}
