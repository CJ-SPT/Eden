using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Market;
using Eden.World.Industry;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Economy;

internal static class EconomicSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedEconomicPolicy>().ToTable("WorldEconomicPolicy");
        model.Entity<SavedAdjustedPrice>().ToTable("WorldAdjustedPrice").HasKey(p => p.TypeId);
        model.Entity<SavedManufacturingIndex>().ToTable("WorldManufacturingIndex").HasKey(p => p.SystemId);
        model.Entity<SavedBeltRenewal>().ToTable("WorldBeltRenewal").HasKey(b => b.BeltId);
        model.Entity<SavedBeltRenewal>().HasIndex(b => b.NextRenewal);
        model.Entity<SavedServicePayment>().ToTable("WorldServicePayment");
        model.Entity<SavedServicePayment>().HasIndex(p => new { p.PilotId, p.Time });
        model.Entity<IndustryJob>().Property(j => j.CostCents).HasDefaultValue(0L);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldEconomicPolicy(
                Id INTEGER PRIMARY KEY, ChargeServiceCosts INTEGER NOT NULL, RenewBelts INTEGER NOT NULL,
                RenewalHourUtc INTEGER NOT NULL, DefaultManufacturingIndex TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldAdjustedPrice(TypeId INTEGER PRIMARY KEY, PriceIsk TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldManufacturingIndex(SystemId INTEGER PRIMARY KEY, "Index" TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldBeltRenewal(
                BeltId INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL, NextRenewal INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldBeltRenewal_NextRenewal ON WorldBeltRenewal(NextRenewal);
            CREATE TABLE IF NOT EXISTS WorldServicePayment(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL, CounterpartyId INTEGER NOT NULL,
                ContextId INTEGER NOT NULL, Kind INTEGER NOT NULL, AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL,
                Time INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldServicePayment_PilotId_Time ON WorldServicePayment(PilotId,Time);
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldIndustryJob')")
                .AsEnumerable()
                .Contains("CostCents")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldIndustryJob ADD COLUMN CostCents INTEGER NOT NULL DEFAULT 0");
        }
    }
}
