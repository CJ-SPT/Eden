using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Eden.World.Catalog;

public sealed record CatalogCacheOptions
{
    public const long DefaultMaximumEstimatedBytes = 64L * 1024 * 1024;
    public const int MaximumPageSize = 500;
    public long MaximumEstimatedBytes { get; init; } = DefaultMaximumEstimatedBytes;
}

public sealed record CatalogIdentity(string Path, int Build, string SourceFingerprint, long Generation);

public sealed record CatalogCacheStatistics(long Loads, long Hits, int Entries, long EstimatedBytes, long PinnedRecords, TimeSpan Startup);

/// <summary>One immutable catalog generation shared by all readers of a source file.</summary>
public sealed class CatalogProvider
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, WeakReference<CatalogProvider>> s_catalogs = new(StringComparer.OrdinalIgnoreCase);
    private static long s_generation;
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<string, ICatalogTable> _tables = new(StringComparer.Ordinal);
    private readonly CatalogCacheOptions _options;
    private readonly FileStamp _stamp;
    private readonly Lazy<FrozenDictionary<int, CatalogItemDefinition>> _items;
    private readonly Lazy<FrozenDictionary<int, ImmutableArray<int>>> _groups;
    private readonly Lazy<FrozenDictionary<int, ImmutableArray<int>>> _blueprints;
    private readonly CatalogMemo<int, ImmutableDictionary<DogmaAttributeId, double>> _attributes;
    private readonly CatalogMemo<int, ImmutableArray<DogmaEffect>> _effects;
    private readonly CatalogMemo<int, ImmutableDictionary<int, int>> _skills;
    private long _loads;
    private long _hits;
    private long _pinnedRecords;
    private int _invalidated;
    private readonly TimeSpan _startup;

    private readonly record struct FileStamp(long Length, long WrittenAt);

    private readonly record struct CacheKey(string Dataset, string Key);

    private sealed record CatalogValue<T>(bool Found, T Value, long EstimatedBytes);

    public CatalogIdentity Identity { get; }
    public IReadOnlyDictionary<int, CatalogItemDefinition> Items => _items.Value;

    public CatalogCacheStatistics Statistics
    {
        get
        {
            var statistics = _cache.GetCurrentStatistics();
            return new(
                Interlocked.Read(ref _loads),
                Interlocked.Read(ref _hits),
                _cache.Count,
                statistics?.CurrentEstimatedSize ?? 0,
                Interlocked.Read(ref _pinnedRecords),
                _startup
            );
        }
    }

    private CatalogProvider(string path, CatalogCacheOptions options, FileStamp stamp)
    {
        var timer = Stopwatch.StartNew();
        _options = options;
        _stamp = stamp;
        _cache = new(new MemoryCacheOptions { SizeLimit = options.MaximumEstimatedBytes, TrackStatistics = true });
        using CatalogDbContext catalog = new(path);
        catalog.Database.OpenConnection();
        CatalogSources.CheckBuild(catalog);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var dataset in catalog.Set<SourceDataset>().OrderBy(dataset => dataset.Name))
        {
            string identity = string.Join(
                '\0',
                dataset.Name,
                dataset.Source,
                dataset.Sha256,
                dataset.Rows.ToString(CultureInfo.InvariantCulture)
            );
            hash.AppendData(Encoding.UTF8.GetBytes(identity + "\n"));
        }
        Identity = new(
            path,
            CatalogSources.PinnedBuild,
            Convert.ToHexString(hash.GetHashAndReset()),
            Interlocked.Increment(ref s_generation)
        );
        _items = new(LoadItems);
        _groups = new(() =>
            Items
                .Values.GroupBy(item => item.GroupId)
                .ToFrozenDictionary(group => group.Key, group => group.Select(item => item.Id).Order().ToImmutableArray())
        );
        _blueprints = new(LoadBlueprints);
        _attributes = new(this, "projection/type-attributes");
        _effects = new(this, "projection/type-effects");
        _skills = new(this, "projection/type-skills");
        _startup = timer.Elapsed;
    }

    public static CatalogProvider Open(string path, CatalogCacheOptions? options = null)
    {
        string canonical = System.IO.Path.GetFullPath(path);
        if (options is { MaximumEstimatedBytes: <= 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        var captured = CatalogReadScope.Resolve(canonical, () => OpenUnscoped(canonical, options));
        if (options is not null && captured._options != options)
        {
            throw new InvalidOperationException("Shared catalog cache options must agree for the same catalog.");
        }
        return captured;
    }

    private static CatalogProvider OpenUnscoped(string path, CatalogCacheOptions? options)
    {
        lock (s_gate)
        {
            var file = new FileInfo(path);
            FileStamp stamp = new(file.Length, file.LastWriteTimeUtc.Ticks);
            if (s_catalogs.TryGetValue(path, out var reference) && reference.TryGetTarget(out var current))
            {
                if (current._stamp != stamp)
                {
                    throw new InvalidDataException("The installed catalog changed outside its owner. Restart the server.");
                }
                if (options is not null && current._options != options)
                {
                    throw new InvalidOperationException("Shared catalog cache options must agree for the same catalog.");
                }
                return current;
            }
            var created = new CatalogProvider(path, options ?? new(), stamp);
            s_catalogs[path] = new(created);
            foreach (string expired in s_catalogs.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray())
            {
                s_catalogs.Remove(expired);
            }
            return created;
        }
    }

    internal static void Invalidate(string path)
    {
        lock (s_gate)
        {
            if (s_catalogs.Remove(System.IO.Path.GetFullPath(path), out var reference) && reference.TryGetTarget(out var current))
            {
                Volatile.Write(ref current._invalidated, 1);
            }
        }
    }

    private void RequireCurrent()
    {
        if (Volatile.Read(ref _invalidated) != 0)
        {
            throw new InvalidOperationException("The catalog changed during this operation. Retry with a new catalog generation.");
        }
    }

    public TValue Get<TKey, TValue>(CatalogSource<TKey, TValue> source, TKey key)
        where TKey : notnull
    {
        if (!TryGet(source, key, out var value))
        {
            throw new KeyNotFoundException($"Missing {source.Name} record {key}.");
        }
        return value;
    }

    public bool TryGet<TKey, TValue>(CatalogSource<TKey, TValue> source, TKey key, out TValue value)
        where TKey : notnull
    {
        var entry = Table(source).Lookup(key);
        value = entry.Value;
        return entry.Found;
    }

    public CatalogPage<TKey, TValue> Page<TKey, TValue>(CatalogSource<TKey, TValue> source, string? after = null, int limit = 100)
        where TKey : notnull => Table(source).Page(after, limit);

    public IReadOnlyDictionary<TKey, TValue> Pin<TKey, TValue>(CatalogSource<TKey, TValue> source)
        where TKey : notnull => Table(source).Pinned;

    public ImmutableArray<int> TypesInGroup(int groupId) => _groups.Value.GetValueOrDefault(groupId, []);

    public ImmutableArray<int> BlueprintsForProduct(int productId) => _blueprints.Value.GetValueOrDefault(productId, []);

    public ImmutableDictionary<DogmaAttributeId, double> Attributes(int typeId) =>
        _attributes
            .Read(
                typeId,
                () =>
                {
                    RequireCurrent();
                    using CatalogDbContext catalog = new(Identity.Path);
                    var attributes = catalog
                        .Set<TypeAttribute>()
                        .Where(attribute => attribute.ItemTypeId == typeId)
                        .ToImmutableDictionary(attribute => (DogmaAttributeId)attribute.AttributeId, attribute => attribute.Value);
                    return new(true, attributes, EntryOverhead + attributes.Count * AttributeEstimatedBytes);
                }
            )
            .Value;

    public void PreloadCore()
    {
        _ = Items;
        _ = _groups.Value;
        _ = Pin(CatalogData.Client.Types);
        _ = _blueprints.Value;
    }

    public ImmutableArray<DogmaEffect> Effects(int typeId) =>
        _effects
            .Read(
                typeId,
                () =>
                {
                    var effects = TryGet(CatalogData.Client.Typedogma, (long)typeId, out var dogma)
                        ? (dogma.DogmaEffects ?? []).Select(effect => (DogmaEffect)checked((int)effect.EffectID!.Value)).ToImmutableArray()
                        : [];
                    return new(true, effects, EntryOverhead + effects.Length * AttributeEstimatedBytes);
                }
            )
            .Value;

    public ImmutableDictionary<int, int> RequiredSkills(int typeId) =>
        _skills
            .Read(
                typeId,
                () =>
                {
                    if (TryGet(CatalogData.Client.Requiredskillsfortypes, (long)typeId, out var source))
                    {
                        var requirements = source.ToImmutableDictionary(
                            pair => int.Parse(pair.Key, CultureInfo.InvariantCulture),
                            pair => checked((int)pair.Value)
                        );
                        return new(true, requirements, EntryOverhead + requirements.Count * AttributeEstimatedBytes);
                    }
                    var attributes = Attributes(typeId);
                    var fallback = InventoryDefinitions
                        .RequiredSkills.Where(pair => attributes.GetValueOrDefault(pair.Skill) != 0)
                        .ToImmutableDictionary(
                            pair => checked((int)attributes[pair.Skill]),
                            pair => checked((int)attributes.GetValueOrDefault(pair.Level))
                        );
                    return new(true, fallback, EntryOverhead + fallback.Count * AttributeEstimatedBytes);
                }
            )
            .Value;

    private const long EntryOverhead = 256;
    private const long AttributeEstimatedBytes = 64;
    private const long PayloadExpansionEstimate = 4;

    private FrozenDictionary<int, CatalogItemDefinition> LoadItems()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        Interlocked.Increment(ref _loads);
        var items = catalog
            .Set<ItemType>()
            .Include(type => type.Group)
            .AsEnumerable()
            .ToFrozenDictionary(
                type => type.Id,
                type => new CatalogItemDefinition(
                    type.Id,
                    type.GroupId,
                    type.Group.CategoryId,
                    type.Name,
                    type.Mass,
                    type.Radius,
                    type.Capacity
                )
            );
        Interlocked.Add(ref _pinnedRecords, items.Count);
        return items;
    }

    private FrozenDictionary<int, ImmutableArray<int>> LoadBlueprints() =>
        Pin(CatalogData.Client.Blueprints)
            .SelectMany(pair => (pair.Value.Activities?.Manufacturing?.Products ?? []).Select(product => (pair.Key, product.TypeID)))
            .Where(product => product.TypeID is > 0 and <= int.MaxValue)
            .GroupBy(product => checked((int)product.TypeID!.Value))
            .ToFrozenDictionary(
                group => group.Key,
                group => group.Select(product => int.Parse(product.Key, CultureInfo.InvariantCulture)).Distinct().Order().ToImmutableArray()
            );

    private CatalogTable<TKey, TValue> Table<TKey, TValue>(CatalogSource<TKey, TValue> source)
        where TKey : notnull
    {
        if (!CatalogSources.Owns(source))
        {
            throw new ArgumentException("Use a generated typed catalog source.", nameof(source));
        }
        return (CatalogTable<TKey, TValue>)_tables.GetOrAdd(source.Name, _ => new CatalogTable<TKey, TValue>(this, source));
    }

    private interface ICatalogTable;

    private sealed class CatalogMemo<TKey, TValue>(CatalogProvider owner, string dataset)
        where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, Lazy<CatalogValue<TValue>>> _loads = new();

        internal CatalogValue<TValue> Read(TKey key, Func<CatalogValue<TValue>> load)
        {
            CacheKey cacheKey = new(dataset, JsonSerializer.Serialize(key));
            if (owner._cache.TryGetValue(cacheKey, out CatalogValue<TValue>? cached))
            {
                Interlocked.Increment(ref owner._hits);
                return cached!;
            }
            var pending = _loads.GetOrAdd(
                key,
                _ => new Lazy<CatalogValue<TValue>>(() =>
                {
                    if (owner._cache.TryGetValue(cacheKey, out CatalogValue<TValue>? completed))
                    {
                        Interlocked.Increment(ref owner._hits);
                        return completed!;
                    }
                    owner.RequireCurrent();
                    Interlocked.Increment(ref owner._loads);
                    return load();
                })
            );
            try
            {
                var value = pending.Value;
                owner._cache.Set(cacheKey, value, new MemoryCacheEntryOptions { Size = value.EstimatedBytes });
                return value;
            }
            finally
            {
                _loads.TryRemove(new KeyValuePair<TKey, Lazy<CatalogValue<TValue>>>(key, pending));
            }
        }
    }

    private sealed class CatalogTable<TKey, TValue> : ICatalogTable
        where TKey : notnull
    {
        private readonly CatalogProvider _owner;
        private readonly CatalogSource<TKey, TValue> _source;
        private readonly CatalogMemo<TKey, TValue> _memo;
        private readonly Lazy<FrozenDictionary<TKey, TValue>> _pinned;

        internal CatalogTable(CatalogProvider owner, CatalogSource<TKey, TValue> source)
        {
            _owner = owner;
            _source = source;
            _memo = new(owner, source.Name);
            _pinned = new(PinAll);
        }

        internal IReadOnlyDictionary<TKey, TValue> Pinned => _pinned.Value;

        internal CatalogValue<TValue> Lookup(TKey key)
        {
            if (_pinned.IsValueCreated)
            {
                Interlocked.Increment(ref _owner._hits);
                return _pinned.Value.TryGetValue(key, out var pinned) ? new(true, pinned, 0) : new(false, default!, 0);
            }
            return _memo.Read(
                key,
                () =>
                {
                    using CatalogDbContext catalog = new(_owner.Identity.Path);
                    string encoded = JsonSerializer.Serialize(key);
                    var record = catalog.Set<SourceRecord>().Find(_source.Name, encoded);
                    return Decode(record);
                }
            );
        }

        private static CatalogValue<TValue> Decode(SourceRecord? record) =>
            record is null
                ? new(false, default!, EntryOverhead)
                : new(true, record.Read<TValue>(), EntryOverhead + record.Data.Length * PayloadExpansionEstimate);

        internal CatalogPage<TKey, TValue> Page(string? after, int limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            if (limit > CatalogCacheOptions.MaximumPageSize)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }
            _owner.RequireCurrent();
            using CatalogDbContext catalog = new(_owner.Identity.Path);
            var query = catalog.Set<SourceRecord>().Where(record => record.Dataset == _source.Name);
            if (after is not null)
            {
                if (JsonSerializer.Deserialize<TKey>(after) is null)
                {
                    throw new ArgumentException("Missing catalog page cursor.", nameof(after));
                }
                query = query.Where(record => record.Key.CompareTo(after) > 0);
            }
            Interlocked.Increment(ref _owner._loads);
            var records = query.OrderBy(record => record.Key).Take(limit + 1).ToArray();
            var rows = records
                .Take(limit)
                .Select(record =>
                {
                    var key = JsonSerializer.Deserialize<TKey>(record.Key)!;
                    return new CatalogRow<TKey, TValue>(key, _memo.Read(key, () => Decode(record)).Value);
                })
                .ToImmutableArray();
            return new(rows, records.Length > limit ? records[limit - 1].Key : null);
        }

        private FrozenDictionary<TKey, TValue> PinAll()
        {
            _owner.RequireCurrent();
            using CatalogDbContext catalog = new(_owner.Identity.Path);
            Interlocked.Increment(ref _owner._loads);
            var values = catalog
                .Set<SourceRecord>()
                .Where(record => record.Dataset == _source.Name)
                .AsEnumerable()
                .ToFrozenDictionary(
                    record => JsonSerializer.Deserialize<TKey>(record.Key)!,
                    record => _memo.Read(JsonSerializer.Deserialize<TKey>(record.Key)!, () => Decode(record)).Value
                );
            Interlocked.Add(ref _owner._pinnedRecords, values.Count);
            return values;
        }
    }
}
