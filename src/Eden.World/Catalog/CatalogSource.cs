using System.Text.Json;
using Eden.Data.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public interface ICatalogSource
{
    string Name { get; }
    internal void Configure(ModelBuilder model);
    internal long Verify(CatalogDbContext db);
}

/// <summary>A build-specific SQLite view with a typed payload and an indexed source key.</summary>
public sealed class CatalogSource<T>(string name) : ICatalogSource
{
    public string Name { get; } = name;

    public IQueryable<CatalogEntry<T>> Query(CatalogDbContext db) => db.Set<CatalogEntry<T>>(Name);

    public T Get<TKey>(CatalogDbContext db, TKey key)
    {
        string encoded = JsonSerializer.Serialize(key);
        return Query(db).Single(r => r.Key == encoded).Value;
    }

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
            if (entry.Value is null)
            {
                throw new InvalidDataException($"Missing {Name} record {entry.Key}.");
            }
            count++;
        }
        return count;
    }
}
