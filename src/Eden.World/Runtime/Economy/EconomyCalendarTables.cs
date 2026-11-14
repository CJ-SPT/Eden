using System.Text.Json;
using Eden.Data.World.Calendar;
using Eden.Gameplay.Calendar;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCalendarEvent(
    long Id,
    long OwnerId,
    long Time,
    int? DurationMinutes,
    string Title,
    string Description,
    bool Important,
    bool Deleted,
    long Modified
);

internal sealed record EconomySavedCalendarParticipant(long EventId, long PilotId, CalendarResponse Response);

internal sealed record EconomySavedCalendarChange(
    long Id,
    long PilotId,
    long EventId,
    CalendarChangeKind Kind,
    long OwnerId,
    long Time,
    long OldTime,
    int? DurationMinutes,
    string Title,
    bool Important,
    bool Deleted,
    long Modified
);

internal sealed partial class ResidentEconomy
{
    private void RegisterCalendarTables()
    {
        Register(
            new EconomyTable<SavedCalendarEvent, EconomySavedCalendarEvent>(
                "WorldCalendarEvent",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.OwnerId,
                        row.Time,
                        row.DurationMinutes,
                        row.Title,
                        row.Description,
                        row.Important,
                        row.Deleted,
                        row.Modified
                    ),
                state => new SavedCalendarEvent
                {
                    Id = state.Id,
                    OwnerId = state.OwnerId,
                    Time = state.Time,
                    DurationMinutes = state.DurationMinutes,
                    Title = state.Title,
                    Description = state.Description,
                    Important = state.Important,
                    Deleted = state.Deleted,
                    Modified = state.Modified,
                },
                ProjectSavedCalendarEvent,
                "Id,OwnerId,Time,DurationMinutes,Title,Description,Important,Deleted," + "Modified",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetBoolean(7),
                        reader.GetBoolean(8),
                        reader.GetInt64(9)
                    ),
                state => new(state.Id),
                indexes: [new("OwnerId", row => row.OwnerId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedCalendarParticipant, EconomySavedCalendarParticipant>(
                "WorldCalendarParticipant",
                row => new(row.EventId, row.PilotId),
                row => new(row.EventId, row.PilotId, row.Response),
                state => new SavedCalendarParticipant
                {
                    EventId = state.EventId,
                    PilotId = state.PilotId,
                    Response = state.Response,
                },
                ProjectSavedCalendarParticipant,
                "EventId,PilotId,Response",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), (CalendarResponse)reader.GetInt32(3)),
                state => new(state.EventId, state.PilotId),
                indexes: [new("EventId", row => row.EventId), new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedCalendarChange, EconomySavedCalendarChange>(
                "WorldCalendarChange",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.PilotId,
                        row.EventId,
                        row.Kind,
                        row.OwnerId,
                        row.Time,
                        row.OldTime,
                        row.DurationMinutes,
                        row.Title,
                        row.Important,
                        row.Deleted,
                        row.Modified
                    ),
                state => new SavedCalendarChange
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    EventId = state.EventId,
                    Kind = state.Kind,
                    OwnerId = state.OwnerId,
                    Time = state.Time,
                    OldTime = state.OldTime,
                    DurationMinutes = state.DurationMinutes,
                    Title = state.Title,
                    Important = state.Important,
                    Deleted = state.Deleted,
                    Modified = state.Modified,
                },
                ProjectSavedCalendarChange,
                "Id,PilotId,EventId,Kind,OwnerId,Time,OldTime,DurationMinutes," + "Title,Important,Deleted,Modified",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        (CalendarChangeKind)reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        reader.GetString(9),
                        reader.GetBoolean(10),
                        reader.GetBoolean(11),
                        reader.GetInt64(12)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId), new("EventId", row => row.EventId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedCalendarEvent(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCalendarEvent>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCalendarEvent
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldCalendarEvent.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCalendarEvent(
                        Id,OwnerId,"Time",DurationMinutes,Title,Description,Important,
                        Deleted,Modified
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.OwnerId'),json_extract(value,'$.Time'),
                        json_extract(value,'$.DurationMinutes'),json_extract(value,'$.Title'),json_extract(value,'$.Description'),
                        json_extract(value,'$.Important'),json_extract(value,'$.Deleted'),json_extract(value,'$.Modified')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        OwnerId=excluded.OwnerId,"Time"=excluded."Time",DurationMinutes=excluded.DurationMinutes,
                        Title=excluded.Title,Description=excluded.Description,Important=excluded.Important,
                        Deleted=excluded.Deleted,Modified=excluded.Modified
                """
            );
        }
    }

    private static void ProjectSavedCalendarParticipant(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCalendarParticipant>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCalendarParticipant
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldCalendarParticipant.EventId=json_extract(value,'$.Id') AND
                            WorldCalendarParticipant.PilotId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCalendarParticipant(
                        EventId,PilotId,Response
                )
                SELECT
                        json_extract(value,'$.EventId'),json_extract(value,'$.PilotId'),json_extract(value,'$.Response')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(EventId,PilotId) DO UPDATE SET
                        Response=excluded.Response
                """
            );
        }
    }

    private static void ProjectSavedCalendarChange(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCalendarChange>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCalendarChange
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldCalendarChange.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCalendarChange(
                        Id,PilotId,EventId,Kind,OwnerId,"Time",OldTime,
                        DurationMinutes,Title,Important,Deleted,Modified
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.EventId'),
                        json_extract(value,'$.Kind'),json_extract(value,'$.OwnerId'),json_extract(value,'$.Time'),
                        json_extract(value,'$.OldTime'),json_extract(value,'$.DurationMinutes'),json_extract(value,'$.Title'),
                        json_extract(value,'$.Important'),json_extract(value,'$.Deleted'),json_extract(value,'$.Modified')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        PilotId=excluded.PilotId,EventId=excluded.EventId,Kind=excluded.Kind,
                        OwnerId=excluded.OwnerId,"Time"=excluded."Time",OldTime=excluded.OldTime,
                        DurationMinutes=excluded.DurationMinutes,Title=excluded.Title,Important=excluded.Important,
                        Deleted=excluded.Deleted,Modified=excluded.Modified
                """
            );
        }
    }
}
