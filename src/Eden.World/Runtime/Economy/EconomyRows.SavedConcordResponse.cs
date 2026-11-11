using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedConcordResponse(
    long ShipId,
    long? CommanderId,
    long? CompletedAt,
    long DestroyAt,
    long? FirstCaptainId,
    long PilotId,
    long RespondAt,
    long? SecondCaptainId,
    int SystemId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedConcordResponse(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedConcordResponse>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldConcordResponse
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldConcordResponse.ShipId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldConcordResponse(
                        ShipId,CommanderId,CompletedAt,DestroyAt,FirstCaptainId,PilotId,RespondAt,SecondCaptainId,SystemId
                    )
                    SELECT
                        json_extract(value,'$.ShipId'),json_extract(value,'$.CommanderId'),
                        json_extract(value,'$.CompletedAt'),json_extract(value,'$.DestroyAt'),
                        json_extract(value,'$.FirstCaptainId'),json_extract(value,'$.PilotId'),
                        json_extract(value,'$.RespondAt'),json_extract(value,'$.SecondCaptainId'),
                        json_extract(value,'$.SystemId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET
                        CommanderId=excluded.CommanderId,CompletedAt=excluded.CompletedAt,DestroyAt=excluded.DestroyAt,
                        FirstCaptainId=excluded.FirstCaptainId,PilotId=excluded.PilotId,RespondAt=excluded.RespondAt,
                        SecondCaptainId=excluded.SecondCaptainId,SystemId=excluded.SystemId
                    """
                );
            }
        }
    }
}
