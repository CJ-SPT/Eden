using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal static class NpcRepairSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        var existing = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldNpc')").ToHashSet();
        string[] columns = [nameof(WorldNpc.ShieldRepairAt), nameof(WorldNpc.ArmorRepairAt), nameof(WorldNpc.RepairRevision)];
        foreach (string column in columns)
        {
            if (!existing.Contains(column))
            {
                // Identifiers are restricted to the fixed property names above.
                string sql = $"ALTER TABLE WorldNpc ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0";
                world.Database.ExecuteSqlRaw(sql);
            }
        }
    }
}
