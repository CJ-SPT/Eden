using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySpatialNavigationState(
    long ShipId,
    long CloakModuleId,
    double CloakReactivationMilliseconds,
    double CloakTargetingMilliseconds,
    double HeadingX,
    double HeadingY,
    double HeadingZ,
    long JumpEndsAt,
    long JumpModuleId,
    long JumpStartedAt,
    long TargetingReadyAt,
    double VelocityX,
    double VelocityY,
    double VelocityZ
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSpatialNavigationState(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySpatialNavigationState>> changes
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
                    DELETE FROM WorldSpatialNavigation
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldSpatialNavigation.ShipId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldSpatialNavigation(
                        ShipId,CloakModuleId,CloakReactivationMilliseconds,CloakTargetingMilliseconds,HeadingX,HeadingY,
                        HeadingZ,JumpEndsAt,JumpModuleId,JumpStartedAt,TargetingReadyAt,VelocityX,VelocityY,VelocityZ
                    )
                    SELECT
                        json_extract(value,'$.ShipId'),json_extract(value,'$.CloakModuleId'),
                        json_extract(value,'$.CloakReactivationMilliseconds'),
                        json_extract(value,'$.CloakTargetingMilliseconds'),json_extract(value,'$.HeadingX'),
                        json_extract(value,'$.HeadingY'),json_extract(value,'$.HeadingZ'),
                        json_extract(value,'$.JumpEndsAt'),json_extract(value,'$.JumpModuleId'),
                        json_extract(value,'$.JumpStartedAt'),json_extract(value,'$.TargetingReadyAt'),
                        json_extract(value,'$.VelocityX'),json_extract(value,'$.VelocityY'),
                        json_extract(value,'$.VelocityZ')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET
                        CloakModuleId=excluded.CloakModuleId,
                        CloakReactivationMilliseconds=excluded.CloakReactivationMilliseconds,
                        CloakTargetingMilliseconds=excluded.CloakTargetingMilliseconds,HeadingX=excluded.HeadingX,
                        HeadingY=excluded.HeadingY,HeadingZ=excluded.HeadingZ,JumpEndsAt=excluded.JumpEndsAt,
                        JumpModuleId=excluded.JumpModuleId,JumpStartedAt=excluded.JumpStartedAt,
                        TargetingReadyAt=excluded.TargetingReadyAt,VelocityX=excluded.VelocityX,
                        VelocityY=excluded.VelocityY,VelocityZ=excluded.VelocityZ
                    """
                );
            }
        }
    }
}
