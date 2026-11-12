using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedInsurancePolicy(
    long Id,
    long EndTime,
    Eden.Gameplay.Insurance.InsuranceLevel Level,
    long PilotId,
    long PremiumCents,
    long ShipId,
    long StartTime,
    Eden.Gameplay.Insurance.InsurancePolicyStatus Status,
    int TypeId,
    long ValueCents
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedInsurancePolicy(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedInsurancePolicy>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldInsurancePolicy
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldInsurancePolicy.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldInsurancePolicy(
                        Id,EndTime,Level,PilotId,PremiumCents,ShipId,StartTime,Status,TypeId,ValueCents
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.EndTime'),json_extract(value,'$.Level'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.PremiumCents'),json_extract(value,'$.ShipId'),
                        json_extract(value,'$.StartTime'),json_extract(value,'$.Status'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.ValueCents')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        EndTime=excluded.EndTime,Level=excluded.Level,PilotId=excluded.PilotId,
                        PremiumCents=excluded.PremiumCents,ShipId=excluded.ShipId,StartTime=excluded.StartTime,
                        Status=excluded.Status,TypeId=excluded.TypeId,ValueCents=excluded.ValueCents
                    """
                );
            }
        }
    }
}
