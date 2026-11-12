using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Docking;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyInventoryOwner(long Id);

internal sealed partial class ResidentEconomy
{
    private static void ProjectInventoryOwner(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyInventoryOwner>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldInventoryOwner
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldInventoryOwner.Id=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldInventoryOwner(Id)
                    SELECT json_extract(value,'$.Id') FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO NOTHING
                    """
                );
            }
        }
    }
}

internal sealed record EconomyAccount(string UserName, long CreatedAt, long Id, AccountCloneGrade CloneGrade);

internal sealed partial class ResidentEconomy
{
    private static void ProjectAccount(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAccount>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is not { } row)
            {
                world.Database.ExecuteSql($"DELETE FROM WorldAccount WHERE Id={change.Key.Id}");
                continue;
            }
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAccount(Id,UserName,CreatedAt,CloneGrade) VALUES({row.Id},{row.UserName},{row.CreatedAt},{row.CloneGrade})
                ON CONFLICT(Id) DO UPDATE SET UserName=excluded.UserName,CreatedAt=excluded.CreatedAt,CloneGrade=excluded.CloneGrade
                """
            );
        }
    }
}

internal sealed record EconomyPilot(
    long Id,
    long AccountId,
    string Name,
    int CorporationId,
    int RaceId,
    int BloodlineId,
    int GenderId,
    int AncestryId,
    int SchoolId,
    long CreatedAt,
    byte[]? Appearance,
    byte[]? Portrait,
    long ActiveShipId,
    ShipSafetyLevel SafetyLevel
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectPilot(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyPilot>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is not { } row)
            {
                world.Database.ExecuteSql($"DELETE FROM WorldPilot WHERE Id={change.Key.Id}");
                continue;
            }
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,RaceId,BloodlineId,GenderId,AncestryId,SchoolId,
                    CreatedAt,Appearance,Portrait,ActiveShipId,SafetyLevel)
                VALUES({row.Id},{row.AccountId},{row.Name},{row.CorporationId},{row.RaceId},{row.BloodlineId},{row.GenderId},
                    {row.AncestryId},{row.SchoolId},{row.CreatedAt},{row.Appearance},{row.Portrait},{row.ActiveShipId},{row.SafetyLevel})
                ON CONFLICT(Id) DO UPDATE SET AccountId=excluded.AccountId,Name=excluded.Name,CorporationId=excluded.CorporationId,
                    RaceId=excluded.RaceId,BloodlineId=excluded.BloodlineId,GenderId=excluded.GenderId,AncestryId=excluded.AncestryId,
                    SchoolId=excluded.SchoolId,CreatedAt=excluded.CreatedAt,Appearance=excluded.Appearance,Portrait=excluded.Portrait,
                    ActiveShipId=excluded.ActiveShipId,SafetyLevel=excluded.SafetyLevel
                """
            );
        }
    }
}

internal sealed record EconomyShip(
    long Id,
    long OwnerId,
    int TypeId,
    int StationId,
    long? StructureId,
    string Name,
    long? StoredInId,
    int StorageFlagId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectShip(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyShip>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldShip WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldShip.Id=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,StructureId,Name,StoredInId,StorageFlagId)
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.OwnerId'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.StationId'),json_extract(value,'$.StructureId'),json_extract(value,'$.Name'),
                        json_extract(value,'$.StoredInId'),json_extract(value,'$.StorageFlagId') FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET OwnerId=excluded.OwnerId,TypeId=excluded.TypeId,
                        StationId=excluded.StationId,StructureId=excluded.StructureId,Name=excluded.Name,
                        StoredInId=excluded.StoredInId,StorageFlagId=excluded.StorageFlagId
                    """
                );
            }
        }
    }
}

internal sealed record EconomyFlightPosition(long ShipId, int? SystemId, double X, double Y, double Z);

internal sealed partial class ResidentEconomy
{
    private static void ProjectFlightPosition(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyFlightPosition>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldPosition
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldPosition.ShipId=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPosition(ShipId,SystemId,X,Y,Z)
                    SELECT json_extract(value,'$.ShipId'),json_extract(value,'$.SystemId'),json_extract(value,'$.X'),
                        json_extract(value,'$.Y'),json_extract(value,'$.Z') FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET SystemId=excluded.SystemId,X=excluded.X,Y=excluded.Y,Z=excluded.Z
                    """
                );
            }
        }
    }
}

internal sealed record EconomyOwnedItem(
    int MaterialEfficiency,
    int TimeEfficiency,
    int BlueprintRuns,
    double? VolumeOverride,
    double CrystalDamage,
    bool Singleton,
    bool Online,
    long Id,
    long OwnerId,
    int TypeId,
    long LocationId,
    int FlagId,
    int Quantity
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectOwnedItem(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyOwnedItem>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldItem WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldItem.Id=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldItem(MaterialEfficiency,TimeEfficiency,BlueprintRuns,VolumeOverride,CrystalDamage,
                        Singleton,Online,Id,OwnerId,TypeId,LocationId,FlagId,Quantity)
                    SELECT json_extract(value,'$.MaterialEfficiency'),json_extract(value,'$.TimeEfficiency'),
                        json_extract(value,'$.BlueprintRuns'),json_extract(value,'$.VolumeOverride'),json_extract(value,
                        '$.CrystalDamage'),json_extract(value,'$.Singleton'),json_extract(value,'$.Online'),
                        json_extract(value,'$.Id'),json_extract(value,'$.OwnerId'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.LocationId'),json_extract(value,'$.FlagId'),json_extract(value,
                        '$.Quantity') FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET MaterialEfficiency=excluded.MaterialEfficiency,
                        TimeEfficiency=excluded.TimeEfficiency,BlueprintRuns=excluded.BlueprintRuns,
                        VolumeOverride=excluded.VolumeOverride,CrystalDamage=excluded.CrystalDamage,
                        Singleton=excluded.Singleton,Online=excluded.Online,OwnerId=excluded.OwnerId,TypeId=excluded.TypeId,
                        LocationId=excluded.LocationId,FlagId=excluded.FlagId,Quantity=excluded.Quantity
                    """
                );
            }
        }
    }
}

