using System.Text.Json;
using Eden.Data.World.Contracts;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyContract(
    long Id,
    long BuyoutCents,
    long CurrentBidCents,
    int AuctionAccountingLevel,
    bool IssuerCollected,
    bool WinnerCollected,
    ContractType Type,
    int EndStationId,
    int EndSystemId,
    int EndConstellationId,
    int EndRegionId,
    int DurationDays,
    long CollateralCents,
    long? PackageId,
    long IssuerId,
    int IssuerCorporationId,
    long? AssigneeId,
    long? AcceptorId,
    ContractStatus Status,
    int StationId,
    int SystemId,
    int ConstellationId,
    int RegionId,
    long Issued,
    long Expires,
    long? Accepted,
    long? Completed,
    long PriceCents,
    long RewardCents,
    string Title,
    string Description,
    double Volume,
    long BrokerCents,
    long DepositCents,
    long SalesTaxCents,
    bool Reclaimed,
    bool Dismissed
);

internal sealed record EconomyContractBid(long Id, long ContractId, long BidderId, long AmountCents, long Time, bool Dismissed);

internal sealed record EconomyContractChange(
    long Id,
    long PilotId,
    long ContractId,
    ContractChangeKind Kind,
    string InventoryJson,
    long? BalanceCents
);

internal sealed record EconomyContractItem(
    long Id,
    int BlueprintRuns,
    int MaterialEfficiency,
    int TimeEfficiency,
    long ContractId,
    long ItemId,
    int TypeId,
    int Quantity,
    bool Offered,
    long? ParentId,
    int FlagId,
    bool Singleton,
    double Damage,
    int GroupId,
    int CategoryId
);

internal sealed record EconomyContractPayment(
    long Id,
    long PilotId,
    long CounterpartyId,
    long ContractId,
    WalletEntryType Type,
    long AmountCents,
    long BalanceCents,
    long Time
);

