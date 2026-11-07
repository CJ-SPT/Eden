using Eden.Gameplay.Market;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

public sealed class SavedPersonalOrder
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int StationId { get; set; }
    public int SystemId { get; set; }
    public int RegionId { get; set; }
    public int TypeId { get; set; }
    public bool Bid { get; set; }
    public long PriceCents { get; set; }
    public int Remaining { get; set; }
    public int Entered { get; set; }
    public long Issued { get; set; }
    public long Expires { get; set; }
    public long Modified { get; set; }
    public int DurationDays { get; set; }
    public long EscrowCents { get; set; }
    public PersonalOrderStatus Status { get; set; }

    public MarketOrder Snapshot() =>
        new(Id, StationId, SystemId, RegionId, PilotId, TypeId, Bid, PriceCents, Remaining, Entered, Issued, DurationDays, EscrowCents);
}

public sealed class SavedMarketChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string Payload { get; set; } = "";
}

public sealed class SavedMarketPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long CounterpartyId { get; set; }
    public long OrderId { get; set; }
    public WalletEntryType Kind { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public MarketPayment Snapshot() => new(Id, PilotId, CounterpartyId, OrderId, Kind, AmountCents, BalanceCents, Time);
}

public sealed class SavedWorldEconomy
{
    public int Id { get; set; }
    public bool TestingMarket { get; set; } = true;
    public bool TestingGrants { get; set; } = true;
    public int InstitutionalStock { get; set; } = NpcSupplyRules.Stock;
}

internal static class PersonalMarketSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedWorldEconomy>().ToTable("WorldEconomy");
        model.Entity<SavedWorldEconomy>().Property(e => e.InstitutionalStock).HasDefaultValue(NpcSupplyRules.Stock);
        model.Entity<SavedMarketChange>().ToTable("WorldMarketChange");
        model.Entity<SavedMarketChange>().HasIndex(c => new { c.PilotId, c.Id });
        model.Entity<SavedMarketPayment>().ToTable("WorldMarketPayment");
        model.Entity<SavedMarketPayment>().HasIndex(c => new { c.PilotId, c.Time });
        model
            .Entity<SavedPersonalOrder>()
            .ToTable(
                "WorldPersonalOrder",
                table =>
                {
                    table.HasCheckConstraint("CK_PersonalOrder_Quantity", "Remaining >= 0 AND Remaining <= Entered AND Entered > 0");
                    table.HasCheckConstraint("CK_PersonalOrder_Price", "PriceCents > 0 AND EscrowCents >= 0");
                }
            );
        model.Entity<SavedPersonalOrder>().HasIndex(o => new { o.PilotId, o.Status });
        model.Entity<SavedPersonalOrder>().HasIndex(o => new { o.Status, o.Expires });
        model
            .Entity<SavedPersonalOrder>()
            .HasIndex(o => new
            {
                o.RegionId,
                o.Status,
                o.TypeId,
            });
        model
            .Entity<SavedPersonalOrder>()
            .HasIndex(o => new
            {
                o.StationId,
                o.TypeId,
                o.Bid,
                o.Status,
                o.PriceCents,
            });
        model.Entity<SavedPersonalOrder>().HasOne<Pilot>().WithMany().HasForeignKey(o => o.PilotId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldEconomy(Id INTEGER PRIMARY KEY, TestingMarket INTEGER NOT NULL, TestingGrants INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldPersonalOrder(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id),
                StationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, RegionId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                Bid INTEGER NOT NULL, PriceCents INTEGER NOT NULL, Remaining INTEGER NOT NULL, Entered INTEGER NOT NULL,
                Issued INTEGER NOT NULL, Expires INTEGER NOT NULL, Modified INTEGER NOT NULL, DurationDays INTEGER NOT NULL,
                EscrowCents INTEGER NOT NULL, Status INTEGER NOT NULL,
                CONSTRAINT CK_PersonalOrder_Quantity CHECK(Remaining >= 0 AND Remaining <= Entered AND Entered > 0),
                CONSTRAINT CK_PersonalOrder_Price CHECK(PriceCents > 0 AND EscrowCents >= 0));
            CREATE INDEX IF NOT EXISTS IX_WorldPersonalOrder_PilotId_Status ON WorldPersonalOrder(PilotId,Status);
            CREATE INDEX IF NOT EXISTS IX_WorldPersonalOrder_Status_Expires ON WorldPersonalOrder(Status,Expires);
            CREATE INDEX IF NOT EXISTS IX_WorldPersonalOrder_RegionId_Status_TypeId ON WorldPersonalOrder(RegionId,Status,TypeId);
            CREATE INDEX IF NOT EXISTS IX_WorldPersonalOrder_StationId_TypeId_Bid_Status_PriceCents
                ON WorldPersonalOrder(StationId,TypeId,Bid,Status,PriceCents);
            CREATE TABLE IF NOT EXISTS WorldMarketChange(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldMarketChange_PilotId_Id ON WorldMarketChange(PilotId,Id);
            CREATE TABLE IF NOT EXISTS WorldMarketPayment(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL, CounterpartyId INTEGER NOT NULL,
                OrderId INTEGER NOT NULL, Kind INTEGER NOT NULL, AmountCents INTEGER NOT NULL,
                BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldMarketPayment_PilotId_Time ON WorldMarketPayment(PilotId,Time);
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldEconomy')")
                .AsEnumerable()
                .Contains("InstitutionalStock")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldEconomy ADD COLUMN InstitutionalStock INTEGER NOT NULL DEFAULT 1000");
        }
        if (
            world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE name='WorldMarketTrade'").AsEnumerable().Any()
            && !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldMarketTrade')")
                .AsEnumerable()
                .Contains("WalletAmountCents")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldMarketTrade ADD COLUMN WalletAmountCents INTEGER");
        }
    }
}
