using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyShipLoss(
    long ShipId,
    long? AttackerPilotId,
    long CapsuleId,
    string DroppedItemIdsJson,
    string ItemsJson,
    string Name,
    long? NpcId,
    long PilotId,
    string StoredShipsJson,
    int SystemId,
    long Time,
    int TypeId,
    double X,
    double Y,
    double Z
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectShipLoss(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyShipLoss>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldShipLoss
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldShipLoss.ShipId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldShipLoss(
                        ShipId,AttackerPilotId,CapsuleId,DroppedItemIdsJson,ItemsJson,Name,NpcId,PilotId,StoredShipsJson,
                        SystemId,Time,TypeId,X,Y,Z
                    )
                    SELECT
                        json_extract(value,'$.ShipId'),json_extract(value,'$.AttackerPilotId'),
                        json_extract(value,'$.CapsuleId'),json_extract(value,'$.DroppedItemIdsJson'),
                        json_extract(value,'$.ItemsJson'),json_extract(value,'$.Name'),json_extract(value,'$.NpcId'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.StoredShipsJson'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.Time'),json_extract(value,'$.TypeId'),
                        json_extract(value,'$.X'),json_extract(value,'$.Y'),json_extract(value,'$.Z')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET
                        AttackerPilotId=excluded.AttackerPilotId,CapsuleId=excluded.CapsuleId,
                        DroppedItemIdsJson=excluded.DroppedItemIdsJson,ItemsJson=excluded.ItemsJson,Name=excluded.Name,
                        NpcId=excluded.NpcId,PilotId=excluded.PilotId,StoredShipsJson=excluded.StoredShipsJson,
                        SystemId=excluded.SystemId,Time=excluded.Time,TypeId=excluded.TypeId,X=excluded.X,Y=excluded.Y,
                        Z=excluded.Z
                    """
                );
            }
        }
    }
}
