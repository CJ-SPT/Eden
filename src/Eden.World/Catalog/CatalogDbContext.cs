using System.Text.Json;
using Eden.Data.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext(string path, bool writable = false) : DbContext
{
    public CatalogProvider Definitions =>
        writable
            ? throw new InvalidOperationException("Use direct writable catalog reads while editing a fixture or import.")
            : CatalogProvider.Open(path);

    internal TValue? Read<TKey, TValue>(CatalogSource<TKey, TValue> source, TKey key)
        where TKey : notnull
        where TValue : class =>
        writable ? Set<SourceRecord>().Find(source.Name, CatalogKey.Encode(key))?.Read<TValue>() : Definitions.Find(source, key);

    internal TValue Require<TKey, TValue>(CatalogSource<TKey, TValue> source, TKey key)
        where TKey : notnull
        where TValue : class => Read(source, key) ?? throw new InvalidDataException($"Missing {source.Name} record {key}.");

    internal T Definition<T>(CatalogDefinition<T> definition, int typeId) =>
        writable ? definition.Load(this, typeId) : Definitions.Definition(definition, typeId);

    internal IReadOnlyDictionary<TKey, TValue> ReadMany<TKey, TValue>(CatalogSource<TKey, TValue> source, IEnumerable<TKey> keys)
        where TKey : notnull
        where TValue : class =>
        writable
            ? keys.Distinct()
                .Select(key => new { Key = key, Value = Read(source, key) })
                .Where(row => row.Value is not null)
                .ToDictionary(row => row.Key, row => row.Value!)
            : Definitions.FindMany(source, keys);

    internal IEnumerable<KeyValuePair<TKey, TValue>> RecordsInSourceOrder<TKey, TValue>(CatalogSource<TKey, TValue> source)
        where TKey : notnull => Records(source).OrderBy(record => CatalogKey.Encode(record.Key), StringComparer.Ordinal);

    internal IReadOnlyDictionary<TKey, TValue> Records<TKey, TValue>(CatalogSource<TKey, TValue> source)
        where TKey : notnull =>
        writable
            ? Set<SourceRecord>()
                .Where(record => record.Dataset == source.Name)
                .AsEnumerable()
                .ToDictionary(record => JsonSerializer.Deserialize<TKey>(record.Key)!, record => record.Read<TValue>())
            : Definitions.Pin(source);

    public override void Dispose()
    {
        base.Dispose();
        if (writable)
        {
            CatalogProvider.Invalidate(path);
            ResidentFitCache.CatalogChanged(path);
            LiveWorld.CatalogChanged(path);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (writable)
        {
            CatalogProvider.Invalidate(path);
            ResidentFitCache.CatalogChanged(path);
            LiveWorld.CatalogChanged(path);
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.TracePerformance();
        if (!writable && _connectionInputs.IsScoped)
        {
            // Service operations share one immutable catalog connection. Contexts
            // do not own it; the outer operation releases it after all readers.
            options.UseSqlite(_connectionInputs.Connection(path), contextOwnsConnection: false);
        }
        else
        {
            options.UseSqlite(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString()
            );
        }
        options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Station>();
        model.Entity<StationDesignation>().HasKey(s => s.StationId);
        model.Entity<StationDesignation>().ToView("StationDesignation");
        model.Entity<Stargate>();
        model.Entity<ItemType>().HasMany(t => t.Attributes).WithOne().HasForeignKey(a => a.ItemTypeId);
        CatalogSources.Configure(model);
    }
}
