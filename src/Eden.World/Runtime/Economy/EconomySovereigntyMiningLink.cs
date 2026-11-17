using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyMiningLink(long SiteId, long HubId, int TypeId);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyMiningLink() =>
        Register(
            new EconomyTable<SavedSovereigntyMiningLink, EconomySovereigntyMiningLink>(
                "WorldSovereigntyMiningLink",
                row => new(row.SiteId),
                row => new(row.SiteId, row.HubId, row.TypeId),
                state => new SavedSovereigntyMiningLink
                {
                    SiteId = state.SiteId,
                    HubId = state.HubId,
                    TypeId = state.TypeId,
                },
                ProjectSovereigntyMiningLink,
                "SiteId,HubId,TypeId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)),
                state => new(state.SiteId),
                indexes: [new("HubId", row => row.HubId)],
                primaryColumn: "SiteId"
            )
        );

    private static void ProjectSovereigntyMiningLink(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySovereigntyMiningLink>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyMiningLink WHERE SiteId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyMiningLink(SiteId,HubId,TypeId)
                SELECT
                    json_extract(value,'$.SiteId'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.TypeId')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SiteId) DO UPDATE SET
                    HubId=excluded.HubId,
                    TypeId=excluded.TypeId
                """
            );
        }
    }
}
