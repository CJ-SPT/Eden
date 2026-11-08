using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class CombatAnomalySchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<WorldCombatAnomaly>().ToTable("WorldCombatAnomaly").HasKey(s => s.Id);
        model.Entity<WorldCombatAnomaly>().HasIndex(s => new { s.SystemId, s.CompletedAt });
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCombatAnomaly (
                Id INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL, Wave INTEGER NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, CompletedAt INTEGER, ReplaceAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldCombatAnomaly_SystemId_CompletedAt ON WorldCombatAnomaly(SystemId,CompletedAt);
            """
        );
}
