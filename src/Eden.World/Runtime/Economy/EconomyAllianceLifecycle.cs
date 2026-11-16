using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAllianceLifecycle(int AllianceId, long? ClosureAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterAllianceLifecycle() =>
        Register(
            new EconomyTable<SavedAllianceLifecycle, EconomyAllianceLifecycle>(
                "WorldAllianceLifecycle",
                row => new(row.AllianceId),
                row => new(row.AllianceId, row.ClosureAt),
                state => new SavedAllianceLifecycle { AllianceId = state.AllianceId, ClosureAt = state.ClosureAt },
                ProjectAllianceLifecycle,
                "AllianceId,ClosureAt",
                reader => new(reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)),
                state => new(state.AllianceId),
                indexes: [],
                primaryColumn: "AllianceId"
            )
        );

    private static void ProjectAllianceLifecycle(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAllianceLifecycle>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAllianceLifecycle WHERE AllianceId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAllianceLifecycle(AllianceId,ClosureAt)
                SELECT
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.ClosureAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(AllianceId) DO UPDATE SET
                    ClosureAt=excluded.ClosureAt
                """
            );
        }
    }
}
