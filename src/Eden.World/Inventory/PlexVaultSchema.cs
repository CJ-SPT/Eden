using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static class PlexVaultSchema
{
    internal const int BalanceVersion = 236;
    internal const int HistoryVersion = 237;
    internal const int DirectTradeVersion = 238;
    internal const string HistoryTable = "WorldPlexVaultHistory";

    internal static void Configure(ModelBuilder model)
    {
        var entries = model.Entity<SavedPlexVaultEntry>().ToTable(HistoryTable);
        entries.HasIndex(entry => new { entry.AccountId, entry.Id });
        entries.HasOne<Account>().WithMany().HasForeignKey(entry => entry.AccountId).OnDelete(DeleteBehavior.Cascade);
    }

    internal static void UpgradeBalance(WorldDbContext world)
    {
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldAccount')").Contains("PlexBalance"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAccount ADD COLUMN PlexBalance INTEGER NOT NULL DEFAULT 0");
        }
    }

    internal static void UpgradeHistory(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldPlexVaultHistory(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, AccountId INTEGER NOT NULL REFERENCES WorldAccount(Id) ON DELETE CASCADE,
                PilotId INTEGER NOT NULL, Kind INTEGER NOT NULL, Amount INTEGER NOT NULL, Balance INTEGER NOT NULL, Time INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_WorldPlexVaultHistory_AccountId_Id ON WorldPlexVaultHistory(AccountId,Id);
            """
        );

    internal static void UpgradeDirectTrade(WorldDbContext world)
    {
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldPlexVaultHistory')")
                .Contains("CounterpartyPilotId")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldPlexVaultHistory ADD COLUMN CounterpartyPilotId INTEGER");
        }
    }
}