internal sealed record EconomyPilotWallet(long PilotId, long Cents);

internal sealed partial class ResidentEconomy
{
    private static void ProjectPilotWallet(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyPilotWallet>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldWallet
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldWallet.PilotId=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldWallet(PilotId,Cents)
                    SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.Cents') FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET Cents=excluded.Cents
                    """
                );
            }
        }
    }
}

internal sealed record EconomyMarketStation(int StationId, long CreatedAt, int SeedVersion);

internal sealed partial class ResidentEconomy
{
    private static void ProjectMarketStation(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMarketStation>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMarketStation
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldMarketStation.StationId=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldMarketStation(StationId,CreatedAt,SeedVersion)
                    SELECT json_extract(value,'$.StationId'),json_extract(value,'$.CreatedAt'),json_extract(value,
                        '$.SeedVersion') FROM json_each({values}) WHERE 1
                    ON CONFLICT(StationId) DO UPDATE SET CreatedAt=excluded.CreatedAt,SeedVersion=excluded.SeedVersion
                    """
                );
            }
        }
    }
}

internal readonly record struct NpcMarketAssortment(int StationId, int TypeId, bool Bid, NpcMarketKind Kind)
{
    internal static NpcMarketAssortment For(NpcMarketOrder order) => new(order.StationId, order.TypeId, order.Bid, order.Kind);
}

internal sealed record EconomyNpcMarketOrder(
    long Id,
    int StationId,
    int SystemId,
    int RegionId,
    long OwnerId,
    int TypeId,
    bool Bid,
    long PriceCents,
    int Remaining,
    int Entered,
    long Issued,
    Eden.Gameplay.Market.NpcMarketKind Kind
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectNpcMarketOrder(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyNpcMarketOrder>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMarketOrder
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldMarketOrder.Id=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldMarketOrder(Id,StationId,SystemId,RegionId,OwnerId,TypeId,Bid,PriceCents,Remaining,
                        Entered,Issued,Kind)
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.StationId'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.RegionId'),json_extract(value,'$.OwnerId'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.Bid'),json_extract(value,'$.PriceCents'),json_extract(value,'$.Remaining'),
                        json_extract(value,'$.Entered'),json_extract(value,'$.Issued'),json_extract(value,
                        '$.Kind') FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET StationId=excluded.StationId,SystemId=excluded.SystemId,
                        RegionId=excluded.RegionId,OwnerId=excluded.OwnerId,TypeId=excluded.TypeId,Bid=excluded.Bid,
                        PriceCents=excluded.PriceCents,Remaining=excluded.Remaining,Entered=excluded.Entered,
                        Issued=excluded.Issued,Kind=excluded.Kind
                    """
                );
            }
        }
    }
}

internal sealed record EconomySavedPersonalOrder(
    long Id,
    long PilotId,
    int StationId,
    int SystemId,
    int RegionId,
    int TypeId,
    bool Bid,
    long PriceCents,
    int Remaining,
    int Entered,
    long Issued,
    long Expires,
    long Modified,
    int DurationDays,
    long EscrowCents,
    PersonalOrderStatus Status
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedPersonalOrder(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedPersonalOrder>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldPersonalOrder
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldPersonalOrder.Id=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPersonalOrder(Id,PilotId,StationId,SystemId,RegionId,TypeId,Bid,PriceCents,Remaining,
                        Entered,Issued,Expires,Modified,DurationDays,EscrowCents,Status)
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.StationId'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.RegionId'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.Bid'),json_extract(value,'$.PriceCents'),json_extract(value,'$.Remaining'),
                        json_extract(value,'$.Entered'),json_extract(value,'$.Issued'),json_extract(value,'$.Expires'),
                        json_extract(value,'$.Modified'),json_extract(value,'$.DurationDays'),json_extract(value,
                        '$.EscrowCents'),json_extract(value,'$.Status') FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET PilotId=excluded.PilotId,StationId=excluded.StationId,
                        SystemId=excluded.SystemId,RegionId=excluded.RegionId,TypeId=excluded.TypeId,Bid=excluded.Bid,
                        PriceCents=excluded.PriceCents,Remaining=excluded.Remaining,Entered=excluded.Entered,
                        Issued=excluded.Issued,Expires=excluded.Expires,Modified=excluded.Modified,
                        DurationDays=excluded.DurationDays,EscrowCents=excluded.EscrowCents,Status=excluded.Status
                    """
                );
            }
        }
    }
}

