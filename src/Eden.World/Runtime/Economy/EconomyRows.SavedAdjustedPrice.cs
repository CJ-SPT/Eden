using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedAdjustedPrice(
    int TypeId,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal PriceIsk
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedAdjustedPrice(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedAdjustedPrice>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldAdjustedPrice
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldAdjustedPrice.TypeId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldAdjustedPrice(
                        TypeId,PriceIsk
                    )
                    SELECT
                        json_extract(value,'$.TypeId'),json_extract(value,'$.PriceIsk')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(TypeId) DO UPDATE SET
                        PriceIsk=excluded.PriceIsk
                    """
                );
            }
        }
    }
}
