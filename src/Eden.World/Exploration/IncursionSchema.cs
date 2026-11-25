using Eden.Data.World.Exploration;
using Eden.Gameplay.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class IncursionSchema
{
    internal const int Version = 226;
    internal const int LoyaltyJournalVersion = 233;
    internal const string CampaignTable = "WorldIncursionCampaign";
    internal const string ContributionTable = "WorldIncursionContribution";
    internal const string RepairDamageTable = "WorldIncursionRepairDamage";
    internal const string LoyaltyJournalTable = "WorldIncursionLoyaltyEntry";

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedIncursionCampaign>().ToTable(CampaignTable).HasKey(row => row.TaleId);
        model.Entity<SavedIncursionContribution>().ToTable(ContributionTable).HasKey(row => new { row.SiteId, row.PilotId });
        model.Entity<SavedIncursionRepairDamage>().ToTable(RepairDamageTable).HasKey(row => new { row.SiteId, row.ShipId });
        var journal = model.Entity<SavedIncursionLoyaltyEntry>().ToTable(LoyaltyJournalTable);
        journal.HasKey(row => new { row.PilotId, row.SourceId });
        journal.HasIndex(row => new { row.PilotId, row.Time });
    }

    internal static void Upgrade(WorldDbContext world, int version)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldIncursionCampaign(
                TaleId INTEGER PRIMARY KEY, Clears INTEGER NOT NULL, HeadquartersCleared INTEGER NOT NULL DEFAULT 0,
                DefeatedAt INTEGER, LastUpdated INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldIncursionContribution(
                SiteId INTEGER NOT NULL, PilotId INTEGER NOT NULL, TaleId INTEGER NOT NULL,
                Damage REAL NOT NULL, Repair REAL NOT NULL DEFAULT 0, PendingLoyalty INTEGER NOT NULL, PRIMARY KEY(SiteId,PilotId));
            CREATE TABLE IF NOT EXISTS WorldIncursionRepairDamage(
                SiteId INTEGER NOT NULL, ShipId INTEGER NOT NULL, TaleId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                Shield REAL NOT NULL, Armor REAL NOT NULL, Hull REAL NOT NULL, PRIMARY KEY(SiteId,ShipId));
            CREATE TABLE IF NOT EXISTS WorldIncursionLoyaltyEntry(
                PilotId INTEGER NOT NULL, SourceId INTEGER NOT NULL, TaleId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                RewardId INTEGER NOT NULL, RewardMessageId INTEGER NOT NULL, DungeonName TEXT NOT NULL, Pilots INTEGER NOT NULL,
                Time INTEGER NOT NULL, Event INTEGER NOT NULL, Points INTEGER NOT NULL, PRIMARY KEY(PilotId,SourceId));
            CREATE INDEX IF NOT EXISTS IX_WorldIncursionLoyaltyEntry_PilotId_Time ON WorldIncursionLoyaltyEntry(PilotId,Time);
            """
        );
        if (version >= Version)
        {
            return;
        }

        var campaign = world
            .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldIncursionCampaign')")
            .ToArray();
        if (!campaign.Contains("HeadquartersCleared"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldIncursionCampaign ADD COLUMN HeadquartersCleared INTEGER NOT NULL DEFAULT 0");
        }
        if (!campaign.Contains("DefeatedAt"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldIncursionCampaign ADD COLUMN DefeatedAt INTEGER");
            // Under schema 223, the eighth clear had already paid the pool and closed its focus.
            world.Database.ExecuteSql(
                $"UPDATE WorldIncursionCampaign SET DefeatedAt=LastUpdated WHERE Clears>={IncursionRules.ClearsToUnlockFinal}"
            );
        }
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldIncursionContribution')")
                .Contains("Repair")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldIncursionContribution ADD COLUMN Repair REAL NOT NULL DEFAULT 0");
        }
    }
}
