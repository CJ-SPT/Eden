using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class Account
{
    public string UserName { get; set; } = "";
    public long CreatedAt { get; set; }
    public long Id { get; set; }
}

public sealed class Pilot
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string Name { get; set; } = "";
    public int CorporationId { get; set; }
    public int RaceId { get; set; }
    public int BloodlineId { get; set; }
    public int GenderId { get; set; } = -1;
    public int AncestryId { get; set; }
    public int SchoolId { get; set; }
    public long CreatedAt { get; set; }
    public long ActiveShipId { get; set; }
    public Ship ActiveShip { get; set; } = null!;
}

[PrimaryKey(nameof(PilotId), nameof(TypeId))]
public sealed class PilotSkill
{
    public long PilotId { get; set; }
    public int TypeId { get; set; }
    public long Points { get; set; }
}

[PrimaryKey(nameof(PilotId), nameof(Position))]
public sealed class QueuedLevel
{
    public long PilotId { get; set; }
    public int Position { get; set; }
    public int TypeId { get; set; }
    public int Level { get; set; }
    public long StartPoints { get; set; }
    public long EndPoints { get; set; }
    public long? StartTime { get; set; }
    public long? EndTime { get; set; }
}

public sealed class TrainingLog
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long Time { get; set; }
    public int TypeId { get; set; }
    public long Points { get; set; }
    public int EventId { get; set; }
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
    public int? SystemId { get; set; }
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
        // Defaults preserve the identities previously returned by character selection.
        model.Entity<Account>().Property(a => a.UserName).HasDefaultValue("Eden");
        model.Entity<Account>().Property(a => a.CreatedAt).HasDefaultValue(134000000000000000L);
        model.Entity<Pilot>().Property(p => p.CreatedAt).HasDefaultValue(134000000000000000L);
        model.Entity<Pilot>().Property(p => p.RaceId).HasDefaultValue(1);
        model.Entity<Pilot>().Property(p => p.BloodlineId).HasDefaultValue(1);
        model.Entity<Pilot>().Property(p => p.GenderId).HasDefaultValue(1).HasSentinel(-1);
        model.Entity<Pilot>().Property(p => p.AncestryId).HasDefaultValue(10);
        model.Entity<Pilot>().Property(p => p.SchoolId).HasDefaultValue(19);
        model.Entity<PilotSkill>().ToTable("WorldPilotSkill", t => t.HasCheckConstraint("CK_PilotSkill_Points", "Points >= 0"));
        model.Entity<PilotSkill>().HasOne<Pilot>().WithMany().HasForeignKey(s => s.PilotId);
        model.Entity<QueuedLevel>().ToTable("WorldSkillQueue").HasOne<Pilot>().WithMany().HasForeignKey(e => e.PilotId);
        model.Entity<TrainingLog>().ToTable("WorldSkillHistory").HasOne<Pilot>().WithMany().HasForeignKey(e => e.PilotId);
        model.Entity<TrainingLog>().HasIndex(e => new { e.PilotId, e.Time });
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
