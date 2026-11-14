using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyCorporationApplication(
    long Id,
    long PilotId,
    int CorporationId,
    string Text,
    CorporationApplicationStatus Status,
    long AppliedAt,
    long UpdatedAt,
    string Response
);

internal sealed record EconomyCorporationBillPaymentSettings(int CorporationId, string DataJson);

internal sealed record EconomyCorporationChange(
    long Id,
    long PilotId,
    string? ApplicationJson,
    string? MembershipJson,
    string? AdministrationJson,
    string? RoleJson,
    string? TitleJson,
    string? ShareJson,
    string? StorageJson
);

internal sealed record EconomyCorporationContact(int CorporationId, long ContactId, double Standing, long LabelMask);

internal sealed record EconomyCorporationContactLabel(int CorporationId, long Id, string Name, int? Color);

internal sealed record EconomyCorporationEmployment(long Id, long PilotId, int CorporationId, long StartedAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterCorporationMembership()
    {
        Register(
            new EconomyTable<SavedCorporationApplication, EconomyCorporationApplication>(
                "WorldCorporationApplication",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.CorporationId, row.Text, row.Status, row.AppliedAt, row.UpdatedAt, row.Response),
                state => new SavedCorporationApplication
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    CorporationId = state.CorporationId,
                    Text = state.Text,
                    Status = state.Status,
                    AppliedAt = state.AppliedAt,
                    UpdatedAt = state.UpdatedAt,
                    Response = state.Response,
                },
                ProjectSavedCorporationApplication,
                "Id,PilotId,CorporationId,Text,Status,AppliedAt,UpdatedAt,Response",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        (CorporationApplicationStatus)reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetString(8)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId), new("CorporationId", row => row.CorporationId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedCorporationBillPaymentSettings, EconomyCorporationBillPaymentSettings>(
                "WorldCorporationBillPaymentSettings",
                row => new(row.CorporationId),
                row => new(row.CorporationId, row.DataJson),
                state => new SavedCorporationBillPaymentSettings { CorporationId = state.CorporationId, DataJson = state.DataJson },
                ProjectSavedCorporationBillPaymentSettings,
                "CorporationId,DataJson",
                reader => new(reader.GetInt32(1), reader.GetString(2)),
                state => new(state.CorporationId),
                primaryColumn: "CorporationId"
            )
        );
        Register(
            new EconomyTable<SavedCorporationChange, EconomyCorporationChange>(
                "WorldCorporationChange",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.PilotId,
                        row.ApplicationJson,
                        row.MembershipJson,
                        row.AdministrationJson,
                        row.RoleJson,
                        row.TitleJson,
                        row.ShareJson,
                        row.StorageJson
                    ),
                state => new SavedCorporationChange
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    ApplicationJson = state.ApplicationJson,
                    MembershipJson = state.MembershipJson,
                    AdministrationJson = state.AdministrationJson,
                    RoleJson = state.RoleJson,
                    TitleJson = state.TitleJson,
                    ShareJson = state.ShareJson,
                    StorageJson = state.StorageJson,
                },
                ProjectSavedCorporationChange,
                "Id,PilotId,ApplicationJson,MembershipJson,AdministrationJson,RoleJson,TitleJson,ShareJson,StorageJson",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedCorporationContact, EconomyCorporationContact>(
                "WorldCorporationContact",
                row => new(row.CorporationId, row.ContactId),
                row => new(row.CorporationId, row.ContactId, row.Standing, row.LabelMask),
                state => new SavedCorporationContact
                {
                    CorporationId = state.CorporationId,
                    ContactId = state.ContactId,
                    Standing = state.Standing,
                    LabelMask = state.LabelMask,
                },
                ProjectSavedCorporationContact,
                "CorporationId,ContactId,Standing,LabelMask",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetInt64(4)),
                state => new(state.CorporationId, state.ContactId),
                indexes: [new("CorporationId", row => row.CorporationId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedCorporationContactLabel, EconomyCorporationContactLabel>(
                "WorldCorporationContactLabel",
                row => new(row.CorporationId, row.Id),
                row => new(row.CorporationId, row.Id, row.Name, row.Color),
                state => new SavedCorporationContactLabel
                {
                    CorporationId = state.CorporationId,
                    Id = state.Id,
                    Name = state.Name,
                    Color = state.Color,
                },
                ProjectSavedCorporationContactLabel,
                "CorporationId,Id,Name,Color",
                reader => new(reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)),
                state => new(state.CorporationId, state.Id),
                indexes: [new("CorporationId", row => row.CorporationId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedCorporationEmployment, EconomyCorporationEmployment>(
                "WorldCorporationEmployment",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.CorporationId, row.StartedAt),
                state => new SavedCorporationEmployment
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    CorporationId = state.CorporationId,
                    StartedAt = state.StartedAt,
                },
                ProjectSavedCorporationEmployment,
                "Id,PilotId,CorporationId,StartedAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt64(4)),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id"
            )
        );
    }

    private static void ProjectSavedCorporationApplication(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationApplication>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationApplication WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationApplication(Id,PilotId,CorporationId,Text,Status,AppliedAt,UpdatedAt,Response)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.Text'), json_extract(value,'$.Status'), json_extract(value,'$.AppliedAt'),
                    json_extract(value,'$.UpdatedAt'), json_extract(value,'$.Response')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    PilotId=excluded.PilotId, CorporationId=excluded.CorporationId,
                    Text=excluded.Text, Status=excluded.Status,
                    AppliedAt=excluded.AppliedAt, UpdatedAt=excluded.UpdatedAt,
                    Response=excluded.Response
                """
            );
        }
    }

    private static void ProjectSavedCorporationBillPaymentSettings(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationBillPaymentSettings>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"DELETE FROM WorldCorporationBillPaymentSettings WHERE CorporationId IN (SELECT value FROM json_each({keys}))"
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationBillPaymentSettings(CorporationId,DataJson)
                SELECT json_extract(value,'$.CorporationId'), json_extract(value,'$.DataJson')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(CorporationId) DO UPDATE SET
                    DataJson=excluded.DataJson
                """
            );
        }
    }

    private static void ProjectSavedCorporationChange(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyCorporationChange>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationChange WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationChange(
                    Id,PilotId,ApplicationJson,MembershipJson,AdministrationJson,RoleJson,TitleJson,ShareJson,StorageJson)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.ApplicationJson'),
                    json_extract(value,'$.MembershipJson'), json_extract(value,'$.AdministrationJson'), json_extract(value,'$.RoleJson'),
                    json_extract(value,'$.TitleJson'), json_extract(value,'$.ShareJson'), json_extract(value,'$.StorageJson')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    PilotId=excluded.PilotId, ApplicationJson=excluded.ApplicationJson,
                    MembershipJson=excluded.MembershipJson, AdministrationJson=excluded.AdministrationJson,
                    RoleJson=excluded.RoleJson, TitleJson=excluded.TitleJson,
                    ShareJson=excluded.ShareJson, StorageJson=excluded.StorageJson
                """
            );
        }
    }

    private static void ProjectSavedCorporationContact(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationContact>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCorporationContact WHERE EXISTS (
                    SELECT 1 FROM json_each({keys})
                    WHERE CorporationId=json_extract(value,'$.Id') AND ContactId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationContact(CorporationId,ContactId,Standing,LabelMask)
                SELECT json_extract(value,'$.CorporationId'), json_extract(value,'$.ContactId'), json_extract(value,'$.Standing'),
                    json_extract(value,'$.LabelMask')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(CorporationId,ContactId) DO UPDATE SET
                    Standing=excluded.Standing, LabelMask=excluded.LabelMask
                """
            );
        }
    }

    private static void ProjectSavedCorporationContactLabel(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationContactLabel>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCorporationContactLabel WHERE EXISTS (
                    SELECT 1 FROM json_each({keys})
                    WHERE CorporationId=json_extract(value,'$.Id') AND Id=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationContactLabel(CorporationId,Id,Name,Color)
                SELECT json_extract(value,'$.CorporationId'), json_extract(value,'$.Id'), json_extract(value,'$.Name'),
                    json_extract(value,'$.Color')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(CorporationId,Id) DO UPDATE SET
                    Name=excluded.Name, Color=excluded.Color
                """
            );
        }
    }

    private static void ProjectSavedCorporationEmployment(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationEmployment>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationEmployment WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationEmployment(Id,PilotId,CorporationId,StartedAt)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.StartedAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    PilotId=excluded.PilotId, CorporationId=excluded.CorporationId,
                    StartedAt=excluded.StartedAt
                """
            );
        }
    }
}
