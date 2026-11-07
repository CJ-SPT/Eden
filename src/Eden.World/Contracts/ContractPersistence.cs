using Eden.Data.World.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Market;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Contracts;

[Eden.Infrastructure.Registration.Injectable(Eden.Infrastructure.Registration.InjectionType.Singleton)]
internal sealed class ContractChangeSignal
{
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);

    public void Publish() => Interlocked.Increment(ref _revision);
}

internal static class ContractPersistence
{
    internal static void UpgradeAuctions(WorldDbContext world)
    {
        var columns = world
            .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldContract')")
            .AsEnumerable()
            .ToHashSet();
        foreach (
            var (name, sql) in new (string, string)[]
            {
                ("BuyoutCents", "ALTER TABLE WorldContract ADD COLUMN BuyoutCents INTEGER NOT NULL DEFAULT 0"),
                ("CurrentBidCents", "ALTER TABLE WorldContract ADD COLUMN CurrentBidCents INTEGER NOT NULL DEFAULT 0"),
                ("AuctionAccountingLevel", "ALTER TABLE WorldContract ADD COLUMN AuctionAccountingLevel INTEGER NOT NULL DEFAULT 0"),
                ("IssuerCollected", "ALTER TABLE WorldContract ADD COLUMN IssuerCollected INTEGER NOT NULL DEFAULT 0"),
                ("WinnerCollected", "ALTER TABLE WorldContract ADD COLUMN WinnerCollected INTEGER NOT NULL DEFAULT 0"),
            }
        )
        {
            if (!columns.Contains(name))
            {
                world.Database.ExecuteSqlRaw(sql);
            }
        }
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldContractBid(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ContractId INTEGER NOT NULL REFERENCES WorldContract(Id) ON DELETE CASCADE,
                BidderId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                AmountCents INTEGER NOT NULL, Time INTEGER NOT NULL, Dismissed INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS IX_WorldContractBid_ContractId_Id ON WorldContractBid(ContractId,Id);
            CREATE INDEX IF NOT EXISTS IX_WorldContractBid_BidderId_ContractId ON WorldContractBid(BidderId,ContractId);
            """
        );
    }

    internal static void UpgradeCourier(WorldDbContext world)
    {
        // Historical test saves can retain newer additive fields. Check the actual
        // columns before upgrading, as with the existing idempotent feature tables.
        var columns = world
            .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldContract')")
            .AsEnumerable()
            .ToHashSet();
        foreach (
            var (name, sql) in new (string, string)[]
            {
                ("Type", "ALTER TABLE WorldContract ADD COLUMN Type INTEGER NOT NULL DEFAULT 1"),
                ("EndStationId", "ALTER TABLE WorldContract ADD COLUMN EndStationId INTEGER NOT NULL DEFAULT 0"),
                ("EndSystemId", "ALTER TABLE WorldContract ADD COLUMN EndSystemId INTEGER NOT NULL DEFAULT 0"),
                ("EndConstellationId", "ALTER TABLE WorldContract ADD COLUMN EndConstellationId INTEGER NOT NULL DEFAULT 0"),
                ("EndRegionId", "ALTER TABLE WorldContract ADD COLUMN EndRegionId INTEGER NOT NULL DEFAULT 0"),
                ("DurationDays", "ALTER TABLE WorldContract ADD COLUMN DurationDays INTEGER NOT NULL DEFAULT 0"),
                ("CollateralCents", "ALTER TABLE WorldContract ADD COLUMN CollateralCents INTEGER NOT NULL DEFAULT 0"),
                ("PackageId", "ALTER TABLE WorldContract ADD COLUMN PackageId INTEGER"),
            }
        )
        {
            if (!columns.Contains(name))
            {
                world.Database.ExecuteSqlRaw(sql);
            }
        }
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldItem')")
                .AsEnumerable()
                .Contains(nameof(OwnedItem.VolumeOverride))
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldItem ADD COLUMN VolumeOverride REAL");
        }
        world.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldContract_PackageId ON WorldContract(PackageId)");
    }

    // Create before historical item/ship migrations query the new escrow filters.
    internal static void CreateSchema(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldContract(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                IssuerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                IssuerCorporationId INTEGER NOT NULL, AssigneeId INTEGER REFERENCES WorldPilot(Id),
                AcceptorId INTEGER REFERENCES WorldPilot(Id), Status INTEGER NOT NULL,
                StationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, ConstellationId INTEGER NOT NULL, RegionId INTEGER NOT NULL,
                Issued INTEGER NOT NULL, Expires INTEGER NOT NULL, Accepted INTEGER, Completed INTEGER,
                PriceCents INTEGER NOT NULL, RewardCents INTEGER NOT NULL, Title TEXT NOT NULL, Description TEXT NOT NULL,
                Volume REAL NOT NULL, BrokerCents INTEGER NOT NULL, DepositCents INTEGER NOT NULL, SalesTaxCents INTEGER NOT NULL,
                Reclaimed INTEGER NOT NULL, Dismissed INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldContract_IssuerId_Status ON WorldContract(IssuerId,Status);
            CREATE INDEX IF NOT EXISTS IX_WorldContract_AssigneeId_Status ON WorldContract(AssigneeId,Status);
            CREATE INDEX IF NOT EXISTS IX_WorldContract_AcceptorId ON WorldContract(AcceptorId);
            CREATE TABLE IF NOT EXISTS WorldContractItem(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ContractId INTEGER NOT NULL REFERENCES WorldContract(Id) ON DELETE CASCADE,
                ItemId INTEGER NOT NULL, TypeId INTEGER NOT NULL, Quantity INTEGER NOT NULL, Offered INTEGER NOT NULL,
                ParentId INTEGER, FlagId INTEGER NOT NULL, Singleton INTEGER NOT NULL, Damage REAL NOT NULL,
                GroupId INTEGER NOT NULL, CategoryId INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldContractItem_ContractId ON WorldContractItem(ContractId);
            CREATE TABLE IF NOT EXISTS WorldContractEscrow(
                ItemId INTEGER NOT NULL PRIMARY KEY, ContractId INTEGER NOT NULL REFERENCES WorldContract(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_WorldContractEscrow_ContractId ON WorldContractEscrow(ContractId);
            CREATE TABLE IF NOT EXISTS WorldContractPayment(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, CounterpartyId INTEGER NOT NULL,
                ContractId INTEGER NOT NULL REFERENCES WorldContract(Id) ON DELETE CASCADE,
                Type INTEGER NOT NULL, AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldContractPayment_ContractId ON WorldContractPayment(ContractId);
            CREATE INDEX IF NOT EXISTS IX_WorldContractPayment_PilotId_Id ON WorldContractPayment(PilotId,Id);
            CREATE TABLE IF NOT EXISTS WorldContractChange(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ContractId INTEGER NOT NULL REFERENCES WorldContract(Id) ON DELETE CASCADE,
                Kind INTEGER NOT NULL, InventoryJson TEXT NOT NULL, BalanceCents INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldContractChange_ContractId ON WorldContractChange(ContractId);
            CREATE INDEX IF NOT EXISTS IX_WorldContractChange_PilotId_Id ON WorldContractChange(PilotId,Id);
            """
        );
}
