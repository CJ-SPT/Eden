using System.Text.Json;
using Eden.Data.World.Bookmarks;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedBookmarkFolder(
    long Id,
    long PilotId,
    string Name,
    string Description,
    bool IsActive,
    bool IsKnown,
    bool IsPersonal,
    long? AdminGroupId,
    long? ManageGroupId,
    long? UseGroupId,
    long? ViewGroupId
);

internal sealed record EconomySavedBookmarkSubfolder(long Id, long FolderId, string Name, long CreatorId);

internal sealed record EconomySavedBookmark(
    long Id,
    long FolderId,
    long? SubfolderId,
    long CreatorId,
    long? ItemId,
    int TypeId,
    int LocationId,
    double X,
    double Y,
    double Z,
    string Name,
    string Note,
    long CreatedAt,
    long? ExpiresAt
);

internal sealed record EconomySavedBookmarkConnection(long PilotId, long FolderId, bool IsActive);

internal sealed partial class ResidentEconomy
{
    private void RegisterBookmarksTables()
    {
        // A moved bookmark must project before the old subfolder's RESTRICT key can be deleted.
        Register(
            new EconomyTable<SavedBookmark, EconomySavedBookmark>(
                "WorldBookmark",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.FolderId,
                        row.SubfolderId,
                        row.CreatorId,
                        row.ItemId,
                        row.TypeId,
                        row.LocationId,
                        row.X,
                        row.Y,
                        row.Z,
                        row.Name,
                        row.Note,
                        row.CreatedAt,
                        row.ExpiresAt
                    ),
                state => new SavedBookmark
                {
                    Id = state.Id,
                    FolderId = state.FolderId,
                    SubfolderId = state.SubfolderId,
                    CreatorId = state.CreatorId,
                    ItemId = state.ItemId,
                    TypeId = state.TypeId,
                    LocationId = state.LocationId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                    Name = state.Name,
                    Note = state.Note,
                    CreatedAt = state.CreatedAt,
                    ExpiresAt = state.ExpiresAt,
                },
                ProjectSavedBookmark,
                "Id,FolderId,SubfolderId,CreatorId,ItemId,TypeId,LocationId,X," + "Y,Z,Name,Note,CreatedAt,ExpiresAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9),
                        reader.GetDouble(10),
                        reader.GetString(11),
                        reader.GetString(12),
                        reader.GetInt64(13),
                        reader.IsDBNull(14) ? null : reader.GetInt64(14)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("FolderId", row => row.FolderId),
                    new("SubfolderId", row => row.SubfolderId),
                    new("CreatorId", row => row.CreatorId),
                ],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedBookmarkSubfolder, EconomySavedBookmarkSubfolder>(
                "WorldBookmarkSubfolder",
                row => new(row.Id),
                row => new(row.Id, row.FolderId, row.Name, row.CreatorId),
                state => new SavedBookmarkSubfolder
                {
                    Id = state.Id,
                    FolderId = state.FolderId,
                    Name = state.Name,
                    CreatorId = state.CreatorId,
                },
                ProjectSavedBookmarkSubfolder,
                "Id,FolderId,Name,CreatorId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4)),
                state => new(state.Id),
                indexes: [new("FolderId", row => row.FolderId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedBookmarkConnection, EconomySavedBookmarkConnection>(
                "WorldBookmarkConnection",
                row => new(row.PilotId, row.FolderId),
                row => new(row.PilotId, row.FolderId, row.IsActive),
                state => new SavedBookmarkConnection
                {
                    PilotId = state.PilotId,
                    FolderId = state.FolderId,
                    IsActive = state.IsActive,
                },
                ProjectSavedBookmarkConnection,
                "PilotId,FolderId,IsActive",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3)),
                state => new(state.PilotId, state.FolderId),
                indexes: [new("PilotId", row => row.PilotId), new("FolderId", row => row.FolderId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedBookmarkFolder, EconomySavedBookmarkFolder>(
                "WorldBookmarkFolder",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.PilotId,
                        row.Name,
                        row.Description,
                        row.IsActive,
                        row.IsKnown,
                        row.IsPersonal,
                        row.AdminGroupId,
                        row.ManageGroupId,
                        row.UseGroupId,
                        row.ViewGroupId
                    ),
                state => new SavedBookmarkFolder
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    Name = state.Name,
                    Description = state.Description,
                    IsActive = state.IsActive,
                    IsKnown = state.IsKnown,
                    IsPersonal = state.IsPersonal,
                    AdminGroupId = state.AdminGroupId,
                    ManageGroupId = state.ManageGroupId,
                    UseGroupId = state.UseGroupId,
                    ViewGroupId = state.ViewGroupId,
                },
                ProjectSavedBookmarkFolder,
                "Id,PilotId,Name,Description,IsActive,IsKnown,IsPersonal,AdminGroupId," + "ManageGroupId,UseGroupId,ViewGroupId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetBoolean(5),
                        reader.GetBoolean(6),
                        reader.GetBoolean(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.IsDBNull(11) ? null : reader.GetInt64(11)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("PilotId", row => row.PilotId),
                    new("AdminGroupId", row => row.AdminGroupId),
                    new("ManageGroupId", row => row.ManageGroupId),
                    new("UseGroupId", row => row.UseGroupId),
                    new("ViewGroupId", row => row.ViewGroupId),
                ],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedBookmarkFolder(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedBookmarkFolder>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldBookmarkFolder
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldBookmarkFolder.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldBookmarkFolder(
                        Id,PilotId,Name,Description,IsActive,IsKnown,IsPersonal,
                        AdminGroupId,ManageGroupId,UseGroupId,ViewGroupId
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.Name'),
                        json_extract(value,'$.Description'),json_extract(value,'$.IsActive'),json_extract(value,'$.IsKnown'),
                        json_extract(value,'$.IsPersonal'),json_extract(value,'$.AdminGroupId'),json_extract(value,'$.ManageGroupId'),
                        json_extract(value,'$.UseGroupId'),json_extract(value,'$.ViewGroupId')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        PilotId=excluded.PilotId,Name=excluded.Name,Description=excluded.Description,
                        IsActive=excluded.IsActive,IsKnown=excluded.IsKnown,IsPersonal=excluded.IsPersonal,
                        AdminGroupId=excluded.AdminGroupId,ManageGroupId=excluded.ManageGroupId,UseGroupId=excluded.UseGroupId,
                        ViewGroupId=excluded.ViewGroupId
                """
            );
        }
    }

    private static void ProjectSavedBookmarkSubfolder(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedBookmarkSubfolder>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldBookmarkSubfolder
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldBookmarkSubfolder.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldBookmarkSubfolder(
                        Id,FolderId,Name,CreatorId
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.FolderId'),json_extract(value,'$.Name'),
                        json_extract(value,'$.CreatorId')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        FolderId=excluded.FolderId,Name=excluded.Name,CreatorId=excluded.CreatorId
                """
            );
        }
    }

    private static void ProjectSavedBookmark(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedBookmark>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldBookmark
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldBookmark.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldBookmark(
                        Id,FolderId,SubfolderId,CreatorId,ItemId,TypeId,LocationId,
                        X,Y,Z,Name,Note,CreatedAt,ExpiresAt
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.FolderId'),json_extract(value,'$.SubfolderId'),
                        json_extract(value,'$.CreatorId'),json_extract(value,'$.ItemId'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.LocationId'),json_extract(value,'$.X'),json_extract(value,'$.Y'),
                        json_extract(value,'$.Z'),json_extract(value,'$.Name'),json_extract(value,'$.Note'),
                        json_extract(value,'$.CreatedAt'),json_extract(value,'$.ExpiresAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        FolderId=excluded.FolderId,SubfolderId=excluded.SubfolderId,CreatorId=excluded.CreatorId,
                        ItemId=excluded.ItemId,TypeId=excluded.TypeId,LocationId=excluded.LocationId,
                        X=excluded.X,Y=excluded.Y,Z=excluded.Z,
                        Name=excluded.Name,Note=excluded.Note,CreatedAt=excluded.CreatedAt,
                        ExpiresAt=excluded.ExpiresAt
                """
            );
        }
    }

    private static void ProjectSavedBookmarkConnection(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedBookmarkConnection>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldBookmarkConnection
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldBookmarkConnection.PilotId=json_extract(value,'$.Id') AND
                            WorldBookmarkConnection.FolderId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldBookmarkConnection(
                        PilotId,FolderId,IsActive
                )
                SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.FolderId'),json_extract(value,'$.IsActive')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,FolderId) DO UPDATE SET
                        IsActive=excluded.IsActive
                """
            );
        }
    }
}