internal sealed record EconomySavedRegionalMarketStock(long OrderId, long AnchorCents, long RefreshedTicks, int DemandVersion);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedRegionalMarketStock(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedRegionalMarketStock>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldRegionalMarketStock
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldRegionalMarketStock.OrderId=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldRegionalMarketStock(OrderId,AnchorCents,RefreshedTicks,DemandVersion)
                    SELECT json_extract(value,'$.OrderId'),json_extract(value,'$.AnchorCents'),json_extract(value,
                        '$.RefreshedTicks'),json_extract(value,'$.DemandVersion') FROM json_each({values}) WHERE 1
                    ON CONFLICT(OrderId) DO UPDATE SET AnchorCents=excluded.AnchorCents,
                        RefreshedTicks=excluded.RefreshedTicks,DemandVersion=excluded.DemandVersion
                    """
                );
            }
        }
    }
}

internal sealed record EconomyMarketExecution(
    long Id,
    long PilotId,
    long OrderId,
    int StationId,
    long CounterpartyId,
    int TypeId,
    bool IsBuy,
    int Quantity,
    long PriceCents,
    long TaxCents,
    long BalanceCents,
    long Time,
    long? WalletAmountCents
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectMarketExecution(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMarketExecution>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMarketTrade
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldMarketTrade.Id=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldMarketTrade(Id,PilotId,OrderId,StationId,CounterpartyId,TypeId,IsBuy,Quantity,
                        PriceCents,TaxCents,BalanceCents,Time,WalletAmountCents)
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.OrderId'),
                        json_extract(value,'$.StationId'),json_extract(value,'$.CounterpartyId'),json_extract(value,
                        '$.TypeId'),json_extract(value,'$.IsBuy'),json_extract(value,'$.Quantity'),json_extract(value,
                        '$.PriceCents'),json_extract(value,'$.TaxCents'),json_extract(value,'$.BalanceCents'),
                        json_extract(value,'$.Time'),json_extract(value,
                        '$.WalletAmountCents') FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET PilotId=excluded.PilotId,OrderId=excluded.OrderId,
                        StationId=excluded.StationId,CounterpartyId=excluded.CounterpartyId,TypeId=excluded.TypeId,
                        IsBuy=excluded.IsBuy,Quantity=excluded.Quantity,PriceCents=excluded.PriceCents,
                        TaxCents=excluded.TaxCents,BalanceCents=excluded.BalanceCents,Time=excluded.Time,
                        WalletAmountCents=excluded.WalletAmountCents
                    """
                );
            }
        }
    }
}

internal sealed record EconomySavedMarketPayment(
    long Id,
    long PilotId,
    long CounterpartyId,
    long OrderId,
    WalletEntryType Kind,
    long AmountCents,
    long BalanceCents,
    long Time
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMarketPayment(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMarketPayment>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMarketPayment
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldMarketPayment.Id=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldMarketPayment(Id,PilotId,CounterpartyId,OrderId,Kind,AmountCents,BalanceCents,Time)
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.CounterpartyId'),
                        json_extract(value,'$.OrderId'),json_extract(value,'$.Kind'),json_extract(value,'$.AmountCents'),
                        json_extract(value,'$.BalanceCents'),json_extract(value,'$.Time') FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET PilotId=excluded.PilotId,CounterpartyId=excluded.CounterpartyId,
                        OrderId=excluded.OrderId,Kind=excluded.Kind,AmountCents=excluded.AmountCents,
                        BalanceCents=excluded.BalanceCents,Time=excluded.Time
                    """
                );
            }
        }
    }
}

internal sealed record EconomySavedMarketChange(long Id, long PilotId, string Payload);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMarketChange(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMarketChange>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMarketChange
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldMarketChange.Id=json_extract(value,'$.Id'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldMarketChange(Id,PilotId,Payload)
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,
                        '$.Payload') FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET PilotId=excluded.PilotId,Payload=excluded.Payload
                    """
                );
            }
        }
    }
}

internal sealed record EconomySavedTaskCounter(long PilotId, string Name, long Count);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedTaskCounter(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedTaskCounter>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldTaskCounter
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldTaskCounter.PilotId=json_extract(value,'$.Id')
                        AND WorldTaskCounter.Name=json_extract(value,'$.Name'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldTaskCounter(PilotId,Name,Count)
                    SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.Name'),json_extract(value,
                        '$.Count') FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId,Name) DO UPDATE SET Count=excluded.Count
                    """
                );
            }
        }
    }
}

internal sealed record EconomySavedCompletedTask(long PilotId, long Id, long Time);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCompletedTask(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCompletedTask>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCompletedTask
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE WorldCompletedTask.PilotId=json_extract(value,'$.Id')
                        AND WorldCompletedTask.Id=json_extract(value,'$.Part'))
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldCompletedTask(PilotId,Id,Time)
                    SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.Id'),json_extract(value,
                        '$.Time') FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId,Id) DO UPDATE SET Time=excluded.Time
                    """
                );
            }
        }
    }
}

internal sealed record EconomySavedWorldEconomy(int Id, bool TestingMarket, bool TestingGrants, int InstitutionalStock);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedWorldEconomy(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedWorldEconomy>> changes)
    {
        if (changes.Count != 0)
        {
            throw new InvalidOperationException("Reference economic rows cannot be projected.");
        }
    }
}

