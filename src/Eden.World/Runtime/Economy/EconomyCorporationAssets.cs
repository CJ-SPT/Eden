using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyCorporationLoyaltyBalance(int CorporationId, int IssuerCorporationId, long Points);

internal sealed record EconomyCorporationLoyaltyTransfer(
    long Id,
    long SourceOwnerId,
    int RecipientCorporationId,
    int IssuerCorporationId,
    long Points,
    long SourceBefore,
    long SourceAfter,
    long RecipientBefore,
    long RecipientAfter,
    long Time
);

internal sealed record EconomyCorporationOffice(long Id, int CorporationId, int StationId, int SystemId, long RentedAt, long FeeCents);

internal sealed record EconomyCorporationOfficeSettings(int Id, long RentalFeeCents, int OfficeSlots);

internal sealed record EconomyCorporationPayment(long Id, long PilotId, int CorporationId, long Time, long BalanceCents);

internal sealed record EconomyCorporationShareHolding(int CorporationId, long ShareholderId, long Shares);

internal sealed record EconomyCorporationShareIssue(int CorporationId, long Shares);

internal sealed record EconomyCorporationShareTransfer(
    long Id,
    int CorporationId,
    long ActorId,
    long FromOwnerId,
    long ToOwnerId,
    long Shares,
    long Time
);

internal sealed record EconomyCorporationWallet(int CorporationId, int Key, long BalanceCents);

