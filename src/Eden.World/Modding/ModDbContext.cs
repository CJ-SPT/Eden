using Eden.Data.World.Modding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Modding;

internal sealed class ModDbContext(string worldPath) : DbContext
{
    internal static string FilePath(string worldPath) => Path.GetFullPath(worldPath) + ".mods.sqlite";

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = FilePath(worldPath), Pooling = false }.ToString());

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SavedModConfiguration>().ToTable("ModConfiguration").HasKey(row => row.Id);
        model.Entity<SavedModIdentity>().ToTable("ModIdentity").HasKey(row => row.LogicalId);
        model.Entity<SavedModIdentity>().HasIndex(row => row.TypeId).IsUnique();
        model.Entity<SavedModState>().ToTable("ModState").HasKey(row => new { row.ModId, row.Key });
    }
}
