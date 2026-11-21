using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyShipSharing(long ShipId, bool FleetHangarFleet, bool FleetHangarCorporation);

internal sealed partial class ResidentEconomy
{
    private void RegisterShipSharing() =>
        Register(
            new EconomyTable<SavedShipSharing, EconomyShipSharing>(
                "WorldShipSharing",
                row => new(row.ShipId),
                row => new(row.ShipId, row.FleetHangarFleet, row.FleetHangarCorporation),
                state => new SavedShipSharing
                {
                    ShipId = state.ShipId,
                    FleetHangarFleet = state.FleetHangarFleet,
                    FleetHangarCorporation = state.FleetHangarCorporation,
                },
                ProjectShipSharing,
                "ShipId,FleetHangarFleet,FleetHangarCorporation",
                reader => new(reader.GetInt64(1), reader.GetBoolean(2), reader.GetBoolean(3)),
                state => new(state.ShipId),
                primaryColumn: "ShipId"
            )
        );

    private static void ProjectShipSharing(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyShipSharing>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key.Id).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql($"DELETE FROM WorldShipSharing WHERE ShipId IN (SELECT value FROM json_each({deleted}))");
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldShipSharing(ShipId,FleetHangarFleet,FleetHangarCorporation)
                    SELECT json_extract(value,'$.ShipId'),json_extract(value,'$.FleetHangarFleet'),
                        json_extract(value,'$.FleetHangarCorporation') FROM json_each({values}) WHERE 1
                    ON CONFLICT(ShipId) DO UPDATE SET FleetHangarFleet=excluded.FleetHangarFleet,
                        FleetHangarCorporation=excluded.FleetHangarCorporation
                    """
                );
            }
        }
    }
}
