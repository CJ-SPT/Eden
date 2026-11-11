using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldAsteroid(
    long Id,
    int BeltId,
    long Quantity,
    float Radius,
    int SystemId,
    int TypeId,
    double X,
    double Y,
    double Z
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldAsteroid(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldAsteroid>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldAsteroid
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldAsteroid.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldAsteroid(
                        Id,BeltId,Quantity,Radius,SystemId,TypeId,X,Y,Z
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.BeltId'),json_extract(value,'$.Quantity'),
                        json_extract(value,'$.Radius'),json_extract(value,'$.SystemId'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.X'),json_extract(value,'$.Y'),json_extract(value,'$.Z')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        BeltId=excluded.BeltId,Quantity=excluded.Quantity,Radius=excluded.Radius,SystemId=excluded.SystemId,
                        TypeId=excluded.TypeId,X=excluded.X,Y=excluded.Y,Z=excluded.Z
                    """
                );
            }
        }
    }
}
