using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class CombatAnomalySchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<WorldCombatAnomaly>().ToTable("WorldCombatAnomaly").HasKey(s => s.Id);
        model.Entity<WorldAnomalyMember>().ToTable("WorldAnomalyMember").HasKey(m => m.NpcId);
        model.Entity<WorldAnomalyMember>().HasIndex(m => new { m.SiteId, m.Wave });
        model
            .Entity<WorldCombatAnomaly>()
            .HasIndex(s => new
            {
                s.SystemId,
                s.Slot,
                s.Id,
            });
        model.Entity<WorldCombatAnomaly>().HasIndex(s => new { s.SystemId, s.CompletedAt });
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCombatAnomaly (
                Id INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL, Wave INTEGER NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, CompletedAt INTEGER, ReplaceAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldCombatAnomaly_SystemId_CompletedAt ON WorldCombatAnomaly(SystemId,CompletedAt);
            CREATE TABLE IF NOT EXISTS WorldAnomalyMember (
                NpcId INTEGER PRIMARY KEY, SiteId INTEGER NOT NULL, Wave INTEGER NOT NULL, Trigger INTEGER NOT NULL,
                Sentry INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldAnomalyMember_SiteId_Wave ON WorldAnomalyMember(SiteId,Wave);
            """
        );
        var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCombatAnomaly')").ToHashSet();
        if (!columns.Contains("DungeonId"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCombatAnomaly ADD COLUMN DungeonId INTEGER NOT NULL DEFAULT 2134");
        }
        if (!columns.Contains("Slot"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCombatAnomaly ADD COLUMN Slot INTEGER NOT NULL DEFAULT 0");
        }
        if (!columns.Contains("NextWaveAt"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCombatAnomaly ADD COLUMN NextWaveAt INTEGER");
        }
        if (!columns.Contains("DefinitionVersion"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCombatAnomaly ADD COLUMN DefinitionVersion INTEGER NOT NULL DEFAULT 0");
        }
        var npcColumns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldNpc')").ToHashSet();
        if (!npcColumns.Contains("AcquisitionRange"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldNpc ADD COLUMN AcquisitionRange REAL");
        }
        world.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_WorldNpc_EncounterId_DestroyedAt_RetiredAt " + "ON WorldNpc(EncounterId,DestroyedAt,RetiredAt)"
        );
        world.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_WorldCombatAnomaly_SystemId_Slot_Id ON WorldCombatAnomaly(SystemId,Slot,Id)"
        );
    }
}
