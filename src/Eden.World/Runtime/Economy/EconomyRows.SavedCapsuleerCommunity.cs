using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerCommunity(
    int Id,
    Eden.Gameplay.Capsuleers.CapsuleerCareer Career,
    long FounderModuleId,
    long FounderShipId,
    int HomeStationId,
    long? PilotId,
    string? Progress,
    int SchoolId,
    int Seed,
    long StartingCents
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerCommunity(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedCapsuleerCommunity>> changes
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
                    DELETE FROM WorldCapsuleerCommunity
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerCommunity.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerCommunity(
                        Id,Career,FounderModuleId,FounderShipId,HomeStationId,PilotId,Progress,SchoolId,Seed,StartingCents
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.Career'),json_extract(value,'$.FounderModuleId'),
                        json_extract(value,'$.FounderShipId'),json_extract(value,'$.HomeStationId'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.Progress'),json_extract(value,'$.SchoolId'),
                        json_extract(value,'$.Seed'),json_extract(value,'$.StartingCents')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        Career=excluded.Career,FounderModuleId=excluded.FounderModuleId,
                        FounderShipId=excluded.FounderShipId,HomeStationId=excluded.HomeStationId,PilotId=excluded.PilotId,
                        Progress=excluded.Progress,SchoolId=excluded.SchoolId,Seed=excluded.Seed,
                        StartingCents=excluded.StartingCents
                    """
                );
            }
        }
    }
}