internal sealed record EconomySavedMarketUnlock(int TypeId, long AccountId, long PilotId, long JobId, long ClaimedAt);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMarketUnlock(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMarketUnlock>> changes)
    {
        if (changes.Count != 0)
        {
            throw new InvalidOperationException("Reference economic rows cannot be projected.");
        }
    }
}

internal sealed record EconomyPilotSkill(long PilotId, int TypeId, long Points);

internal sealed partial class ResidentEconomy
{
    private static void ProjectPilotSkill(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyPilotSkill>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPilotSkill(PilotId,TypeId,Points) VALUES({row.PilotId},{row.TypeId},{row.Points})
                    ON CONFLICT(PilotId,TypeId) DO UPDATE SET Points=excluded.Points
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldPilotSkill WHERE PilotId={change.Key.Id} AND TypeId={change.Key.Part}");
            }
        }
    }
}

internal sealed record EconomySavedNpcStanding(long PilotId, long SourceId, double Value);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedNpcStanding(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedNpcStanding>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldNpcStanding(PilotId,SourceId,Value) VALUES({row.PilotId},{row.SourceId},{row.Value})
                    ON CONFLICT(PilotId,SourceId) DO UPDATE SET Value=excluded.Value
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldNpcStanding WHERE PilotId={change.Key.Id} AND SourceId={change.Key.Part}");
            }
        }
    }
}

internal sealed record EconomyIndustryJob(
    long? SuspendedAt,
    IndustryActivity Activity,
    int LicensedRuns,
    bool BlueprintCopy,
    int MaterialEfficiency,
    int TimeEfficiency,
    int OutputRuns,
    double Probability,
    int? SuccessfulRuns,
    int? OptionalTypeId,
    long Id,
    long PilotId,
    long BlueprintId,
    int BlueprintTypeId,
    long StationId,
    int ProductTypeId,
    int Runs,
    int OutputQuantity,
    long StartTime,
    long EndTime,
    long DurationSeconds,
    IndustryJobStatus Status,
    long? CompletedAt,
    string MaterialsJson,
    long CostCents
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectIndustryJob(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyIndustryJob>> changes)
    {
        if (changes.Count != 0)
        {
            throw new InvalidOperationException("Reference economic rows cannot be projected.");
        }
    }
}

internal sealed record EconomySavedContractEscrow(long ItemId, long ContractId);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedContractEscrow(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedContractEscrow>> changes)
    {
        if (changes.Count != 0)
        {
            throw new InvalidOperationException("Reference economic rows cannot be projected.");
        }
    }
}