internal sealed partial class ResidentEconomy
{
    private void RegisterContracts()
    {
        Register(
            new EconomyTable<SavedContract, EconomyContract>(
                "WorldContract",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.BuyoutCents,
                        row.CurrentBidCents,
                        row.AuctionAccountingLevel,
                        row.IssuerCollected,
                        row.WinnerCollected,
                        row.Type,
                        row.EndStationId,
                        row.EndSystemId,
                        row.EndConstellationId,
                        row.EndRegionId,
                        row.DurationDays,
                        row.CollateralCents,
                        row.PackageId,
                        row.IssuerId,
                        row.IssuerCorporationId,
                        row.AssigneeId,
                        row.AcceptorId,
                        row.Status,
                        row.StationId,
                        row.SystemId,
                        row.ConstellationId,
                        row.RegionId,
                        row.Issued,
                        row.Expires,
                        row.Accepted,
                        row.Completed,
                        row.PriceCents,
                        row.RewardCents,
                        row.Title,
                        row.Description,
                        row.Volume,
                        row.BrokerCents,
                        row.DepositCents,
                        row.SalesTaxCents,
                        row.Reclaimed,
                        row.Dismissed
                    ),
                state => new SavedContract
                {
                    Id = state.Id,
                    BuyoutCents = state.BuyoutCents,
                    CurrentBidCents = state.CurrentBidCents,
                    AuctionAccountingLevel = state.AuctionAccountingLevel,
                    IssuerCollected = state.IssuerCollected,
                    WinnerCollected = state.WinnerCollected,
                    Type = state.Type,
                    EndStationId = state.EndStationId,
                    EndSystemId = state.EndSystemId,
                    EndConstellationId = state.EndConstellationId,
                    EndRegionId = state.EndRegionId,
                    DurationDays = state.DurationDays,
                    CollateralCents = state.CollateralCents,
                    PackageId = state.PackageId,
                    IssuerId = state.IssuerId,
                    IssuerCorporationId = state.IssuerCorporationId,
                    AssigneeId = state.AssigneeId,
                    AcceptorId = state.AcceptorId,
                    Status = state.Status,
                    StationId = state.StationId,
                    SystemId = state.SystemId,
                    ConstellationId = state.ConstellationId,
                    RegionId = state.RegionId,
                    Issued = state.Issued,
                    Expires = state.Expires,
                    Accepted = state.Accepted,
                    Completed = state.Completed,
                    PriceCents = state.PriceCents,
                    RewardCents = state.RewardCents,
                    Title = state.Title,
                    Description = state.Description,
                    Volume = state.Volume,
                    BrokerCents = state.BrokerCents,
                    DepositCents = state.DepositCents,
                    SalesTaxCents = state.SalesTaxCents,
                    Reclaimed = state.Reclaimed,
                    Dismissed = state.Dismissed,
                },
                ProjectSavedContract,
                "Id,BuyoutCents,CurrentBidCents,AuctionAccountingLevel,IssuerCollected,WinnerCollected,Type,EndStationId,"
                    + "EndSystemId,EndConstellationId,EndRegionId,DurationDays,CollateralCents,PackageId,IssuerId,IssuerCorporationId,"
                    + "AssigneeId,AcceptorId,Status,StationId,SystemId,ConstellationId,RegionId,Issued,"
                    + "Expires,Accepted,Completed,PriceCents,RewardCents,Title,Description,Volume,"
                    + "BrokerCents,DepositCents,SalesTaxCents,Reclaimed,Dismissed",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetBoolean(5),
                        reader.GetBoolean(6),
                        (ContractType)reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10),
                        reader.GetInt32(11),
                        reader.GetInt32(12),
                        reader.GetInt64(13),
                        reader.IsDBNull(14) ? null : reader.GetInt64(14),
                        reader.GetInt64(15),
                        reader.GetInt32(16),
                        reader.IsDBNull(17) ? null : reader.GetInt64(17),
                        reader.IsDBNull(18) ? null : reader.GetInt64(18),
                        (ContractStatus)reader.GetInt32(19),
                        reader.GetInt32(20),
                        reader.GetInt32(21),
                        reader.GetInt32(22),
                        reader.GetInt32(23),
                        reader.GetInt64(24),
                        reader.GetInt64(25),
                        reader.IsDBNull(26) ? null : reader.GetInt64(26),
                        reader.IsDBNull(27) ? null : reader.GetInt64(27),
                        reader.GetInt64(28),
                        reader.GetInt64(29),
                        reader.GetString(30),
                        reader.GetString(31),
                        reader.GetDouble(32),
                        reader.GetInt64(33),
                        reader.GetInt64(34),
                        reader.GetInt64(35),
                        reader.GetBoolean(36),
                        reader.GetBoolean(37)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("IssuerId", row => row.IssuerId),
                    new("AssigneeId", row => row.AssigneeId),
                    new("AcceptorId", row => row.AcceptorId),
                    new("Status", row => row.Status),
                    new("PackageId", row => row.PackageId),
                ],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedContractBid, EconomyContractBid>(
                "WorldContractBid",
                row => new(row.Id),
                row => new(row.Id, row.ContractId, row.BidderId, row.AmountCents, row.Time, row.Dismissed),
                state => new SavedContractBid
                {
                    Id = state.Id,
                    ContractId = state.ContractId,
                    BidderId = state.BidderId,
                    AmountCents = state.AmountCents,
                    Time = state.Time,
                    Dismissed = state.Dismissed,
                },
                ProjectSavedContractBid,
                "Id,ContractId,BidderId,AmountCents,Time,Dismissed",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetBoolean(6)
                    ),
                state => new(state.Id),
                indexes: [new("ContractId", row => row.ContractId), new("BidderId", row => row.BidderId)],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedContractChange, EconomyContractChange>(
                "WorldContractChange",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.ContractId, row.Kind, row.InventoryJson, row.BalanceCents),
                state => new SavedContractChange
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    ContractId = state.ContractId,
                    Kind = state.Kind,
                    InventoryJson = state.InventoryJson,
                    BalanceCents = state.BalanceCents,
                },
                ProjectSavedContractChange,
                "Id,PilotId,ContractId,Kind,InventoryJson,BalanceCents",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        (ContractChangeKind)reader.GetInt32(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId), new("ContractId", row => row.ContractId)],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedContractItem, EconomyContractItem>(
                "WorldContractItem",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.BlueprintRuns,
                        row.MaterialEfficiency,
                        row.TimeEfficiency,
                        row.ContractId,
                        row.ItemId,
                        row.TypeId,
                        row.Quantity,
                        row.Offered,
                        row.ParentId,
                        row.FlagId,
                        row.Singleton,
                        row.Damage,
                        row.GroupId,
                        row.CategoryId
                    ),
                state => new SavedContractItem
                {
                    Id = state.Id,
                    BlueprintRuns = state.BlueprintRuns,
                    MaterialEfficiency = state.MaterialEfficiency,
                    TimeEfficiency = state.TimeEfficiency,
                    ContractId = state.ContractId,
                    ItemId = state.ItemId,
                    TypeId = state.TypeId,
                    Quantity = state.Quantity,
                    Offered = state.Offered,
                    ParentId = state.ParentId,
                    FlagId = state.FlagId,
                    Singleton = state.Singleton,
                    Damage = state.Damage,
                    GroupId = state.GroupId,
                    CategoryId = state.CategoryId,
                },
                ProjectSavedContractItem,
                "Id,BlueprintRuns,MaterialEfficiency,TimeEfficiency,ContractId,ItemId,TypeId,Quantity,"
                    + "Offered,ParentId,FlagId,Singleton,Damage,GroupId,CategoryId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetBoolean(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.GetInt32(11),
                        reader.GetBoolean(12),
                        reader.GetDouble(13),
                        reader.GetInt32(14),
                        reader.GetInt32(15)
                    ),
                state => new(state.Id),
                indexes: [new("ContractId", row => row.ContractId), new("TypeId", row => row.TypeId)],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedContractPayment, EconomyContractPayment>(
                "WorldContractPayment",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.CounterpartyId, row.ContractId, row.Type, row.AmountCents, row.BalanceCents, row.Time),
                state => new SavedContractPayment
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    CounterpartyId = state.CounterpartyId,
                    ContractId = state.ContractId,
                    Type = state.Type,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    Time = state.Time,
                },
                ProjectSavedContractPayment,
                "Id,PilotId,CounterpartyId,ContractId,Type,AmountCents,BalanceCents,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        (WalletEntryType)reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId), new("ContractId", row => row.ContractId)],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedContract(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyContract>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string ids = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldContract WHERE Id IN (SELECT value FROM json_each({ids}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldContract(
                    Id,BuyoutCents,CurrentBidCents,AuctionAccountingLevel,IssuerCollected,WinnerCollected,Type,EndStationId,EndSystemId,
                    EndConstellationId,EndRegionId,DurationDays,CollateralCents,PackageId,
                    IssuerId,IssuerCorporationId,AssigneeId,AcceptorId,
                    Status,StationId,SystemId,ConstellationId,RegionId,Issued,Expires,Accepted,Completed,
                    PriceCents,RewardCents,Title,Description,Volume,BrokerCents,DepositCents,SalesTaxCents,Reclaimed,
                    Dismissed)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.BuyoutCents'),json_extract(value,'$.CurrentBidCents'),
                    json_extract(value,'$.AuctionAccountingLevel'),json_extract(value,
                    '$.IssuerCollected'),json_extract(value,'$.WinnerCollected'),
                    json_extract(value,'$.Type'),json_extract(value,'$.EndStationId'),json_extract(value,'$.EndSystemId'),
                    json_extract(value,'$.EndConstellationId'),json_extract(value,'$.EndRegionId'),json_extract(value,'$.DurationDays'),
                    json_extract(value,'$.CollateralCents'),json_extract(value,'$.PackageId'),json_extract(value,'$.IssuerId'),
                    json_extract(value,'$.IssuerCorporationId'),json_extract(value,'$.AssigneeId'),json_extract(value,'$.AcceptorId'),
                    json_extract(value,'$.Status'),json_extract(value,'$.StationId'),json_extract(value,'$.SystemId'),
                    json_extract(value,'$.ConstellationId'),json_extract(value,'$.RegionId'),json_extract(value,'$.Issued'),
                    json_extract(value,'$.Expires'),json_extract(value,'$.Accepted'),json_extract(value,'$.Completed'),
                    json_extract(value,'$.PriceCents'),json_extract(value,'$.RewardCents'),json_extract(value,'$.Title'),
                    json_extract(value,'$.Description'),json_extract(value,'$.Volume'),json_extract(value,'$.BrokerCents'),
                    json_extract(value,'$.DepositCents'),json_extract(value,'$.SalesTaxCents'),json_extract(value,'$.Reclaimed'),
                    json_extract(value,'$.Dismissed')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET CurrentBidCents=excluded.CurrentBidCents,IssuerCollected=excluded.IssuerCollected,
                WinnerCollected=excluded.WinnerCollected,
                    AcceptorId=excluded.AcceptorId,Status=excluded.Status,Accepted=excluded.Accepted,
                    Completed=excluded.Completed,Volume=excluded.Volume,PackageId=excluded.PackageId,
                    Reclaimed=excluded.Reclaimed,Dismissed=excluded.Dismissed
                """
            );
        }
    }

    private static void ProjectSavedContractBid(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyContractBid>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string ids = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldContractBid WHERE Id IN (SELECT value FROM json_each({ids}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldContractBid(
                    Id,ContractId,BidderId,AmountCents,Time,Dismissed)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.ContractId'),json_extract(value,'$.BidderId'),
                    json_extract(value,'$.AmountCents'),json_extract(value,'$.Time'),json_extract(value,'$.Dismissed')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET Dismissed=excluded.Dismissed
                """
            );
        }
    }

    private static void ProjectSavedContractChange(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyContractChange>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string ids = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldContractChange WHERE Id IN (SELECT value FROM json_each({ids}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldContractChange(
                    Id,PilotId,ContractId,Kind,InventoryJson,BalanceCents)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.ContractId'),
                    json_extract(value,'$.Kind'),json_extract(value,'$.InventoryJson'),json_extract(value,'$.BalanceCents')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO NOTHING
                """
            );
        }
    }

    private static void ProjectSavedContractItem(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyContractItem>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string ids = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldContractItem WHERE Id IN (SELECT value FROM json_each({ids}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldContractItem(
                    Id,BlueprintRuns,MaterialEfficiency,TimeEfficiency,ContractId,ItemId,TypeId,Quantity,Offered,
                    ParentId,FlagId,Singleton,Damage,GroupId,CategoryId)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.BlueprintRuns'),json_extract(value,'$.MaterialEfficiency'),
                    json_extract(value,'$.TimeEfficiency'),json_extract(value,'$.ContractId'),json_extract(value,'$.ItemId'),
                    json_extract(value,'$.TypeId'),json_extract(value,'$.Quantity'),json_extract(value,'$.Offered'),
                    json_extract(value,'$.ParentId'),json_extract(value,'$.FlagId'),json_extract(value,'$.Singleton'),
                    json_extract(value,'$.Damage'),json_extract(value,'$.GroupId'),json_extract(value,'$.CategoryId')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO NOTHING
                """
            );
        }
    }

    private static void ProjectSavedContractPayment(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyContractPayment>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string ids = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldContractPayment WHERE Id IN (SELECT value FROM json_each({ids}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldContractPayment(
                    Id,PilotId,CounterpartyId,ContractId,Type,AmountCents,BalanceCents,Time)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.CounterpartyId'),
                    json_extract(value,'$.ContractId'),json_extract(value,'$.Type'),json_extract(value,'$.AmountCents'),
                    json_extract(value,'$.BalanceCents'),json_extract(value,'$.Time')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO NOTHING
                """
            );
        }
    }
}
