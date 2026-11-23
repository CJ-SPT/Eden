using System.Text.Json;
using Eden.Data.World.Docking;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record EconomyShipSkinLicense(long PilotId, int SkinId, long? Expires, bool IsSingleUse);

    private void RegisterShipSkinLicenses() =>
        Register(
            new EconomyTable<SavedShipSkinLicense, EconomyShipSkinLicense>(
                "WorldShipSkinLicense",
                row => new(row.PilotId, row.SkinId),
                row => new(row.PilotId, row.SkinId, row.Expires, row.IsSingleUse),
                state => new SavedShipSkinLicense
                {
                    PilotId = state.PilotId,
                    SkinId = state.SkinId,
                    Expires = state.Expires,
                    IsSingleUse = state.IsSingleUse,
                },
                ProjectShipSkinLicenses,
                "PilotId,SkinId,Expires,IsSingleUse",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetBoolean(4)),
                state => new(state.PilotId, state.SkinId),
                indexes: [new(nameof(SavedShipSkinLicense.PilotId), state => state.PilotId)],
                primaryColumn: null
            )
        );

    private static void ProjectShipSkinLicenses(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyShipSkinLicense>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldShipSkinLicense WHERE EXISTS (
                    SELECT 1 FROM json_each({removed})
                    WHERE PilotId=json_extract(value,'$.Id') AND SkinId=json_extract(value,'$.Part'))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShipSkinLicense(PilotId,SkinId,Expires,IsSingleUse)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.SkinId'),
                    json_extract(value,'$.Expires'),json_extract(value,'$.IsSingleUse')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId,SkinId) DO UPDATE SET Expires=excluded.Expires,IsSingleUse=excluded.IsSingleUse
                """
            );
        }
    }
}
