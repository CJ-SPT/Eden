using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class IncursionSchema
{
    internal const int Version = 223;
    internal const string CampaignTable = "WorldIncursionCampaign";
    internal const string ContributionTable = "WorldIncursionContribution";

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedIncursionCampaign>().ToTable(CampaignTable).HasKey(row => row.TaleId);
        model.Entity<SavedIncursionContribution>().ToTable(ContributionTable).HasKey(row => new { row.SiteId, row.PilotId });
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldIncursionCampaign(
                TaleId INTEGER PRIMARY KEY, Clears INTEGER NOT NULL, LastUpdated INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldIncursionContribution(
                SiteId INTEGER NOT NULL, PilotId INTEGER NOT NULL, TaleId INTEGER NOT NULL,
                Damage REAL NOT NULL, PendingLoyalty INTEGER NOT NULL, PRIMARY KEY(SiteId,PilotId));
            """
        );
}