internal sealed partial class ResidentEconomy
{
    private void RegisterCorporationAssets()
    {
        Register(
            new EconomyTable<SavedCorporationLoyaltyBalance, EconomyCorporationLoyaltyBalance>(
                "WorldCorporationLoyaltyWallet",
                row => new(row.CorporationId, row.IssuerCorporationId),
                row => new(row.CorporationId, row.IssuerCorporationId, row.Points),
                state => new SavedCorporationLoyaltyBalance
                {
                    CorporationId = state.CorporationId,
                    IssuerCorporationId = state.IssuerCorporationId,
                    Points = state.Points,
                },
                ProjectSavedCorporationLoyaltyBalance,
                "CorporationId,IssuerCorporationId,Points",
                reader => new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)),
                state => new(state.CorporationId, state.IssuerCorporationId),
                indexes: [new("CorporationId", row => row.CorporationId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedCorporationLoyaltyTransfer, EconomyCorporationLoyaltyTransfer>(
                "WorldCorporationLoyaltyTransfer",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.SourceOwnerId,
                        row.RecipientCorporationId,
                        row.IssuerCorporationId,
                        row.Points,
                        row.SourceBefore,
                        row.SourceAfter,
                        row.RecipientBefore,
                        row.RecipientAfter,
                        row.Time
                    ),
                state => new SavedCorporationLoyaltyTransfer
                {
                    Id = state.Id,
                    SourceOwnerId = state.SourceOwnerId,
                    RecipientCorporationId = state.RecipientCorporationId,
                    IssuerCorporationId = state.IssuerCorporationId,
                    Points = state.Points,
                    SourceBefore = state.SourceBefore,
                    SourceAfter = state.SourceAfter,
                    RecipientBefore = state.RecipientBefore,
                    RecipientAfter = state.RecipientAfter,
                    Time = state.Time,
                },
                ProjectSavedCorporationLoyaltyTransfer,
                "Id,SourceOwnerId,RecipientCorporationId,IssuerCorporationId,Points,"
                    + "SourceBefore,SourceAfter,RecipientBefore,RecipientAfter,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10)
                    ),
                state => new(state.Id),
                indexes: [new("SourceOwnerId", row => row.SourceOwnerId), new("RecipientCorporationId", row => row.RecipientCorporationId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedCorporationOffice, EconomyCorporationOffice>(
                "WorldCorporationOffice",
                row => new(row.Id),
                row => new(row.Id, row.CorporationId, row.StationId, row.SystemId, row.RentedAt, row.FeeCents),
                state => new SavedCorporationOffice
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    StationId = state.StationId,
                    SystemId = state.SystemId,
                    RentedAt = state.RentedAt,
                    FeeCents = state.FeeCents,
                },
                ProjectSavedCorporationOffice,
                "Id,CorporationId,StationId,SystemId,RentedAt,FeeCents",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.Id),
                indexes: [new("CorporationId", row => row.CorporationId), new("StationId", row => row.StationId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedCorporationOfficeSettings, EconomyCorporationOfficeSettings>(
                "WorldCorporationOfficeSettings",
                row => new(row.Id),
                row => new(row.Id, row.RentalFeeCents, row.OfficeSlots),
                state => new SavedCorporationOfficeSettings
                {
                    Id = state.Id,
                    RentalFeeCents = state.RentalFeeCents,
                    OfficeSlots = state.OfficeSlots,
                },
                ProjectSavedCorporationOfficeSettings,
                "Id,RentalFeeCents,OfficeSlots",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3)),
                state => new(state.Id),
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedCorporationPayment, EconomyCorporationPayment>(
                "WorldCorporationPayment",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.CorporationId, row.Time, row.BalanceCents),
                state => new SavedCorporationPayment
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    CorporationId = state.CorporationId,
                    Time = state.Time,
                    BalanceCents = state.BalanceCents,
                },
                ProjectSavedCorporationPayment,
                "Id,PilotId,CorporationId,Time,BalanceCents",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5)),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedCorporationShareHolding, EconomyCorporationShareHolding>(
                "WorldCorporationShareHolding",
                row => new(row.CorporationId, row.ShareholderId),
                row => new(row.CorporationId, row.ShareholderId, row.Shares),
                state => new SavedCorporationShareHolding
                {
                    CorporationId = state.CorporationId,
                    ShareholderId = state.ShareholderId,
                    Shares = state.Shares,
                },
                ProjectSavedCorporationShareHolding,
                "CorporationId,ShareholderId,Shares",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.CorporationId, state.ShareholderId),
                indexes: [new("CorporationId", row => row.CorporationId), new("ShareholderId", row => row.ShareholderId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedCorporationShareIssue, EconomyCorporationShareIssue>(
                "WorldCorporationShareIssue",
                row => new(row.CorporationId),
                row => new(row.CorporationId, row.Shares),
                state => new SavedCorporationShareIssue { CorporationId = state.CorporationId, Shares = state.Shares },
                ProjectSavedCorporationShareIssue,
                "CorporationId,Shares",
                reader => new(reader.GetInt32(1), reader.GetInt64(2)),
                state => new(state.CorporationId),
                primaryColumn: "CorporationId"
            )
        );
        Register(
            new EconomyTable<SavedCorporationShareTransfer, EconomyCorporationShareTransfer>(
                "WorldCorporationShareTransfer",
                row => new(row.Id),
                row => new(row.Id, row.CorporationId, row.ActorId, row.FromOwnerId, row.ToOwnerId, row.Shares, row.Time),
                state => new SavedCorporationShareTransfer
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    ActorId = state.ActorId,
                    FromOwnerId = state.FromOwnerId,
                    ToOwnerId = state.ToOwnerId,
                    Shares = state.Shares,
                    Time = state.Time,
                },
                ProjectSavedCorporationShareTransfer,
                "Id,CorporationId,ActorId,FromOwnerId,ToOwnerId,Shares,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.Id),
                indexes: [new("CorporationId", row => row.CorporationId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedCorporationWallet, EconomyCorporationWallet>(
                "WorldCorporationWallet",
                row => new(row.CorporationId, row.Key),
                row => new(row.CorporationId, row.Key, row.BalanceCents),
                state => new SavedCorporationWallet
                {
                    CorporationId = state.CorporationId,
                    Key = state.Key,
                    BalanceCents = state.BalanceCents,
                },
                ProjectSavedCorporationWallet,
                "CorporationId,Key,BalanceCents",
                reader => new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)),
                state => new(state.CorporationId, state.Key),
                indexes: [new("CorporationId", row => row.CorporationId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectSavedCorporationLoyaltyBalance(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationLoyaltyBalance>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCorporationLoyaltyWallet WHERE EXISTS (
                    SELECT 1 FROM json_each({keys})
                    WHERE CorporationId=json_extract(value,'$.Id') AND IssuerCorporationId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationLoyaltyWallet(CorporationId,IssuerCorporationId,Points)
                SELECT json_extract(value,'$.CorporationId'), json_extract(value,'$.IssuerCorporationId'), json_extract(value,'$.Points')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(CorporationId,IssuerCorporationId) DO UPDATE SET
                    Points=excluded.Points
                """
            );
        }
    }

    private static void ProjectSavedCorporationLoyaltyTransfer(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationLoyaltyTransfer>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationLoyaltyTransfer WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationLoyaltyTransfer(
                    Id,SourceOwnerId,RecipientCorporationId,IssuerCorporationId,Points,
                    SourceBefore,SourceAfter,RecipientBefore,RecipientAfter,Time)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.SourceOwnerId'), json_extract(value,'$.RecipientCorporationId'),
                    json_extract(value,'$.IssuerCorporationId'), json_extract(value,'$.Points'), json_extract(value,'$.SourceBefore'),
                    json_extract(value,'$.SourceAfter'), json_extract(value,'$.RecipientBefore'), json_extract(value,'$.RecipientAfter'),
                    json_extract(value,'$.Time')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    SourceOwnerId=excluded.SourceOwnerId, RecipientCorporationId=excluded.RecipientCorporationId,
                    IssuerCorporationId=excluded.IssuerCorporationId, Points=excluded.Points,
                    SourceBefore=excluded.SourceBefore, SourceAfter=excluded.SourceAfter,
                    RecipientBefore=excluded.RecipientBefore, RecipientAfter=excluded.RecipientAfter,
                    Time=excluded.Time
                """
            );
        }
    }

    private static void ProjectSavedCorporationOffice(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyCorporationOffice>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationOffice WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationOffice(Id,CorporationId,StationId,SystemId,RentedAt,FeeCents)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.CorporationId'), json_extract(value,'$.StationId'),
                    json_extract(value,'$.SystemId'), json_extract(value,'$.RentedAt'), json_extract(value,'$.FeeCents')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    CorporationId=excluded.CorporationId, StationId=excluded.StationId,
                    SystemId=excluded.SystemId, RentedAt=excluded.RentedAt,
                    FeeCents=excluded.FeeCents
                """
            );
        }
    }

    private static void ProjectSavedCorporationOfficeSettings(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationOfficeSettings>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationOfficeSettings WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationOfficeSettings(Id,RentalFeeCents,OfficeSlots)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.RentalFeeCents'), json_extract(value,'$.OfficeSlots')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    RentalFeeCents=excluded.RentalFeeCents, OfficeSlots=excluded.OfficeSlots
                """
            );
        }
    }

    private static void ProjectSavedCorporationPayment(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationPayment>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationPayment WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationPayment(Id,PilotId,CorporationId,Time,BalanceCents)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.Time'), json_extract(value,'$.BalanceCents')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    PilotId=excluded.PilotId, CorporationId=excluded.CorporationId,
                    Time=excluded.Time, BalanceCents=excluded.BalanceCents
                """
            );
        }
    }

    private static void ProjectSavedCorporationShareHolding(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationShareHolding>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCorporationShareHolding WHERE EXISTS (
                    SELECT 1 FROM json_each({keys})
                    WHERE CorporationId=json_extract(value,'$.Id') AND ShareholderId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationShareHolding(CorporationId,ShareholderId,Shares)
                SELECT json_extract(value,'$.CorporationId'), json_extract(value,'$.ShareholderId'), json_extract(value,'$.Shares')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(CorporationId,ShareholderId) DO UPDATE SET
                    Shares=excluded.Shares
                """
            );
        }
    }

    private static void ProjectSavedCorporationShareIssue(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationShareIssue>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"DELETE FROM WorldCorporationShareIssue WHERE CorporationId IN (SELECT value FROM json_each({keys}))"
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationShareIssue(CorporationId,Shares)
                SELECT json_extract(value,'$.CorporationId'), json_extract(value,'$.Shares')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(CorporationId) DO UPDATE SET
                    Shares=excluded.Shares
                """
            );
        }
    }

    private static void ProjectSavedCorporationShareTransfer(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationShareTransfer>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationShareTransfer WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationShareTransfer(Id,CorporationId,ActorId,FromOwnerId,ToOwnerId,Shares,Time)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.CorporationId'), json_extract(value,'$.ActorId'),
                    json_extract(value,'$.FromOwnerId'), json_extract(value,'$.ToOwnerId'), json_extract(value,'$.Shares'),
                    json_extract(value,'$.Time')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    CorporationId=excluded.CorporationId, ActorId=excluded.ActorId,
                    FromOwnerId=excluded.FromOwnerId, ToOwnerId=excluded.ToOwnerId,
                    Shares=excluded.Shares, Time=excluded.Time
                """
            );
        }
    }

    private static void ProjectSavedCorporationWallet(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyCorporationWallet>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCorporationWallet WHERE EXISTS (
                    SELECT 1 FROM json_each({keys})
                    WHERE CorporationId=json_extract(value,'$.Id') AND Key=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationWallet(CorporationId,Key,BalanceCents)
                SELECT json_extract(value,'$.CorporationId'), json_extract(value,'$.Key'), json_extract(value,'$.BalanceCents')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(CorporationId,Key) DO UPDATE SET
                    BalanceCents=excluded.BalanceCents
                """
            );
        }
    }
}
