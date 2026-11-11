using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedStructure(
    long Id,
    int CorporationId,
    long DeployedAt,
    string Description,
    bool HasCore,
    long? LowPowerAt,
    int? MoonId,
    string Name,
    bool PublicAccess,
    int ReinforceHour,
    int ReinforceWeekday,
    Eden.Gameplay.Structures.StructureState State,
    long? StateEndsAt,
    long StateStartedAt,
    int SystemId,
    int TypeId,
    long? UnanchorAt,
    double X,
    double Y,
    double Yaw,
    double Z
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedStructure(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedStructure>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldStructure
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldStructure.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldStructure(
                        Id,CorporationId,DeployedAt,Description,HasCore,LowPowerAt,MoonId,Name,PublicAccess,ReinforceHour,
                        ReinforceWeekday,State,StateEndsAt,StateStartedAt,SystemId,TypeId,UnanchorAt,X,Y,Yaw,Z
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.CorporationId'),json_extract(value,'$.DeployedAt'),
                        json_extract(value,'$.Description'),json_extract(value,'$.HasCore'),
                        json_extract(value,'$.LowPowerAt'),json_extract(value,'$.MoonId'),json_extract(value,'$.Name'),
                        json_extract(value,'$.PublicAccess'),json_extract(value,'$.ReinforceHour'),
                        json_extract(value,'$.ReinforceWeekday'),json_extract(value,'$.State'),
                        json_extract(value,'$.StateEndsAt'),json_extract(value,'$.StateStartedAt'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.TypeId'),json_extract(value,'$.UnanchorAt'),
                        json_extract(value,'$.X'),json_extract(value,'$.Y'),json_extract(value,'$.Yaw'),
                        json_extract(value,'$.Z')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        CorporationId=excluded.CorporationId,DeployedAt=excluded.DeployedAt,
                        Description=excluded.Description,HasCore=excluded.HasCore,LowPowerAt=excluded.LowPowerAt,
                        MoonId=excluded.MoonId,Name=excluded.Name,PublicAccess=excluded.PublicAccess,
                        ReinforceHour=excluded.ReinforceHour,ReinforceWeekday=excluded.ReinforceWeekday,
                        State=excluded.State,StateEndsAt=excluded.StateEndsAt,StateStartedAt=excluded.StateStartedAt,
                        SystemId=excluded.SystemId,TypeId=excluded.TypeId,UnanchorAt=excluded.UnanchorAt,X=excluded.X,
                        Y=excluded.Y,Yaw=excluded.Yaw,Z=excluded.Z
                    """
                );
            }
        }
    }
}
