using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerHauler(
    int Id,
    int DestinationStationId,
    long FounderShipId,
    long PilotId,
    string? Progress,
    System.Int32[]? RouteSystems,
    int SourceStationId,
    long StartingCents
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerHauler(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCapsuleerHauler>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCapsuleerHauler
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerHauler.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerHauler(
                        Id,DestinationStationId,FounderShipId,PilotId,Progress,RouteSystems,SourceStationId,StartingCents
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.DestinationStationId'),
                        json_extract(value,'$.FounderShipId'),json_extract(value,'$.PilotId'),
                        json_extract(value,'$.Progress'),json_extract(value,'$.RouteSystems'),
                        json_extract(value,'$.SourceStationId'),json_extract(value,'$.StartingCents')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        DestinationStationId=excluded.DestinationStationId,FounderShipId=excluded.FounderShipId,
                        PilotId=excluded.PilotId,Progress=excluded.Progress,RouteSystems=excluded.RouteSystems,
                        SourceStationId=excluded.SourceStationId,StartingCents=excluded.StartingCents
                    """
                );
            }
        }
    }
}
