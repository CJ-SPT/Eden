using System.Text.Json;
using Eden.Data.World.AccessGroups;
using Eden.Gameplay.AccessGroups;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedAccessGroup(long Id, long CreatorId, bool IsCorporation, string Name, string Description);

internal sealed record EconomySavedAccessGroupMember(long GroupId, long OwnerId, AccessGroupRole Role);

internal sealed record EconomySavedAccessGroupLog(
    long Id,
    long GroupId,
    long ActorId,
    long Time,
    AccessGroupChange Change,
    long? OwnerId,
    AccessGroupRole? OldRole,
    AccessGroupRole? NewRole,
    string? OldText,
    string? NewText
);

internal sealed partial class ResidentEconomy
{
    private void RegisterAccessGroupsTables()
    {
        Register(
            new EconomyTable<SavedAccessGroup, EconomySavedAccessGroup>(
                "WorldAccessGroup",
                row => new(row.Id),
                row => new(row.Id, row.CreatorId, row.IsCorporation, row.Name, row.Description),
                state => new SavedAccessGroup
                {
                    Id = state.Id,
                    CreatorId = state.CreatorId,
                    IsCorporation = state.IsCorporation,
                    Name = state.Name,
                    Description = state.Description,
                },
                ProjectSavedAccessGroup,
                "Id,CreatorId,IsCorporation,Name,Description",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3), reader.GetString(4), reader.GetString(5)),
                state => new(state.Id),
                indexes: [new("CreatorId", row => row.CreatorId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedAccessGroupMember, EconomySavedAccessGroupMember>(
                "WorldAccessGroupMember",
                row => new(row.GroupId, row.OwnerId),
                row => new(row.GroupId, row.OwnerId, row.Role),
                state => new SavedAccessGroupMember
                {
                    GroupId = state.GroupId,
                    OwnerId = state.OwnerId,
                    Role = state.Role,
                },
                ProjectSavedAccessGroupMember,
                "GroupId,OwnerId,Role",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), (AccessGroupRole)reader.GetInt32(3)),
                state => new(state.GroupId, state.OwnerId),
                indexes: [new("GroupId", row => row.GroupId), new("OwnerId", row => row.OwnerId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedAccessGroupLog, EconomySavedAccessGroupLog>(
                "WorldAccessGroupLog",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.GroupId,
                        row.ActorId,
                        row.Time,
                        row.Change,
                        row.OwnerId,
                        row.OldRole,
                        row.NewRole,
                        row.OldText,
                        row.NewText
                    ),
                state => new SavedAccessGroupLog
                {
                    Id = state.Id,
                    GroupId = state.GroupId,
                    ActorId = state.ActorId,
                    Time = state.Time,
                    Change = state.Change,
                    OwnerId = state.OwnerId,
                    OldRole = state.OldRole,
                    NewRole = state.NewRole,
                    OldText = state.OldText,
                    NewText = state.NewText,
                },
                ProjectSavedAccessGroupLog,
                "Id,GroupId,ActorId,Time,Change,OwnerId,OldRole,NewRole," + "OldText,NewText",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        (AccessGroupChange)reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : (AccessGroupRole)reader.GetInt32(7),
                        reader.IsDBNull(8) ? null : (AccessGroupRole)reader.GetInt32(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9),
                        reader.IsDBNull(10) ? null : reader.GetString(10)
                    ),
                state => new(state.Id),
                indexes: [new("GroupId", row => row.GroupId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedAccessGroup(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedAccessGroup>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldAccessGroup
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldAccessGroup.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAccessGroup(
                        Id,CreatorId,IsCorporation,Name,Description
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.CreatorId'),json_extract(value,'$.IsCorporation'),
                        json_extract(value,'$.Name'),json_extract(value,'$.Description')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        CreatorId=excluded.CreatorId,IsCorporation=excluded.IsCorporation,Name=excluded.Name,
                        Description=excluded.Description
                """
            );
        }
    }

    private static void ProjectSavedAccessGroupMember(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedAccessGroupMember>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldAccessGroupMember
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldAccessGroupMember.GroupId=json_extract(value,'$.Id') AND
                            WorldAccessGroupMember.OwnerId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAccessGroupMember(
                        GroupId,OwnerId,Role
                )
                SELECT
                        json_extract(value,'$.GroupId'),json_extract(value,'$.OwnerId'),json_extract(value,'$.Role')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(GroupId,OwnerId) DO UPDATE SET
                        Role=excluded.Role
                """
            );
        }
    }

    private static void ProjectSavedAccessGroupLog(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedAccessGroupLog>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldAccessGroupLog
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldAccessGroupLog.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAccessGroupLog(
                        Id,GroupId,ActorId,"Time","Change",OwnerId,OldRole,
                        NewRole,OldText,NewText
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.GroupId'),json_extract(value,'$.ActorId'),
                        json_extract(value,'$.Time'),json_extract(value,'$.Change'),json_extract(value,'$.OwnerId'),
                        json_extract(value,'$.OldRole'),json_extract(value,'$.NewRole'),json_extract(value,'$.OldText'),
                        json_extract(value,'$.NewText')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        GroupId=excluded.GroupId,ActorId=excluded.ActorId,"Time"=excluded."Time",
                        "Change"=excluded."Change",OwnerId=excluded.OwnerId,OldRole=excluded.OldRole,
                        NewRole=excluded.NewRole,OldText=excluded.OldText,NewText=excluded.NewText
                """
            );
        }
    }
}
