using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Data.Common;
using Eden.Gameplay.Dogma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Eden.Server.Persistence;

internal readonly record struct FitCacheKey(long PilotId, long ShipId, long ItemId);

internal sealed record FitCacheEntry(string Revision, FrozenDictionary<DogmaAttributeId, double> Values);

internal sealed class ResidentFitCache : IDisposable
{
    private const int MaximumEntries = 8192;
    private static readonly ConcurrentDictionary<string, WeakReference<ResidentFitCache>> s_worlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FitCacheKey, FitCacheEntry> _entries = [];
    private readonly Queue<FitCacheKey> _order = [];
    private readonly string _path;
    private readonly object _gate = new();

    internal ResidentFitCache(WorldDatabase database)
    {
        _path = Path.GetFullPath(database.Path);
        s_worlds[_path] = new(this);
    }

    internal static ResidentFitCache? Find(string path) =>
        s_worlds.TryGetValue(Path.GetFullPath(path), out var weak) && weak.TryGetTarget(out var cache) ? cache : null;

    internal Dictionary<DogmaAttributeId, double>? Read(FitCacheKey key, string revision)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry) && entry.Revision == revision ? entry.Values.ToDictionary() : null;
        }
    }

    internal void Publish(IReadOnlyDictionary<FitCacheKey, FitCacheEntry> entries)
    {
        lock (_gate)
        {
            foreach (var pair in entries)
            {
                if (!_entries.ContainsKey(pair.Key))
                {
                    if (_entries.Count >= MaximumEntries)
                    {
                        _entries.Remove(_order.Dequeue());
                    }
                    _order.Enqueue(pair.Key);
                }
                _entries[pair.Key] = pair.Value;
            }
        }
    }

    public void Dispose()
    {
        if (Find(_path) == this)
        {
            s_worlds.TryRemove(_path, out _);
        }
    }
}

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
