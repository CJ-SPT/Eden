using System.Text.Json;
using Eden.Data.World.Docking;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record EconomyAppliedShipSkin(long ShipId, long PilotId, int SkinId, long? Expires);

    private void RegisterAppliedShipSkins() =>
        Register(
            new EconomyTable<SavedAppliedShipSkin, EconomyAppliedShipSkin>(
                "WorldAppliedShipSkin",
                row => new(row.ShipId),
                row => new(row.ShipId, row.PilotId, row.SkinId, row.Expires),
                state => new SavedAppliedShipSkin
                {
                    ShipId = state.ShipId,
                    PilotId = state.PilotId,
                    SkinId = state.SkinId,
                    Expires = state.Expires,
                },
                ProjectAppliedShipSkins,
                "ShipId,PilotId,SkinId,Expires",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)),
                state => new(state.ShipId),
                primaryColumn: "ShipId"
            )
        );

    private static void ProjectAppliedShipSkins(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAppliedShipSkin>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAppliedShipSkin WHERE ShipId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAppliedShipSkin(ShipId,PilotId,SkinId,Expires)
                SELECT json_extract(value,'$.ShipId'),json_extract(value,'$.PilotId'),
                    json_extract(value,'$.SkinId'),json_extract(value,'$.Expires')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(ShipId) DO UPDATE SET PilotId=excluded.PilotId,SkinId=excluded.SkinId,Expires=excluded.Expires
                """
            );
        }
    }
}
