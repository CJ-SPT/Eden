using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedEconomicPolicy(
    int Id,
    bool ChargeServiceCosts,
    System.Decimal DefaultManufacturingIndex,
    bool RenewBelts,
    int RenewalHourUtc
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedEconomicPolicy(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedEconomicPolicy>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldEconomicPolicy
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldEconomicPolicy.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldEconomicPolicy(
                        Id,ChargeServiceCosts,DefaultManufacturingIndex,RenewBelts,RenewalHourUtc
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.ChargeServiceCosts'),
                        json_extract(value,'$.DefaultManufacturingIndex'),json_extract(value,'$.RenewBelts'),
                        json_extract(value,'$.RenewalHourUtc')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        ChargeServiceCosts=excluded.ChargeServiceCosts,
                        DefaultManufacturingIndex=excluded.DefaultManufacturingIndex,RenewBelts=excluded.RenewBelts,
                        RenewalHourUtc=excluded.RenewalHourUtc
                    """
                );
            }
        }
    }
}
