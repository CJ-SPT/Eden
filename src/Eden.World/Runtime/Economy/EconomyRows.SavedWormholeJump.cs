using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedWormholeJump(long PilotId, long EntranceId, long JumpedAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterWormholeJumps() =>
        Register(
            new EconomyTable<SavedWormholeJump, EconomySavedWormholeJump>(
                "WorldWormholeJump",
                row => new(row.PilotId, row.EntranceId),
                row => new(row.PilotId, row.EntranceId, row.JumpedAt),
                state => new SavedWormholeJump
                {
                    PilotId = state.PilotId,
                    EntranceId = state.EntranceId,
                    JumpedAt = state.JumpedAt,
                },
                ProjectWormholeJumps,
                "PilotId,EntranceId,JumpedAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.PilotId, state.EntranceId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );

    private static void ProjectWormholeJumps(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedWormholeJump>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key).ToArray();
            if (removed.Length != 0)
            {
                string keys = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldWormholeJump
                    WHERE EXISTS (SELECT 1 FROM json_each({keys}) WHERE
                        WorldWormholeJump.PilotId=json_extract(value,'$.Id') AND
                        WorldWormholeJump.EntranceId=json_extract(value,'$.Part'))
                    """
                );
            }
            var rows = batch.Where(change => change.Value is not null).Select(change => change.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldWormholeJump(PilotId,EntranceId,JumpedAt)
                    SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.EntranceId'),json_extract(value,'$.JumpedAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId,EntranceId) DO UPDATE SET JumpedAt=excluded.JumpedAt
                    """
                );
            }
        }
    }
}
