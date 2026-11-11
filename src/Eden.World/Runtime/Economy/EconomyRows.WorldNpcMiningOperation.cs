using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldNpcMiningOperation(
    long EncounterId,
    double AlertX,
    double AlertY,
    double AlertZ,
    int BeltId,
    int CorporationId,
    int FactionId,
    int HaulGeneration,
    long PickupAt,
    long? ResetAt,
    long? ResponseAt,
    Eden.Gameplay.Mining.NpcMiningStage Stage,
    int? StationId,
    int SystemId,
    long? ThreatShipId
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldNpcMiningOperation(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyWorldNpcMiningOperation>> changes
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
                    DELETE FROM WorldNpcMiningOperation
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpcMiningOperation.EncounterId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldNpcMiningOperation(
                        EncounterId,AlertX,AlertY,AlertZ,BeltId,CorporationId,FactionId,HaulGeneration,PickupAt,ResetAt,
                        ResponseAt,Stage,StationId,SystemId,ThreatShipId
                    )
                    SELECT
                        json_extract(value,'$.EncounterId'),json_extract(value,'$.AlertX'),json_extract(value,'$.AlertY'),
                        json_extract(value,'$.AlertZ'),json_extract(value,'$.BeltId'),json_extract(value,'$.CorporationId'),
                        json_extract(value,'$.FactionId'),json_extract(value,'$.HaulGeneration'),
                        json_extract(value,'$.PickupAt'),json_extract(value,'$.ResetAt'),json_extract(value,'$.ResponseAt'),
                        json_extract(value,'$.Stage'),json_extract(value,'$.StationId'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.ThreatShipId')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(EncounterId) DO UPDATE SET
                        AlertX=excluded.AlertX,AlertY=excluded.AlertY,AlertZ=excluded.AlertZ,BeltId=excluded.BeltId,
                        CorporationId=excluded.CorporationId,FactionId=excluded.FactionId,
                        HaulGeneration=excluded.HaulGeneration,PickupAt=excluded.PickupAt,ResetAt=excluded.ResetAt,
                        ResponseAt=excluded.ResponseAt,Stage=excluded.Stage,StationId=excluded.StationId,
                        SystemId=excluded.SystemId,ThreatShipId=excluded.ThreatShipId
                    """
                );
            }
        }
    }
}
