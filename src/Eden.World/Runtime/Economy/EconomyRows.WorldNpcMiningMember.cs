using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldNpcMiningMember(
    long NpcId,
    long? AsteroidId,
    double Capacitor,
    long CapacitorAt,
    long EncounterId,
    long? MiningDueAt,
    long? MiningStartedAt,
    Eden.Gameplay.Mining.NpcActivityKind SupportKind,
    long SupportReadyAt,
    long SupportStartedAt,
    long? SupportTargetId,
    Eden.Gameplay.Mining.NpcMiningTravel Travel,
    long? WarpArrivesAt,
    double WarpOriginX,
    double WarpOriginY,
    double WarpOriginZ,
    double WarpSpeed,
    long? WarpStartedAt
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldNpcMiningMember(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldNpcMiningMember>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNpcMiningMember
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpcMiningMember.NpcId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldNpcMiningMember(
                        NpcId,AsteroidId,Capacitor,CapacitorAt,EncounterId,MiningDueAt,MiningStartedAt,SupportKind,
                        SupportReadyAt,SupportStartedAt,SupportTargetId,Travel,WarpArrivesAt,WarpOriginX,WarpOriginY,
                        WarpOriginZ,WarpSpeed,WarpStartedAt
                    )
                    SELECT
                        json_extract(value,'$.NpcId'),json_extract(value,'$.AsteroidId'),json_extract(value,'$.Capacitor'),
                        json_extract(value,'$.CapacitorAt'),json_extract(value,'$.EncounterId'),
                        json_extract(value,'$.MiningDueAt'),json_extract(value,'$.MiningStartedAt'),
                        json_extract(value,'$.SupportKind'),json_extract(value,'$.SupportReadyAt'),
                        json_extract(value,'$.SupportStartedAt'),json_extract(value,'$.SupportTargetId'),
                        json_extract(value,'$.Travel'),json_extract(value,'$.WarpArrivesAt'),
                        json_extract(value,'$.WarpOriginX'),json_extract(value,'$.WarpOriginY'),
                        json_extract(value,'$.WarpOriginZ'),json_extract(value,'$.WarpSpeed'),
                        json_extract(value,'$.WarpStartedAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(NpcId) DO UPDATE SET
                        AsteroidId=excluded.AsteroidId,Capacitor=excluded.Capacitor,CapacitorAt=excluded.CapacitorAt,
                        EncounterId=excluded.EncounterId,MiningDueAt=excluded.MiningDueAt,
                        MiningStartedAt=excluded.MiningStartedAt,SupportKind=excluded.SupportKind,
                        SupportReadyAt=excluded.SupportReadyAt,SupportStartedAt=excluded.SupportStartedAt,
                        SupportTargetId=excluded.SupportTargetId,Travel=excluded.Travel,
                        WarpArrivesAt=excluded.WarpArrivesAt,WarpOriginX=excluded.WarpOriginX,
                        WarpOriginY=excluded.WarpOriginY,WarpOriginZ=excluded.WarpOriginZ,WarpSpeed=excluded.WarpSpeed,
                        WarpStartedAt=excluded.WarpStartedAt
                    """
                );
            }
        }
    }
}
