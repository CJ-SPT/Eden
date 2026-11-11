using Eden.Data.World.Characters;
using Eden.Data.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal static class MiningLedgerSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedPersonalMiningLog>().ToTable("WorldPersonalMiningLog").HasKey(l => l.Id);
        model.Entity<SavedPersonalMiningLog>().HasOne<Pilot>().WithMany().HasForeignKey(l => l.PilotId).OnDelete(DeleteBehavior.Cascade);
        model
            .Entity<SavedPersonalMiningLog>()
            .HasIndex(l => new
            {
                l.PilotId,
                l.Day,
                l.CorporationId,
                l.SystemId,
                l.TypeId,
            })
            .IsUnique();
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldPersonalMiningLog(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                Day INTEGER NOT NULL, CorporationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                Quantity INTEGER NOT NULL, Critical INTEGER NOT NULL, Wasted INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldPersonalMiningLog_PilotId_Day_CorporationId_SystemId_TypeId
                ON WorldPersonalMiningLog(PilotId,Day,CorporationId,SystemId,TypeId);
            """
        );
}
