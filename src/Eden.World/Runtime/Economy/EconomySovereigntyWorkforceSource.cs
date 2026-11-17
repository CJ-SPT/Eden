using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyWorkforceSource(long Id, long HubId, int SourceSystemId);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyWorkforceSource() =>
        Register(
            new EconomyTable<SavedSovereigntyWorkforceSource, EconomySovereigntyWorkforceSource>(
                "WorldSovereigntyWorkforceSource",
                row => new(row.Id),
                row => new(row.Id, row.HubId, row.SourceSystemId),
                state => new SavedSovereigntyWorkforceSource
                {
                    Id = state.Id,
                    HubId = state.HubId,
                    SourceSystemId = state.SourceSystemId,
                },
                ProjectSovereigntyWorkforceSource,
                "Id,HubId,SourceSystemId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)),
                state => new(state.Id),
                indexes: [new("HubId", row => row.HubId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectSovereigntyWorkforceSource(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySovereigntyWorkforceSource>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyWorkforceSource WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyWorkforceSource(Id,HubId,SourceSystemId)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.SourceSystemId')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    HubId=excluded.HubId,
                    SourceSystemId=excluded.SourceSystemId
                """
            );
        }
    }
}
