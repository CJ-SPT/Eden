using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldWreck(
    long Id,
    int CorporationId,
    long CreatedAt,
    long ExpiresAt,
    string Name,
    long? NpcId,
    long OwnerId,
    string? SalvageJson,
    long? SalvagedAt,
    long? ShipId,
    int SystemId,
    int TypeId,
    double X,
    double Y,
    double Z
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldWreck(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldWreck>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldWreck
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldWreck.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldWreck(
                        Id,CorporationId,CreatedAt,ExpiresAt,Name,NpcId,OwnerId,SalvageJson,SalvagedAt,ShipId,SystemId,
                        TypeId,X,Y,Z
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.CorporationId'),json_extract(value,'$.CreatedAt'),
                        json_extract(value,'$.ExpiresAt'),json_extract(value,'$.Name'),json_extract(value,'$.NpcId'),
                        json_extract(value,'$.OwnerId'),json_extract(value,'$.SalvageJson'),
                        json_extract(value,'$.SalvagedAt'),json_extract(value,'$.ShipId'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.TypeId'),json_extract(value,'$.X'),json_extract(value,'$.Y'),
                        json_extract(value,'$.Z')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        CorporationId=excluded.CorporationId,CreatedAt=excluded.CreatedAt,ExpiresAt=excluded.ExpiresAt,
                        Name=excluded.Name,NpcId=excluded.NpcId,OwnerId=excluded.OwnerId,SalvageJson=excluded.SalvageJson,
                        SalvagedAt=excluded.SalvagedAt,ShipId=excluded.ShipId,SystemId=excluded.SystemId,
                        TypeId=excluded.TypeId,X=excluded.X,Y=excluded.Y,Z=excluded.Z
                    """
                );
            }
        }
    }
}
