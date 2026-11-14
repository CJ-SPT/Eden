using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedDedGateUnlock(long SiteId, long GateObjectId, long PilotId, long ResetAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterDedGateUnlock()
    {
        Register(
            new EconomyTable<SavedDedGateUnlock, EconomySavedDedGateUnlock>(
                "WorldDedGateUnlock",
                row => new(row.SiteId, row.GateObjectId, row.PilotId.ToString(CultureInfo.InvariantCulture)),
                row => new(row.SiteId, row.GateObjectId, row.PilotId, row.ResetAt),
                state => new SavedDedGateUnlock
                {
                    SiteId = state.SiteId,
                    GateObjectId = state.GateObjectId,
                    PilotId = state.PilotId,
                    ResetAt = state.ResetAt,
                },
                ProjectSavedDedGateUnlock,
                "SiteId,GateObjectId,PilotId,ResetAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                state => new(state.SiteId, state.GateObjectId, state.PilotId.ToString(CultureInfo.InvariantCulture)),
                indexes: [new("SiteId", row => row.SiteId), new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectSavedDedGateUnlock(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedDedGateUnlock>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldDedGateUnlock WHERE EXISTS (
                        SELECT 1 FROM json_each({deleted})
                        WHERE SiteId=json_extract(value,'$.Id') AND GateObjectId=json_extract(value,'$.Part')
                            AND PilotId=CAST(json_extract(value,'$.Name') AS INTEGER))
                    """
                );
            }
            var rows = batch.Where(change => change.Value is not null).Select(change => change.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldDedGateUnlock(SiteId,GateObjectId,PilotId,ResetAt)
                    SELECT json_extract(value,'$.SiteId'),json_extract(value,'$.GateObjectId'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.ResetAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(SiteId,GateObjectId,PilotId) DO UPDATE SET ResetAt=excluded.ResetAt
                    """
                );
            }
        }
    }
}
