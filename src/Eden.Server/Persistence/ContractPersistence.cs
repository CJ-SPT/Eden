using Eden.Gameplay.Contracts;
using Eden.Gameplay.Market;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedContract
{
    public long Id { get; set; }
    public long IssuerId { get; set; }
    public int IssuerCorporationId { get; set; }
    public long? AssigneeId { get; set; }
    public long? AcceptorId { get; set; }
    public ContractStatus Status { get; set; }
    public int StationId { get; set; }
    public int SystemId { get; set; }
    public int ConstellationId { get; set; }
    public int RegionId { get; set; }
    public long Issued { get; set; }
    public long Expires { get; set; }
    public long? Accepted { get; set; }
    public long? Completed { get; set; }
    public long PriceCents { get; set; }
    public long RewardCents { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public double Volume { get; set; }
    public long BrokerCents { get; set; }
    public long DepositCents { get; set; }
    public long SalesTaxCents { get; set; }
    public bool Reclaimed { get; set; }
    public bool Dismissed { get; set; }

    public PersonalContract Snapshot() =>
        new(
            Id,
            IssuerId,
            IssuerCorporationId,
            AssigneeId,
            AcceptorId,
            Status,
            StationId,
            SystemId,
            ConstellationId,
            RegionId,
            Issued,
            Expires,
            Accepted,
            Completed,
            PriceCents,
            RewardCents,
            Title,
            Description,
            Volume,
            new(BrokerCents, DepositCents, SalesTaxCents)
        );
}

public sealed class SavedContractItem
{
    public long Id { get; set; }
    public long ContractId { get; set; }
    public long ItemId { get; set; }
    public int TypeId { get; set; }
    public int Quantity { get; set; }
    public bool Offered { get; set; }
    public long? ParentId { get; set; }
    public int FlagId { get; set; }
    public bool Singleton { get; set; }
    public double Damage { get; set; }
    public int GroupId { get; set; }
    public int CategoryId { get; set; }

    public ContractItem Snapshot() => new(ItemId, TypeId, Quantity, Offered, ParentId, FlagId, Singleton, Damage, GroupId, CategoryId);
}

public sealed class SavedContractEscrow
{
    public long ItemId { get; set; }
    public long ContractId { get; set; }
}

public sealed class SavedContractPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long CounterpartyId { get; set; }
    public long ContractId { get; set; }
    public WalletEntryType Type { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public ContractPayment Snapshot() => new(Id, PilotId, CounterpartyId, ContractId, Type, AmountCents, BalanceCents, Time);
}

public sealed class SavedContractChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ContractId { get; set; }
    public ContractChangeKind Kind { get; set; }
    public string InventoryJson { get; set; } = "[]";
    public long? BalanceCents { get; set; }
}

[Eden.Protocol.Injectable(Eden.Protocol.InjectionType.Singleton)]
internal sealed class ContractChangeSignal
{
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);

    public void Publish() => Interlocked.Increment(ref _revision);
}

internal static class ContractPersistence
{
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
