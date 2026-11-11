using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldMissile(
    long Id,
    int ChargeTypeId,
    int CorporationId,
    long? EndedAt,
    long ExpiresAt,
    int GroupId,
    int LaunchStamp,
    long LaunchedAt,
    int LauncherTypeId,
    long ModuleId,
    string? Outcome,
    long PilotId,
    bool Reported,
    string Snapshot,
    Eden.Gameplay.Combat.MissileSourceKind SourceKind,
    long SourceShipId,
    int SourceTypeId,
    Eden.Gameplay.Combat.MissileFlightStatus Status,
    int SystemId,
    long TargetId,
    Eden.Gameplay.Combat.MissileTargetKind TargetKind,
    float TargetRadius,
    long UpdatedAt
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldMissile(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldMissile>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMissile
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldMissile.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldMissile(
                        Id,ChargeTypeId,CorporationId,EndedAt,ExpiresAt,GroupId,LaunchStamp,LaunchedAt,LauncherTypeId,
                        ModuleId,Outcome,PilotId,Reported,Snapshot,SourceKind,SourceShipId,SourceTypeId,Status,SystemId,
                        TargetId,TargetKind,TargetRadius,UpdatedAt
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.ChargeTypeId'),
                        json_extract(value,'$.CorporationId'),json_extract(value,'$.EndedAt'),
                        json_extract(value,'$.ExpiresAt'),json_extract(value,'$.GroupId'),
                        json_extract(value,'$.LaunchStamp'),json_extract(value,'$.LaunchedAt'),
                        json_extract(value,'$.LauncherTypeId'),json_extract(value,'$.ModuleId'),
                        json_extract(value,'$.Outcome'),json_extract(value,'$.PilotId'),json_extract(value,'$.Reported'),
                        json_extract(value,'$.Snapshot'),json_extract(value,'$.SourceKind'),
                        json_extract(value,'$.SourceShipId'),json_extract(value,'$.SourceTypeId'),
                        json_extract(value,'$.Status'),json_extract(value,'$.SystemId'),json_extract(value,'$.TargetId'),
                        json_extract(value,'$.TargetKind'),json_extract(value,'$.TargetRadius'),
                        json_extract(value,'$.UpdatedAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        ChargeTypeId=excluded.ChargeTypeId,CorporationId=excluded.CorporationId,EndedAt=excluded.EndedAt,
                        ExpiresAt=excluded.ExpiresAt,GroupId=excluded.GroupId,LaunchStamp=excluded.LaunchStamp,
                        LaunchedAt=excluded.LaunchedAt,LauncherTypeId=excluded.LauncherTypeId,ModuleId=excluded.ModuleId,
                        Outcome=excluded.Outcome,PilotId=excluded.PilotId,Reported=excluded.Reported,
                        Snapshot=excluded.Snapshot,SourceKind=excluded.SourceKind,SourceShipId=excluded.SourceShipId,
                        SourceTypeId=excluded.SourceTypeId,Status=excluded.Status,SystemId=excluded.SystemId,
                        TargetId=excluded.TargetId,TargetKind=excluded.TargetKind,TargetRadius=excluded.TargetRadius,
                        UpdatedAt=excluded.UpdatedAt
                    """
                );
            }
        }
    }
}
