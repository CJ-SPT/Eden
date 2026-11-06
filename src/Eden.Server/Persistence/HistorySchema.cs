using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class HistorySchema
{
    internal static void Configure(ModelBuilder model)
    {
        model
            .Entity<SavedWalletLedger>()
            .HasIndex(e => new
            {
                e.OwnerId,
                e.Key,
                e.Time,
            });
        model.Entity<SavedInsuranceLedger>().HasIndex(e => new { e.PilotId, e.Time });
        model.Entity<SavedHomeStationPayment>().HasIndex(e => new { e.PilotId, e.Time });
        model.Entity<SavedJumpClonePayment>().HasIndex(e => new { e.PilotId, e.Time });
        model.Entity<SavedContractPayment>().HasIndex(e => new { e.PilotId, e.Time });
        model.Entity<SavedCorporationPayment>().HasIndex(e => new { e.PilotId, e.Time });
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE INDEX IF NOT EXISTS IX_WorldWalletLedger_OwnerId_Key_Time ON WorldWalletLedger(OwnerId,Key,Time);
            CREATE INDEX IF NOT EXISTS IX_WorldInsuranceLedger_PilotId_Time ON WorldInsuranceLedger(PilotId,Time);
            CREATE INDEX IF NOT EXISTS IX_WorldHomeStationPayment_PilotId_Time ON WorldHomeStationPayment(PilotId,Time);
            CREATE INDEX IF NOT EXISTS IX_WorldJumpClonePayment_PilotId_Time ON WorldJumpClonePayment(PilotId,Time);
            CREATE INDEX IF NOT EXISTS IX_WorldContractPayment_PilotId_Time ON WorldContractPayment(PilotId,Time);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationPayment_PilotId_Time ON WorldCorporationPayment(PilotId,Time);
            """
        );
    }
}
