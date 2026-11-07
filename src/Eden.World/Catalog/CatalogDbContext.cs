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
        if (!writable && _fittingInputs.IsScoped)
        {
            // Service operations share one immutable catalog connection. Contexts
            // do not own it; the outer operation releases it after all readers.
            options.UseSqlite(_fittingInputs.Connection(path), contextOwnsConnection: false);
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
