using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedStandingTransaction(
    long Id,
    double After,
    double Before,
    Eden.Gameplay.Characters.StandingEvent Event,
    long PilotId,
    long SourceId,
    int SystemId,
    long Time,
    int VictimCorporationId,
    int VictimTypeId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedStandingTransaction(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedStandingTransaction>> changes
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
                    DELETE FROM WorldStandingTransaction
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldStandingTransaction.Id=json_extract(value,'$.Id')
                    )
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldStandingTransaction(
                        Id,After,Before,Event,PilotId,SourceId,SystemId,Time,VictimCorporationId,VictimTypeId
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.After'),json_extract(value,'$.Before'),
                        json_extract(value,'$.Event'),json_extract(value,'$.PilotId'),json_extract(value,'$.SourceId'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.Time'),
                        json_extract(value,'$.VictimCorporationId'),json_extract(value,'$.VictimTypeId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        After=excluded.After,Before=excluded.Before,Event=excluded.Event,PilotId=excluded.PilotId,
                        SourceId=excluded.SourceId,SystemId=excluded.SystemId,Time=excluded.Time,
                        VictimCorporationId=excluded.VictimCorporationId,VictimTypeId=excluded.VictimTypeId
                    """
                );
            }
        }
    }
}
