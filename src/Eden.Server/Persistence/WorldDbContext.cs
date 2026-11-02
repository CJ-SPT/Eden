using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class Account
{
    public long Id { get; set; }
}

public sealed class Pilot
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string Name { get; set; } = "";
    public int CorporationId { get; set; }
    public long ActiveShipId { get; set; }
    public Ship ActiveShip { get; set; } = null!;
}

public sealed class Ship
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public Pilot Owner { get; set; } = null!;
    public int TypeId { get; set; }
    public int StationId { get; set; }
    public string Name { get; set; } = "";
    public FlightPosition? Position { get; set; }
}

public sealed class FlightPosition
{
    public long ShipId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class WorldDbContext(string path) : DbContext
{
    public DbSet<Pilot> Pilots => Set<Pilot>();
    public DbSet<Ship> Ships => Set<Ship>();
    public DbSet<FlightPosition> Positions => Set<FlightPosition>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(
        new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Account>().ToTable("WorldAccount");
        model.Entity<Pilot>().ToTable("WorldPilot");
        model.Entity<Ship>().ToTable("WorldShip");
        model.Entity<FlightPosition>().ToTable("WorldPosition").HasKey(p => p.ShipId);
        model.Entity<Pilot>().HasIndex(p => p.AccountId).IsUnique();
        model.Entity<Pilot>().HasOne(p => p.ActiveShip).WithMany()
            .HasForeignKey(p => new { p.ActiveShipId, p.Id }).HasPrincipalKey(s => new { s.Id, s.OwnerId })
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ship>().HasOne(s => s.Owner).WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ship>().HasOne(s => s.Position).WithOne().HasForeignKey<FlightPosition>(p => p.ShipId);
    }
}
