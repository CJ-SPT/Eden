using System.Text.Json;
using Eden.Data.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public interface ICatalogSource
{
    string Name { get; }
    Type KeyType { get; }
    Type ValueType { get; }
    CatalogDatasetDescriptor Descriptor { get; }
    internal void Configure(ModelBuilder model);
    internal long Verify(CatalogDbContext db);
    internal CatalogBrowsePage Browse(CatalogProvider catalog, string? after, int limit, string? search, string? key);
}

/// <summary>A build-specific SQLite view with a typed payload and an indexed source key.</summary>
public abstract class CatalogSource<T>(string name) : ICatalogSource
{
    public string Name { get; } = name;
    public abstract Type KeyType { get; }
    public Type ValueType => typeof(T);
    public CatalogDatasetDescriptor Descriptor => CatalogSchemas.Get(Name);

    public IQueryable<CatalogEntry<T>> Query(CatalogDbContext db) => db.Set<CatalogEntry<T>>(Name);

    protected T GetEncoded(CatalogDbContext db, string encoded)
    {
        return Query(db).Single(r => r.Key == encoded).Value;
    }

    protected abstract void ValidateKey(string encoded);

    internal abstract CatalogBrowsePage Browse(CatalogProvider catalog, string? after, int limit, string? search, string? key);

    CatalogBrowsePage ICatalogSource.Browse(CatalogProvider catalog, string? after, int limit, string? search, string? key) =>
        Browse(catalog, after, limit, search, key);

    void ICatalogSource.Configure(ModelBuilder model)
    {
        var entity = model.SharedTypeEntity<CatalogEntry<T>>(Name);
        entity.HasNoKey().ToView("records_" + Name.Replace('/', '_'));
        entity
            .Property(e => e.Value)
            .HasColumnName("Data")
            .HasConversion(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Default)!
            );
    }

    long ICatalogSource.Verify(CatalogDbContext db)
    {
        long count = 0;
        // Materialize every payload through its EF mapping before activating the catalog.
        foreach (var entry in Query(db).AsEnumerable())
        {
            ValidateKey(entry.Key);
            if (entry.Value is null)
            {
                throw new InvalidDataException($"Missing {Name} record {entry.Key}.");
            }
            count++;
        }
        return count;
    }
}

public sealed class CatalogSource<TKey, TValue>(string name) : CatalogSource<TValue>(name)
    where TKey : notnull
{
    internal override CatalogBrowsePage Browse(CatalogProvider catalog, string? after, int limit, string? search, string? key) =>
        catalog.Browse(this, after, limit, search, key);

    public override Type KeyType => typeof(TKey);

    public TValue Get(CatalogDbContext db, TKey key) => GetEncoded(db, CatalogKey.Encode(key));

    protected override void ValidateKey(string encoded)
    {
        if (JsonSerializer.Deserialize<TKey>(encoded) is null)
        {
            throw new InvalidDataException($"Missing {Name} key.");
        }
    }
}