internal sealed partial class ResidentEconomy
{
    private void RegisterTables()
    {
        RegisterBotTables();
        RegisterCloneTrainingTables();
        Register(
            new EconomyTable<InventoryOwner, EconomyInventoryOwner>(
                "WorldInventoryOwner",
                row => new(row.Id),
                row => new(row.Id),
                state => new InventoryOwner { Id = state.Id },
                ProjectInventoryOwner,
                "Id",
                reader => new(reader.GetInt64(1)),
                state => new(state.Id),
                readOnly: false,
                indexes: [],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Account, EconomyAccount>(
                "WorldAccount",
                row => new(row.Id),
                row => new(row.UserName, row.CreatedAt, row.Id, row.CloneGrade),
                state => new Account
                {
                    UserName = state.UserName,
                    CreatedAt = state.CreatedAt,
                    Id = state.Id,
                    CloneGrade = state.CloneGrade,
                },
                ProjectAccount,
                "UserName,CreatedAt,Id,CloneGrade",
                reader => new(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), (AccountCloneGrade)reader.GetInt32(4)),
                state => new(state.Id),
                readOnly: false,
                indexes: [new("UserName", row => row.UserName, new EconomyNameComparer())],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Pilot, EconomyPilot>(
                "WorldPilot",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.AccountId,
                        row.Name,
                        row.CorporationId,
                        row.RaceId,
                        row.BloodlineId,
                        row.GenderId,
                        row.AncestryId,
                        row.SchoolId,
                        row.CreatedAt,
                        row.Appearance?.ToArray(),
                        row.Portrait?.ToArray(),
                        row.ActiveShipId,
                        row.SafetyLevel
                    ),
                state => new Pilot
                {
                    Id = state.Id,
                    AccountId = state.AccountId,
                    Name = state.Name,
                    CorporationId = state.CorporationId,
                    RaceId = state.RaceId,
                    BloodlineId = state.BloodlineId,
                    GenderId = state.GenderId,
                    AncestryId = state.AncestryId,
                    SchoolId = state.SchoolId,
                    CreatedAt = state.CreatedAt,
                    Appearance = state.Appearance?.ToArray(),
                    Portrait = state.Portrait?.ToArray(),
                    ActiveShipId = state.ActiveShipId,
                    SafetyLevel = state.SafetyLevel,
                },
                ProjectPilot,
                "Id,AccountId,Name,CorporationId,RaceId,BloodlineId,GenderId,AncestryId,SchoolId,CreatedAt,Appearance,Portrait,"
                    + "ActiveShipId,SafetyLevel",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetInt64(10),
                        reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11),
                        reader.IsDBNull(12) ? null : reader.GetFieldValue<byte[]>(12),
                        reader.GetInt64(13),
                        (ShipSafetyLevel)reader.GetInt32(14)
                    ),
                state => new(state.Id),
                readOnly: false,
                equal: (left, right) =>
                    left with { Appearance = null, Portrait = null } == right with { Appearance = null, Portrait = null }
                    && (left.Appearance ?? []).SequenceEqual(right.Appearance ?? [])
                    && (left.Portrait ?? []).SequenceEqual(right.Portrait ?? []),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("AccountId", row => row.AccountId),
                    new("Name", row => row.Name, new EconomyNameComparer()),
                    new("ActiveShipId", row => row.ActiveShipId),
                ],
                changed: PilotIdentityChanged,
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<Ship, EconomyShip>(
                "WorldShip",
                row => new(row.Id),
                row => new(row.Id, row.OwnerId, row.TypeId, row.StationId, row.StructureId, row.Name, row.StoredInId, row.StorageFlagId),
                state => new Ship
                {
                    Id = state.Id,
                    OwnerId = state.OwnerId,
                    TypeId = state.TypeId,
                    StationId = state.StationId,
                    StructureId = state.StructureId,
                    Name = state.Name,
                    StoredInId = state.StoredInId,
                    StorageFlagId = state.StorageFlagId,
                },
                ProjectShip,
                "Id,OwnerId,TypeId,StationId,StructureId,Name,StoredInId,StorageFlagId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.GetInt32(8)
                    ),
                state => new(state.Id),
                readOnly: false,
                indexes:
                [
                    new("OwnerId", row => row.OwnerId),
                    new("TypeId", row => row.TypeId),
                    new("StationId", row => row.StationId),
                    new("StoredInId", row => row.StoredInId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<FlightPosition, EconomyFlightPosition>(
                "WorldPosition",
                row => new(row.ShipId),
                row => new(row.ShipId, row.SystemId, row.X, row.Y, row.Z),
                state => new FlightPosition
                {
                    ShipId = state.ShipId,
                    SystemId = state.SystemId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectFlightPosition,
                "ShipId,SystemId,X,Y,Z",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        reader.GetDouble(3),
                        reader.GetDouble(4),
                        reader.GetDouble(5)
                    ),
                state => new(state.ShipId),
                readOnly: false,
                indexes: [new("SystemId", row => row.SystemId)],
                primaryColumn: "ShipId"
            )
        );
        Register(
            new EconomyTable<OwnedItem, EconomyOwnedItem>(
                "WorldItem",
                row => new(row.Id),
                row =>
                    new(
                        row.MaterialEfficiency,
                        row.TimeEfficiency,
                        row.BlueprintRuns,
                        row.VolumeOverride,
                        row.CrystalDamage,
                        row.Singleton,
                        row.Online,
                        row.Id,
                        row.OwnerId,
                        row.TypeId,
                        row.LocationId,
                        row.FlagId,
                        row.Quantity
                    ),
                state => new OwnedItem
                {
                    MaterialEfficiency = state.MaterialEfficiency,
                    TimeEfficiency = state.TimeEfficiency,
                    BlueprintRuns = state.BlueprintRuns,
                    VolumeOverride = state.VolumeOverride,
                    CrystalDamage = state.CrystalDamage,
                    Singleton = state.Singleton,
                    Online = state.Online,
                    Id = state.Id,
                    OwnerId = state.OwnerId,
                    TypeId = state.TypeId,
                    LocationId = state.LocationId,
                    FlagId = state.FlagId,
                    Quantity = state.Quantity,
                },
                ProjectOwnedItem,
                "MaterialEfficiency,TimeEfficiency,BlueprintRuns,VolumeOverride,CrystalDamage,Singleton,Online,Id,OwnerId,TypeId,"
                    + "LocationId,FlagId,Quantity",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetDouble(4),
                        reader.GetDouble(5),
                        reader.GetBoolean(6),
                        reader.GetBoolean(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt32(10),
                        reader.GetInt64(11),
                        reader.GetInt32(12),
                        reader.GetInt32(13)
                    ),
                state => new(state.Id),
                readOnly: false,
                indexes:
                [
                    new("OwnerId", row => row.OwnerId, member: EconomyIndexMember.RowId),
                    new("TypeId", row => row.TypeId, member: EconomyIndexMember.RowId),
                    new("LocationId", row => row.LocationId, member: EconomyIndexMember.RowId),
                ],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<PilotWallet, EconomyPilotWallet>(
                "WorldWallet",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Cents),
                state => new PilotWallet { PilotId = state.PilotId, Cents = state.Cents },
                ProjectPilotWallet,
                "PilotId,Cents",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId),
                readOnly: false,
                indexes: [],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<MarketStation, EconomyMarketStation>(
                "WorldMarketStation",
                row => new(row.StationId),
                row => new(row.StationId, row.CreatedAt, row.SeedVersion),
                state => new MarketStation
                {
                    StationId = state.StationId,
                    CreatedAt = state.CreatedAt,
                    SeedVersion = state.SeedVersion,
                },
                ProjectMarketStation,
                "StationId,CreatedAt,SeedVersion",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3)),
                state => new(state.StationId),
                readOnly: false,
                indexes: [],
                primaryColumn: "StationId"
            )
        );
        Register(
            new EconomyTable<NpcMarketOrder, EconomyNpcMarketOrder>(
                "WorldMarketOrder",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.StationId,
                        row.SystemId,
                        row.RegionId,
                        row.OwnerId,
                        row.TypeId,
                        row.Bid,
                        row.PriceCents,
                        row.Remaining,
                        row.Entered,
                        row.Issued,
                        row.Kind
                    ),
                state => new NpcMarketOrder
                {
                    Id = state.Id,
                    StationId = state.StationId,
                    SystemId = state.SystemId,
                    RegionId = state.RegionId,
                    OwnerId = state.OwnerId,
                    TypeId = state.TypeId,
                    Bid = state.Bid,
                    PriceCents = state.PriceCents,
                    Remaining = state.Remaining,
                    Entered = state.Entered,
                    Issued = state.Issued,
                    Kind = state.Kind,
                },
                ProjectNpcMarketOrder,
                "Id,StationId,SystemId,RegionId,OwnerId,TypeId,Bid,PriceCents,Remaining,Entered,Issued,Kind",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetBoolean(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10),
                        reader.GetInt64(11),
                        (Eden.Gameplay.Market.NpcMarketKind)reader.GetInt32(12)
                    ),
                state => new(state.Id),
                readOnly: false,
                indexes:
                [
                    new("StationId", row => row.StationId, member: EconomyIndexMember.RowId),
                    new("RegionId", row => row.RegionId, member: EconomyIndexMember.RowId),
                    new("OwnerId", row => row.OwnerId, member: EconomyIndexMember.RowId),
                    new("TypeId", row => row.TypeId, member: EconomyIndexMember.RowId),
                    new(nameof(NpcMarketAssortment), row => new NpcMarketAssortment(row.StationId, row.TypeId, row.Bid, row.Kind)),
                ],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedPersonalOrder, EconomySavedPersonalOrder>(
                "WorldPersonalOrder",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.PilotId,
                        row.StationId,
                        row.SystemId,
                        row.RegionId,
                        row.TypeId,
                        row.Bid,
                        row.PriceCents,
                        row.Remaining,
                        row.Entered,
                        row.Issued,
                        row.Expires,
                        row.Modified,
                        row.DurationDays,
                        row.EscrowCents,
                        row.Status
                    ),
                state => new SavedPersonalOrder
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    StationId = state.StationId,
                    SystemId = state.SystemId,
                    RegionId = state.RegionId,
                    TypeId = state.TypeId,
                    Bid = state.Bid,
                    PriceCents = state.PriceCents,
                    Remaining = state.Remaining,
                    Entered = state.Entered,
                    Issued = state.Issued,
                    Expires = state.Expires,
                    Modified = state.Modified,
                    DurationDays = state.DurationDays,
                    EscrowCents = state.EscrowCents,
                    Status = state.Status,
                },
                ProjectSavedPersonalOrder,
                "Id,PilotId,StationId,SystemId,RegionId,TypeId,Bid,PriceCents,Remaining,Entered,Issued,Expires,Modified,DurationDays,"
                    + "EscrowCents,Status",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetBoolean(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12),
                        reader.GetInt64(13),
                        reader.GetInt32(14),
                        reader.GetInt64(15),
                        (PersonalOrderStatus)reader.GetInt32(16)
                    ),
                state => new(state.Id),
                readOnly: false,
                indexes:
                [
                    new("PilotId", row => row.PilotId),
                    new("StationId", row => row.StationId),
                    new("RegionId", row => row.RegionId),
                    new("TypeId", row => row.TypeId),
                    new("Status", row => row.Status),
                ],
                changed: OrderChanged,
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedRegionalMarketStock, EconomySavedRegionalMarketStock>(
                "WorldRegionalMarketStock",
                row => new(row.OrderId),
                row => new(row.OrderId, row.AnchorCents, row.RefreshedTicks, row.DemandVersion),
                state => new SavedRegionalMarketStock
                {
                    OrderId = state.OrderId,
                    AnchorCents = state.AnchorCents,
                    RefreshedTicks = state.RefreshedTicks,
                    DemandVersion = state.DemandVersion,
                },
                ProjectSavedRegionalMarketStock,
                "OrderId,AnchorCents,RefreshedTicks,DemandVersion",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4)),
                state => new(state.OrderId),
                readOnly: false,
                indexes: [],
                primaryColumn: "OrderId"
            )
        );
        Register(
            new EconomyTable<MarketExecution, EconomyMarketExecution>(
                "WorldMarketTrade",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.PilotId,
                        row.OrderId,
                        row.StationId,
                        row.CounterpartyId,
                        row.TypeId,
                        row.IsBuy,
                        row.Quantity,
                        row.PriceCents,
                        row.TaxCents,
                        row.BalanceCents,
                        row.Time,
                        row.WalletAmountCents
                    ),
                state => new MarketExecution
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    OrderId = state.OrderId,
                    StationId = state.StationId,
                    CounterpartyId = state.CounterpartyId,
                    TypeId = state.TypeId,
                    IsBuy = state.IsBuy,
                    Quantity = state.Quantity,
                    PriceCents = state.PriceCents,
                    TaxCents = state.TaxCents,
                    BalanceCents = state.BalanceCents,
                    Time = state.Time,
                    WalletAmountCents = state.WalletAmountCents,
                },
                ProjectMarketExecution,
                "Id,PilotId,OrderId,StationId,CounterpartyId,TypeId,IsBuy,Quantity,PriceCents,TaxCents,BalanceCents,Time,WalletAmountCents",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetBoolean(7),
                        reader.GetInt32(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12),
                        reader.IsDBNull(13) ? null : reader.GetInt64(13)
                    ),
                state => new(state.Id),
                readOnly: false,
                indexes:
                [
                    new("PilotId", row => row.PilotId),
                    new("OrderId", row => row.OrderId),
                    new("StationId", row => row.StationId),
                    new("TypeId", row => row.TypeId),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedMarketPayment, EconomySavedMarketPayment>(
                "WorldMarketPayment",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.CounterpartyId, row.OrderId, row.Kind, row.AmountCents, row.BalanceCents, row.Time),
                state => new SavedMarketPayment
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    CounterpartyId = state.CounterpartyId,
                    OrderId = state.OrderId,
                    Kind = state.Kind,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    Time = state.Time,
                },
                ProjectSavedMarketPayment,
                "Id,PilotId,CounterpartyId,OrderId,Kind,AmountCents,BalanceCents,Time",
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
                readOnly: false,
                indexes: [new("Id", row => row.Id), new("PilotId", row => row.PilotId), new("OrderId", row => row.OrderId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedMarketChange, EconomySavedMarketChange>(
                "WorldMarketChange",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.Payload),
                state => new SavedMarketChange
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    Payload = state.Payload,
                },
                ProjectSavedMarketChange,
                "Id,PilotId,Payload",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)),
                state => new(state.Id),
                readOnly: false,
                indexes: [new("Id", row => row.Id), new("PilotId", row => row.PilotId)],
                changed: MarketChangeChanged,
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedTaskCounter, EconomySavedTaskCounter>(
                "WorldTaskCounter",
                row => new(row.PilotId, Name: row.Name),
                row => new(row.PilotId, row.Name, row.Count),
                state => new SavedTaskCounter
                {
                    PilotId = state.PilotId,
                    Name = state.Name,
                    Count = state.Count,
                },
                ProjectSavedTaskCounter,
                "PilotId,Name,Count",
                reader => new(reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3)),
                state => new(state.PilotId, Name: state.Name),
                readOnly: false,
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedCompletedTask, EconomySavedCompletedTask>(
                "WorldCompletedTask",
                row => new(row.PilotId, row.Id),
                row => new(row.PilotId, row.Id, row.Time),
                state => new SavedCompletedTask
                {
                    PilotId = state.PilotId,
                    Id = state.Id,
                    Time = state.Time,
                },
                ProjectSavedCompletedTask,
                "PilotId,Id,Time",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.PilotId, state.Id),
                readOnly: false,
                indexes: [new("PilotId", row => row.PilotId), new("Id", row => row.Id)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedWorldEconomy, EconomySavedWorldEconomy>(
                "WorldEconomy",
                row => new(row.Id),
                row => new(row.Id, row.TestingMarket, row.TestingGrants, row.InstitutionalStock),
                state => new SavedWorldEconomy
                {
                    Id = state.Id,
                    TestingMarket = state.TestingMarket,
                    TestingGrants = state.TestingGrants,
                    InstitutionalStock = state.InstitutionalStock,
                },
                ProjectSavedWorldEconomy,
                "Id,TestingMarket,TestingGrants,InstitutionalStock",
                reader => new(reader.GetInt32(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetInt32(4)),
                state => new(state.Id),
                readOnly: true,
                indexes: [],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedMarketUnlock, EconomySavedMarketUnlock>(
                "WorldMarketUnlock",
                row => new(row.TypeId),
                row => new(row.TypeId, row.AccountId, row.PilotId, row.JobId, row.ClaimedAt),
                state => new SavedMarketUnlock
                {
                    TypeId = state.TypeId,
                    AccountId = state.AccountId,
                    PilotId = state.PilotId,
                    JobId = state.JobId,
                    ClaimedAt = state.ClaimedAt,
                },
                ProjectSavedMarketUnlock,
                "TypeId,AccountId,PilotId,JobId,ClaimedAt",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)),
                state => new(state.TypeId),
                readOnly: true,
                indexes: [new("TypeId", row => row.TypeId), new("PilotId", row => row.PilotId)],
                primaryColumn: "TypeId"
            )
        );
        Register(
            new EconomyTable<PilotSkill, EconomyPilotSkill>(
                "WorldPilotSkill",
                row => new(row.PilotId, row.TypeId),
                row => new(row.PilotId, row.TypeId, row.Points),
                state => new PilotSkill
                {
                    PilotId = state.PilotId,
                    TypeId = state.TypeId,
                    Points = state.Points,
                },
                ProjectPilotSkill,
                "PilotId,TypeId,Points",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3)),
                state => new(state.PilotId, state.TypeId),
                readOnly: false,
                indexes: [new("PilotId", row => row.PilotId, member: EconomyIndexMember.SkillTypeId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedNpcStanding, EconomySavedNpcStanding>(
                "WorldNpcStanding",
                row => new(row.PilotId, row.SourceId),
                row => new(row.PilotId, row.SourceId, row.Value),
                state => new SavedNpcStanding
                {
                    PilotId = state.PilotId,
                    SourceId = state.SourceId,
                    Value = state.Value,
                },
                ProjectSavedNpcStanding,
                "PilotId,SourceId,Value",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3)),
                state => new(state.PilotId, state.SourceId),
                readOnly: false,
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<IndustryJob, EconomyIndustryJob>(
                "WorldIndustryJob",
                row => new(row.Id),
                row =>
                    new(
                        row.SuspendedAt,
                        row.Activity,
                        row.LicensedRuns,
                        row.BlueprintCopy,
                        row.MaterialEfficiency,
                        row.TimeEfficiency,
                        row.OutputRuns,
                        row.Probability,
                        row.SuccessfulRuns,
                        row.OptionalTypeId,
                        row.Id,
                        row.PilotId,
                        row.BlueprintId,
                        row.BlueprintTypeId,
                        row.StationId,
                        row.ProductTypeId,
                        row.Runs,
                        row.OutputQuantity,
                        row.StartTime,
                        row.EndTime,
                        row.DurationSeconds,
                        row.Status,
                        row.CompletedAt,
                        row.MaterialsJson,
                        row.CostCents
                    ),
                state => new IndustryJob
                {
                    SuspendedAt = state.SuspendedAt,
                    Activity = state.Activity,
                    LicensedRuns = state.LicensedRuns,
                    BlueprintCopy = state.BlueprintCopy,
                    MaterialEfficiency = state.MaterialEfficiency,
                    TimeEfficiency = state.TimeEfficiency,
                    OutputRuns = state.OutputRuns,
                    Probability = state.Probability,
                    SuccessfulRuns = state.SuccessfulRuns,
                    OptionalTypeId = state.OptionalTypeId,
                    Id = state.Id,
                    PilotId = state.PilotId,
                    BlueprintId = state.BlueprintId,
                    BlueprintTypeId = state.BlueprintTypeId,
                    StationId = state.StationId,
                    ProductTypeId = state.ProductTypeId,
                    Runs = state.Runs,
                    OutputQuantity = state.OutputQuantity,
                    StartTime = state.StartTime,
                    EndTime = state.EndTime,
                    DurationSeconds = state.DurationSeconds,
                    Status = state.Status,
                    CompletedAt = state.CompletedAt,
                    MaterialsJson = state.MaterialsJson,
                    CostCents = state.CostCents,
                },
                ProjectIndustryJob,
                "SuspendedAt,Activity,LicensedRuns,BlueprintCopy,MaterialEfficiency,TimeEfficiency,OutputRuns,Probability,"
                    + "SuccessfulRuns,OptionalTypeId,Id,PilotId,BlueprintId,BlueprintTypeId,StationId,ProductTypeId,Runs,OutputQuantity,"
                    + "StartTime,EndTime,DurationSeconds,Status,CompletedAt,MaterialsJson,CostCents",
                reader =>
                    new(
                        reader.IsDBNull(1) ? null : reader.GetInt64(1),
                        (IndustryActivity)reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetBoolean(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetDouble(8),
                        reader.IsDBNull(9) ? null : reader.GetInt32(9),
                        reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        reader.GetInt64(11),
                        reader.GetInt64(12),
                        reader.GetInt64(13),
                        reader.GetInt32(14),
                        reader.GetInt64(15),
                        reader.GetInt32(16),
                        reader.GetInt32(17),
                        reader.GetInt32(18),
                        reader.GetInt64(19),
                        reader.GetInt64(20),
                        reader.GetInt64(21),
                        (IndustryJobStatus)reader.GetInt32(22),
                        reader.IsDBNull(23) ? null : reader.GetInt64(23),
                        reader.GetString(24),
                        reader.GetInt64(25)
                    ),
                state => new(state.Id),
                readOnly: true,
                indexes:
                [
                    new("PilotId", row => row.PilotId),
                    new("BlueprintId", row => row.BlueprintId),
                    new("StationId", row => row.StationId),
                    new("Status", row => row.Status),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedContractEscrow, EconomySavedContractEscrow>(
                "WorldContractEscrow",
                row => new(row.ItemId),
                row => new(row.ItemId, row.ContractId),
                state => new SavedContractEscrow { ItemId = state.ItemId, ContractId = state.ContractId },
                ProjectSavedContractEscrow,
                "ItemId,ContractId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.ItemId),
                readOnly: true,
                indexes: [],
                primaryColumn: "ItemId"
            )
        );
    }
}

internal sealed partial class EconomyTransaction
{
    internal void AssignIdentity<TRow>(TRow row)
        where TRow : class
    {
        AssignBotIdentity(row);
        switch (row)
        {
            case OwnedItem value:
                Reserve("WorldItem", value.Id);
                break;
            case Ship value:
                Reserve("WorldShip", value.Id);
                break;
            case NpcMarketOrder value:
                value.Id = value.Id == 0 ? Next("WorldMarketOrder") : value.Id;
                Reserve("WorldMarketOrder", value.Id);
                break;
            case SavedPersonalOrder value:
                value.Id = value.Id == 0 ? Next("WorldPersonalOrder", PersonalMarketRules.FirstOrderId) : value.Id;
                Reserve("WorldPersonalOrder", value.Id);
                break;
            case MarketExecution value:
                value.Id = value.Id == 0 ? Next("WorldMarketTrade") : value.Id;
                Reserve("WorldMarketTrade", value.Id);
                break;
            case SavedMarketPayment value:
                value.Id = value.Id == 0 ? Next("WorldMarketPayment") : value.Id;
                Reserve("WorldMarketPayment", value.Id);
                break;
            case SavedMarketChange value:
                value.Id = value.Id == 0 ? Next("WorldMarketChange") : value.Id;
                Reserve("WorldMarketChange", value.Id);
                break;
        }
    }
}
