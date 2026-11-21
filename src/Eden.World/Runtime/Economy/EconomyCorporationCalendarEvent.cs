using System.Text.Json;
using Eden.Data.World.Calendar;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyCorporationCalendarEvent(long Id, int CorporationId, string DataJson);

internal sealed partial class ResidentEconomy
{
    private void RegisterCorporationCalendar() =>
        Register(
            new EconomyTable<SavedCorporationCalendarEvent, EconomyCorporationCalendarEvent>(
                "WorldCorporationCalendarEvent",
                row => new(row.Id),
                row => new(row.Id, row.CorporationId, row.DataJson),
                state => new SavedCorporationCalendarEvent
                {
                    Id = state.Id,
                    CorporationId = state.CorporationId,
                    DataJson = state.DataJson,
                },
                ProjectCorporationCalendar,
                "Id,CorporationId,DataJson",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)),
                state => new(state.Id),
                indexes: [new("CorporationId", row => row.CorporationId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectCorporationCalendar(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyCorporationCalendarEvent>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationCalendarEvent WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationCalendarEvent(Id,CorporationId,DataJson)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.DataJson')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    CorporationId=excluded.CorporationId,
                    DataJson=excluded.DataJson
                """
            );
        }
    }
}
