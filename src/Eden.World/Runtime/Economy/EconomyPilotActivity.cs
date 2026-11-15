using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyPilotActivity(long PilotId, long? LoggedOnAt, long? LoggedOffAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterPilotActivity() =>
        Register(
            new EconomyTable<SavedPilotActivity, EconomyPilotActivity>(
                "WorldPilotActivity",
                row => new(row.PilotId),
                row => new(row.PilotId, row.LoggedOnAt, row.LoggedOffAt),
                state => new SavedPilotActivity
                {
                    PilotId = state.PilotId,
                    LoggedOnAt = state.LoggedOnAt,
                    LoggedOffAt = state.LoggedOffAt,
                },
                ProjectPilotActivity,
                "PilotId,LoggedOnAt,LoggedOffAt",
                reader =>
                    new(reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)),
                state => new(state.PilotId),
                primaryColumn: "PilotId"
            )
        );

    private static void ProjectPilotActivity(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyPilotActivity>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldPilotActivity WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilotActivity(PilotId,LoggedOnAt,LoggedOffAt)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.LoggedOnAt'),json_extract(value,'$.LoggedOffAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET LoggedOnAt=excluded.LoggedOnAt,LoggedOffAt=excluded.LoggedOffAt
                """
            );
        }
    }
}
