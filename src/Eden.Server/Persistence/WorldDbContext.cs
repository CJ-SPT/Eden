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
    public byte[]? Appearance { get; set; }
    public byte[]? Portrait { get; set; }
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

public sealed class ShipEnergy
{
    public long ShipId { get; set; }
    public double Charge { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class WorldAsteroid
{
    public long Id { get; set; }
    public int BeltId { get; set; }
    public int SystemId { get; set; }
    public int TypeId { get; set; }
    public long Quantity { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float Radius { get; set; }
}

public sealed class OwnedItem
{
    public bool Singleton { get; set; }
    public bool Online { get; set; }
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public int TypeId { get; set; }
    public long LocationId { get; set; }
    public int FlagId { get; set; }
    public int Quantity { get; set; }
}

public sealed class WorldDbContext(string path) : DbContext
{
    public DbSet<Pilot> Pilots => Set<Pilot>();
    public DbSet<Ship> Ships => Set<Ship>();
    public DbSet<FlightPosition> Positions => Set<FlightPosition>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                ForeignKeys = true,
                Pooling = false,
            }.ToString()
        );

    protected override void OnModelCreating(ModelBuilder model)
    {
        model
            .Entity<IndustryJob>()
            .ToTable(
                "WorldIndustryJob",
                t =>
                    t.HasCheckConstraint(
                        "CK_IndustryJob_Values",
                        "Runs > 0 AND OutputQuantity > 0 AND DurationSeconds > 0 AND EndTime > StartTime"
                    )
            );
        model.Entity<IndustryJob>().HasOne<Pilot>().WithMany().HasForeignKey(j => j.PilotId);
        model.Entity<IndustryJob>().HasIndex(j => new { j.PilotId, j.Status });
        model.Entity<IndustryJob>().HasIndex(j => j.BlueprintId);
        model
            .Entity<WorldWreck>()
            .ToTable(
                "WorldWreck",
                t =>
                    t.HasCheckConstraint(
                        "CK_Wreck_Source",
                        "(NpcId IS NOT NULL AND ShipId IS NULL) OR (NpcId IS NULL AND ShipId IS NOT NULL)"
                    )
            )
            .HasKey(w => w.Id);
        model.Entity<WorldWreck>().Property(w => w.Id).ValueGeneratedNever();
        model.Entity<WorldWreck>().HasOne<WorldNpc>().WithMany().HasForeignKey(w => w.NpcId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorldWreck>().HasOne<ShipLoss>().WithMany().HasForeignKey(w => w.ShipId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorldWreck>().HasOne<Pilot>().WithMany().HasForeignKey(w => w.OwnerId);
        model.Entity<WorldWreck>().HasIndex(w => w.NpcId).IsUnique();
        model.Entity<WorldWreck>().HasIndex(w => w.ShipId).IsUnique();
        model.Entity<WorldWreck>().HasIndex(w => new { w.SystemId, w.ExpiresAt });
        model.Entity<StationRepairPayment>().ToTable("WorldRepairPayment").HasKey(p => p.Id);
        model.Entity<StationRepairPayment>().HasOne<Pilot>().WithMany().HasForeignKey(p => p.PilotId);
        model.Entity<StationRepairPayment>().HasIndex(p => new { p.PilotId, p.Time });
        model.Entity<NpcAggression>().ToTable("WorldNpcAggression").HasKey(a => a.NpcId);
        model.Entity<NpcAggression>().HasOne<WorldNpc>().WithMany().HasForeignKey(a => a.NpcId);
        model.Entity<NpcAggression>().HasOne<Ship>().WithMany().HasForeignKey(a => a.TargetShipId);
        model.Entity<NpcAggression>().HasIndex(a => a.TargetShipId);
        model.Entity<ShipLoss>().ToTable("WorldShipLoss").HasKey(l => l.ShipId);
        model.Entity<ShipLoss>().Property(l => l.ShipId).ValueGeneratedNever();
        model.Entity<ShipLoss>().HasOne<Pilot>().WithMany().HasForeignKey(l => l.PilotId);
        model.Entity<ShipLoss>().HasOne<WorldNpc>().WithMany().HasForeignKey(l => l.NpcId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ShipLoss>().HasIndex(l => new { l.PilotId, l.Time });
        model
            .Entity<PendingArmorRepair>()
            .ToTable("WorldArmorRepair", t => t.HasCheckConstraint("CK_ArmorRepair_Amount", "Amount > 0"))
            .HasKey(r => r.ModuleId);
        model.Entity<PendingArmorRepair>().HasOne<OwnedItem>().WithMany().HasForeignKey(r => r.ModuleId);
        model.Entity<PendingArmorRepair>().HasOne<Ship>().WithMany().HasForeignKey(r => r.ShipId);
        model.Entity<PendingArmorRepair>().HasIndex(r => new { r.ShipId, r.CompletesAt });
        model.Entity<ActiveHardener>().ToTable("WorldActiveHardener").HasKey(h => h.ModuleId);
        model.Entity<ActiveHardener>().HasOne<OwnedItem>().WithMany().HasForeignKey(h => h.ModuleId);
        model.Entity<ActiveHardener>().HasOne<Ship>().WithMany().HasForeignKey(h => h.ShipId);
        model.Entity<ModuleCooldown>().ToTable("WorldWeaponCooldown").HasKey(w => w.ModuleId);
        model.Entity<ModuleCooldown>().HasOne<OwnedItem>().WithMany().HasForeignKey(w => w.ModuleId);
        model
            .Entity<NpcBounty>()
            .ToTable("WorldNpcBounty", t => t.HasCheckConstraint("CK_NpcBounty_Amounts", "AmountCents > 0 AND BalanceCents >= 0"));
        model.Entity<NpcBounty>().HasOne<Pilot>().WithMany().HasForeignKey(b => b.PilotId);
        model.Entity<NpcBounty>().HasOne<WorldNpc>().WithMany().HasForeignKey(b => b.NpcId);
        model.Entity<NpcBounty>().HasIndex(b => b.NpcId).IsUnique();
        model.Entity<NpcBounty>().HasIndex(b => new { b.PilotId, b.Time });
        model
            .Entity<WorldNpc>()
            .ToTable(
                "WorldNpc",
                t => t.HasCheckConstraint("CK_Npc_Health", "ShieldCharge >= 0 AND ArmorDamage >= 0 AND StructureDamage >= 0")
            );
        model.Entity<WorldNpc>().HasIndex(n => n.BeltId);
        model.Entity<WorldNpc>().HasIndex(n => n.SystemId);
        model
            .Entity<ShipHealthState>()
            .ToTable(
                "WorldShipHealth",
                t => t.HasCheckConstraint("CK_ShipHealth_Values", "ShieldCharge >= 0 AND ArmorDamage >= 0 AND StructureDamage >= 0")
            )
            .HasKey(s => s.ShipId);
        model.Entity<ShipHealthState>().HasOne<Ship>().WithMany().HasForeignKey(s => s.ShipId);
        model
            .Entity<PilotWallet>()
            .ToTable("WorldWallet", t => t.HasCheckConstraint("CK_Wallet_Cents", "Cents >= 0"))
            .HasKey(w => w.PilotId);
        model.Entity<PilotWallet>().HasOne<Pilot>().WithMany().HasForeignKey(w => w.PilotId);
        model.Entity<MarketStation>().ToTable("WorldMarketStation").HasKey(s => s.StationId);
        model.Entity<MarketStation>().Property(s => s.SeedVersion).HasDefaultValue(0);
        model
            .Entity<TestingWalletGrant>()
            .ToTable("WorldTestingGrant", t => t.HasCheckConstraint("CK_TestingGrant_Amounts", "AmountCents > 0 AND BalanceCents >= 0"))
            .HasKey(g => g.PilotId);
        model.Entity<TestingWalletGrant>().HasOne<Pilot>().WithMany().HasForeignKey(g => g.PilotId);
        model
            .Entity<NpcMarketOrder>()
            .ToTable(
                "WorldMarketOrder",
                t =>
                {
                    t.HasCheckConstraint("CK_MarketOrder_Price", "PriceCents > 0");
                    t.HasCheckConstraint("CK_MarketOrder_Quantity", "Remaining >= 0 AND Entered > 0 AND Remaining <= Entered");
                }
            );
        model
            .Entity<NpcMarketOrder>()
            .HasIndex(o => new
            {
                o.StationId,
                o.TypeId,
                o.Bid,
            })
            .IsUnique();
        model.Entity<NpcMarketOrder>().HasIndex(o => o.RegionId);
        model
            .Entity<NpcMarketOrder>()
            .HasIndex(o => new
            {
                o.RegionId,
                o.TypeId,
                o.Bid,
                o.PriceCents,
            });
        model
            .Entity<MarketExecution>()
            .ToTable(
                "WorldMarketTrade",
                t =>
                {
                    t.HasCheckConstraint(
                        "CK_MarketTrade_Amounts",
                        "PriceCents > 0 AND Quantity > 0 AND TaxCents >= 0 AND BalanceCents >= 0"
                    );
                }
            );
        model.Entity<MarketExecution>().HasOne<Pilot>().WithMany().HasForeignKey(t => t.PilotId);
        model.Entity<MarketExecution>().HasIndex(t => new { t.PilotId, t.Time });
        model
            .Entity<ShipEnergy>()
            .ToTable("WorldShipEnergy", t => t.HasCheckConstraint("CK_ShipEnergy_Charge", "Charge >= 0"))
            .HasKey(e => e.ShipId);
        model.Entity<ShipEnergy>().HasOne<Ship>().WithMany().HasForeignKey(e => e.ShipId);
        model.Entity<WorldAsteroid>().ToTable("WorldAsteroid", t => t.HasCheckConstraint("CK_Asteroid_Quantity", "Quantity >= 0"));
        model.Entity<WorldAsteroid>().Property(a => a.Id).ValueGeneratedNever();
        model.Entity<WorldAsteroid>().HasIndex(a => a.SystemId);
        model.Entity<WorldAsteroid>().HasIndex(a => a.BeltId);
        model.Entity<OwnedItem>().ToTable("WorldItem", t => t.HasCheckConstraint("CK_Item_Quantity", "Quantity > 0"));
        model.Entity<OwnedItem>().Property(i => i.Singleton).HasDefaultValue(false);
        model.Entity<OwnedItem>().Property(i => i.Online).HasDefaultValue(false);
        model.Entity<OwnedItem>().HasOne<Pilot>().WithMany().HasForeignKey(i => i.OwnerId);
        model.Entity<OwnedItem>().HasIndex(i => new { i.OwnerId, i.LocationId });
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
        model.Entity<Pilot>().HasIndex(p => p.AccountId);
        model.Entity<Pilot>().Property(p => p.Name).UseCollation("NOCASE");
        model.Entity<Pilot>().HasIndex(p => p.Name).IsUnique();
        model
            .Entity<Pilot>()
            .HasOne(p => p.ActiveShip)
            .WithMany()
            .HasForeignKey(p => new { p.ActiveShipId, p.Id })
            .HasPrincipalKey(s => new { s.Id, s.OwnerId })
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ship>().HasOne(s => s.Owner).WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<Ship>().HasOne(s => s.Position).WithOne().HasForeignKey<FlightPosition>(p => p.ShipId);
    }
}
