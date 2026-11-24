using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record Campaign(long TaleId, int Clears, long LastUpdated);

    private sealed record Contribution(long SiteId, long PilotId, long TaleId, double Damage, long PendingLoyalty);

    private void RegisterIncursions()
    {
        Register(
            new EconomyTable<SavedIncursionCampaign, Campaign>(
                IncursionSchema.CampaignTable,
                row => new(row.TaleId),
                row => new(row.TaleId, row.Clears, row.LastUpdated),
                row => new SavedIncursionCampaign
                {
                    TaleId = row.TaleId,
                    Clears = row.Clears,
                    LastUpdated = row.LastUpdated,
                },
                ProjectCampaigns,
                "TaleId,Clears,LastUpdated",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3)),
                row => new(row.TaleId),
                primaryColumn: "TaleId"
            )
        );
        Register(
            new EconomyTable<SavedIncursionContribution, Contribution>(
                IncursionSchema.ContributionTable,
                row => new(row.SiteId, row.PilotId),
                row => new(row.SiteId, row.PilotId, row.TaleId, row.Damage, row.PendingLoyalty),
                row => new SavedIncursionContribution
                {
                    SiteId = row.SiteId,
                    PilotId = row.PilotId,
                    TaleId = row.TaleId,
                    Damage = row.Damage,
                    PendingLoyalty = row.PendingLoyalty,
                },
                ProjectContributions,
                "SiteId,PilotId,TaleId,Damage,PendingLoyalty",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetInt64(5)),
                row => new(row.SiteId, row.PilotId),
                indexes: [new("SiteId", row => row.SiteId), new("TaleId", row => row.TaleId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectCampaigns(WorldDbContext world, IReadOnlyList<EconomyChange<Campaign>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldIncursionCampaign WHERE TaleId IN (SELECT value FROM json_each({deleted}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldIncursionCampaign(TaleId,Clears,LastUpdated)
                SELECT value->>'$.TaleId',value->>'$.Clears',value->>'$.LastUpdated'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(TaleId) DO UPDATE SET Clears=excluded.Clears,LastUpdated=excluded.LastUpdated
                """
            );
        }
    }

    private static void ProjectContributions(WorldDbContext world, IReadOnlyList<EconomyChange<Contribution>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldIncursionContribution
                WHERE (SiteId,PilotId) IN (SELECT value->>'$.Id',value->>'$.Part' FROM json_each({deleted}))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldIncursionContribution(SiteId,PilotId,TaleId,Damage,PendingLoyalty)
                SELECT value->>'$.SiteId',value->>'$.PilotId',value->>'$.TaleId',value->>'$.Damage',value->>'$.PendingLoyalty'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SiteId,PilotId) DO UPDATE SET Damage=excluded.Damage,PendingLoyalty=excluded.PendingLoyalty
                """
            );
        }
    }
}
