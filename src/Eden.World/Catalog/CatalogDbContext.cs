using Eden.Data.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext(string path, bool writable = false) : DbContext
{
    public override void Dispose()
    {
        base.Dispose();
        if (writable)
        {
            LiveWorld.CatalogChanged(path);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (writable)
        {
            LiveWorld.CatalogChanged(path);
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options
            .TracePerformance()
            .UseSqlite(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString()
            )
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

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
