using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Economy;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedStructureService(long ItemId, long StructureId, int Slot, bool Online, long? NextFuelAt);

internal sealed record EconomySavedMoonExtraction(
    long Id,
    long StructureId,
    int MoonId,
    int SystemId,
    int CorporationId,
    long StartedAt,
    long ReadyAt,
    long AutoFractureAt,
    long? FracturedAt,
    long? FieldExpiresAt,
    bool Ended,
    string Composition
);

internal sealed record EconomySavedMoonMiningLog(
    long Id,
    long StructureId,
    long PilotId,
    long Day,
    int CorporationId,
    int SystemId,
    int TypeId,
    long Quantity,
    long Critical,
    long Wasted
);

internal sealed record EconomySavedServicePayment(
    long Id,
    long PilotId,
    long CounterpartyId,
    long ContextId,
    WalletEntryType Kind,
    long AmountCents,
    long BalanceCents,
    long Time
);

internal sealed record EconomySavedManufacturingIndex(int SystemId, decimal Index);

internal sealed partial class ResidentEconomy
{
    private void RegisterStructureIndustryTables()
    {
        Register(
            new EconomyTable<SavedStructureService, EconomySavedStructureService>(
                "WorldStructureService",
                row => new(row.ItemId),
                row => new(row.ItemId, row.StructureId, row.Slot, row.Online, row.NextFuelAt),
                state => new SavedStructureService
                {
                    ItemId = state.ItemId,
                    StructureId = state.StructureId,
                    Slot = state.Slot,
                    Online = state.Online,
                    NextFuelAt = state.NextFuelAt,
                },
                ProjectSavedStructureService,
                "ItemId,StructureId,Slot,Online,NextFuelAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetBoolean(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5)
                    ),
                state => new(state.ItemId),
                indexes: [new("StructureId", row => row.StructureId), new("Online", row => row.Online)],
                primaryColumn: "ItemId"
            )
        );
        Register(
            new EconomyTable<SavedMoonExtraction, EconomySavedMoonExtraction>(
                "WorldMoonExtraction",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.StructureId,
                        row.MoonId,
                        row.SystemId,
                        row.CorporationId,
                        row.StartedAt,
                        row.ReadyAt,
                        row.AutoFractureAt,
                        row.FracturedAt,
                        row.FieldExpiresAt,
                        row.Ended,
                        row.Composition
                    ),
                state => new SavedMoonExtraction
                {
                    Id = state.Id,
                    StructureId = state.StructureId,
                    MoonId = state.MoonId,
                    SystemId = state.SystemId,
                    CorporationId = state.CorporationId,
                    StartedAt = state.StartedAt,
                    ReadyAt = state.ReadyAt,
                    AutoFractureAt = state.AutoFractureAt,
                    FracturedAt = state.FracturedAt,
                    FieldExpiresAt = state.FieldExpiresAt,
                    Ended = state.Ended,
                    Composition = state.Composition,
                },
                ProjectSavedMoonExtraction,
                "Id,StructureId,MoonId,SystemId,CorporationId,StartedAt,ReadyAt,AutoFractureAt,"
                    + "FracturedAt,FieldExpiresAt,Ended,Composition",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.GetBoolean(11),
                        reader.GetString(12)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("StructureId", row => row.StructureId),
                    new("SystemId", row => row.SystemId),
                    new("CorporationId", row => row.CorporationId),
                    new("Ended", row => row.Ended),
                ],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedMoonMiningLog, EconomySavedMoonMiningLog>(
                "WorldMoonMiningLog",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.StructureId,
                        row.PilotId,
                        row.Day,
                        row.CorporationId,
                        row.SystemId,
                        row.TypeId,
                        row.Quantity,
                        row.Critical,
                        row.Wasted
                    ),
                state => new SavedMoonMiningLog
                {
                    Id = state.Id,
                    StructureId = state.StructureId,
                    PilotId = state.PilotId,
                    Day = state.Day,
                    CorporationId = state.CorporationId,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                    Quantity = state.Quantity,
                    Critical = state.Critical,
                    Wasted = state.Wasted,
                },
                ProjectSavedMoonMiningLog,
                "Id,StructureId,PilotId,Day,CorporationId,SystemId,TypeId,Quantity,Critical,Wasted",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10)
                    ),
                state => new(state.Id),
                indexes: [new("StructureId", row => row.StructureId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedServicePayment, EconomySavedServicePayment>(
                "WorldServicePayment",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.CounterpartyId, row.ContextId, row.Kind, row.AmountCents, row.BalanceCents, row.Time),
                state => new SavedServicePayment
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    CounterpartyId = state.CounterpartyId,
                    ContextId = state.ContextId,
                    Kind = state.Kind,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    Time = state.Time,
                },
                ProjectSavedServicePayment,
                "Id,PilotId,CounterpartyId,ContextId,Kind,AmountCents,BalanceCents,Time",
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
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedManufacturingIndex, EconomySavedManufacturingIndex>(
                "WorldManufacturingIndex",
                row => new(row.SystemId),
                row => new(row.SystemId, row.Index),
                state => new SavedManufacturingIndex { SystemId = state.SystemId, Index = state.Index },
                ProjectSavedManufacturingIndex,
                "SystemId,\"Index\"",
                reader => new(reader.GetInt32(1), decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture)),
                state => new(state.SystemId),
                readOnly: true,
                indexes: [],
                primaryColumn: "SystemId"
            )
        );
    }

    private static void ProjectSavedStructureService(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedStructureService>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldStructureService WHERE ItemId IN (SELECT value FROM json_each({deleted}))");
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldStructureService(
                    ItemId,StructureId,Slot,Online,NextFuelAt
                )
                SELECT
                    json_extract(value,'$.ItemId'),json_extract(value,'$.StructureId'),json_extract(value,'$.Slot'),
                    json_extract(value,'$.Online'),json_extract(value,'$.NextFuelAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(ItemId) DO UPDATE SET
                    StructureId=excluded.StructureId,Slot=excluded.Slot,Online=excluded.Online,
                    NextFuelAt=excluded.NextFuelAt
                """
            );
        }
    }

    private static void ProjectSavedMoonExtraction(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMoonExtraction>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMoonExtraction WHERE Id IN (SELECT value FROM json_each({deleted}))");
        }
        foreach (
            var batch in changes
                .Where(c => c.Value is not null)
                .OrderByDescending(c => c.Value!.Ended)
                .Select(c => c.Value)
                .Chunk(ProjectionBatchSize)
        )
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMoonExtraction(
                    Id,StructureId,MoonId,SystemId,CorporationId,StartedAt,ReadyAt,AutoFractureAt,
                    FracturedAt,FieldExpiresAt,Ended,Composition
                )
                SELECT
                    json_extract(value,'$.Id'),json_extract(value,'$.StructureId'),json_extract(value,'$.MoonId'),
                    json_extract(value,'$.SystemId'),json_extract(value,'$.CorporationId'),json_extract(value,'$.StartedAt'),
                    json_extract(value,'$.ReadyAt'),json_extract(value,'$.AutoFractureAt'),json_extract(value,'$.FracturedAt'),
                    json_extract(value,'$.FieldExpiresAt'),json_extract(value,'$.Ended'),json_extract(value,'$.Composition')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    StructureId=excluded.StructureId,MoonId=excluded.MoonId,SystemId=excluded.SystemId,
                    CorporationId=excluded.CorporationId,StartedAt=excluded.StartedAt,ReadyAt=excluded.ReadyAt,
                    AutoFractureAt=excluded.AutoFractureAt,FracturedAt=excluded.FracturedAt,FieldExpiresAt=excluded.FieldExpiresAt,
                    Ended=excluded.Ended,Composition=excluded.Composition
                """
            );
        }
    }

    private static void ProjectSavedMoonMiningLog(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMoonMiningLog>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMoonMiningLog WHERE Id IN (SELECT value FROM json_each({deleted}))");
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMoonMiningLog(
                    Id,StructureId,PilotId,Day,CorporationId,SystemId,TypeId,Quantity,
                    Critical,Wasted
                )
                SELECT
                    json_extract(value,'$.Id'),json_extract(value,'$.StructureId'),json_extract(value,'$.PilotId'),
                    json_extract(value,'$.Day'),json_extract(value,'$.CorporationId'),json_extract(value,'$.SystemId'),
                    json_extract(value,'$.TypeId'),json_extract(value,'$.Quantity'),json_extract(value,'$.Critical'),
                    json_extract(value,'$.Wasted')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    StructureId=excluded.StructureId,PilotId=excluded.PilotId,Day=excluded.Day,
                    CorporationId=excluded.CorporationId,SystemId=excluded.SystemId,TypeId=excluded.TypeId,
                    Quantity=excluded.Quantity,Critical=excluded.Critical,Wasted=excluded.Wasted
                """
            );
        }
    }

    private static void ProjectSavedServicePayment(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedServicePayment>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldServicePayment WHERE Id IN (SELECT value FROM json_each({deleted}))");
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldServicePayment(
                    Id,PilotId,CounterpartyId,ContextId,Kind,AmountCents,BalanceCents,Time
                )
                SELECT
                    json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.CounterpartyId'),
                    json_extract(value,'$.ContextId'),json_extract(value,'$.Kind'),json_extract(value,'$.AmountCents'),
                    json_extract(value,'$.BalanceCents'),json_extract(value,'$.Time')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    PilotId=excluded.PilotId,CounterpartyId=excluded.CounterpartyId,ContextId=excluded.ContextId,
                    Kind=excluded.Kind,AmountCents=excluded.AmountCents,BalanceCents=excluded.BalanceCents,
                    Time=excluded.Time
                """
            );
        }
    }

    private static void ProjectSavedManufacturingIndex(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedManufacturingIndex>> changes
    )
    {
        if (changes.Count != 0)
        {
            throw new InvalidOperationException("Reference economic rows cannot be projected.");
        }
    }

    private static void ProjectIndustryJob(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyIndustryJob>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldIndustryJob WHERE Id IN (SELECT value FROM json_each({deleted}))");
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldIndustryJob(
                    Id,SuspendedAt,Activity,LicensedRuns,BlueprintCopy,MaterialEfficiency,TimeEfficiency,OutputRuns,
                    Probability,SuccessfulRuns,OptionalTypeId,PilotId,BlueprintId,BlueprintTypeId,StationId,ProductTypeId,
                    Runs,OutputQuantity,StartTime,EndTime,DurationSeconds,Status,CompletedAt,MaterialsJson,
                    CostCents
                )
                SELECT
                    json_extract(value,'$.Id'),json_extract(value,'$.SuspendedAt'),json_extract(value,'$.Activity'),
                    json_extract(value,'$.LicensedRuns'),json_extract(value,'$.BlueprintCopy'),json_extract(value,'$.MaterialEfficiency'),
                    json_extract(value,'$.TimeEfficiency'),json_extract(value,'$.OutputRuns'),json_extract(value,'$.Probability'),
                    json_extract(value,'$.SuccessfulRuns'),json_extract(value,'$.OptionalTypeId'),json_extract(value,'$.PilotId'),
                    json_extract(value,'$.BlueprintId'),json_extract(value,'$.BlueprintTypeId'),json_extract(value,'$.StationId'),
                    json_extract(value,'$.ProductTypeId'),json_extract(value,'$.Runs'),json_extract(value,'$.OutputQuantity'),
                    json_extract(value,'$.StartTime'),json_extract(value,'$.EndTime'),json_extract(value,'$.DurationSeconds'),
                    json_extract(value,'$.Status'),json_extract(value,'$.CompletedAt'),json_extract(value,'$.MaterialsJson'),
                    json_extract(value,'$.CostCents')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    SuspendedAt=excluded.SuspendedAt,Activity=excluded.Activity,LicensedRuns=excluded.LicensedRuns,
                    BlueprintCopy=excluded.BlueprintCopy,MaterialEfficiency=excluded.MaterialEfficiency,
                    TimeEfficiency=excluded.TimeEfficiency,
                    OutputRuns=excluded.OutputRuns,Probability=excluded.Probability,SuccessfulRuns=excluded.SuccessfulRuns,
                    OptionalTypeId=excluded.OptionalTypeId,PilotId=excluded.PilotId,BlueprintId=excluded.BlueprintId,
                    BlueprintTypeId=excluded.BlueprintTypeId,StationId=excluded.StationId,ProductTypeId=excluded.ProductTypeId,
                    Runs=excluded.Runs,OutputQuantity=excluded.OutputQuantity,StartTime=excluded.StartTime,
                    EndTime=excluded.EndTime,DurationSeconds=excluded.DurationSeconds,Status=excluded.Status,
                    CompletedAt=excluded.CompletedAt,MaterialsJson=excluded.MaterialsJson,CostCents=excluded.CostCents
                """
            );
        }
    }

    private static void ProjectSavedMarketUnlock(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMarketUnlock>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMarketUnlock WHERE TypeId IN (SELECT value FROM json_each({deleted}))");
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMarketUnlock(
                    TypeId,AccountId,PilotId,JobId,ClaimedAt
                )
                SELECT
                    json_extract(value,'$.TypeId'),json_extract(value,'$.AccountId'),json_extract(value,'$.PilotId'),
                    json_extract(value,'$.JobId'),json_extract(value,'$.ClaimedAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(TypeId) DO UPDATE SET
                    AccountId=excluded.AccountId,PilotId=excluded.PilotId,JobId=excluded.JobId,
                    ClaimedAt=excluded.ClaimedAt
                """
            );
        }
    }
}
