using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerSystem(
    int SystemId,
    string CatalogFingerprint,
    int? DestinationStationId,
    Eden.Gameplay.Capsuleers.CapsuleerEligibility Eligibility,
    int? HomeStationId,
    int PolicyVersion
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerSystem(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCapsuleerSystem>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCapsuleerSystem
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerSystem.SystemId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerSystem(
                        SystemId,CatalogFingerprint,DestinationStationId,Eligibility,HomeStationId,PolicyVersion
                    )
                    SELECT
                        json_extract(value,'$.SystemId'),json_extract(value,'$.CatalogFingerprint'),
                        json_extract(value,'$.DestinationStationId'),json_extract(value,'$.Eligibility'),
                        json_extract(value,'$.HomeStationId'),json_extract(value,'$.PolicyVersion')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(SystemId) DO UPDATE SET
                        CatalogFingerprint=excluded.CatalogFingerprint,DestinationStationId=excluded.DestinationStationId,
                        Eligibility=excluded.Eligibility,HomeStationId=excluded.HomeStationId,
                        PolicyVersion=excluded.PolicyVersion
                    """
                );
            }
        }
    }
}
