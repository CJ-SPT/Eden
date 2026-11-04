using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class NpcPopulationSchema
{
    private sealed record Column(string Name, string Definition);

    // Checking individual columns also supports the historical migration fixtures,
    // which deliberately replay old versions against retained newer tables.
    internal static void Upgrade(WorldDbContext world)
    {
        Column[] columns =
        [
            new("CorporationId", "INTEGER NOT NULL DEFAULT 1000127"),
            new("FactionId", "INTEGER NOT NULL DEFAULT 500010"),
            new("Role", "INTEGER NOT NULL DEFAULT 0"),
            new("AnchorKind", "INTEGER NOT NULL DEFAULT 0"),
            new("EncounterId", "INTEGER NOT NULL DEFAULT 0"),
            new("HomeX", "REAL"),
            new("HomeY", "REAL"),
            new("HomeZ", "REAL"),
            new("RetiredAt", "INTEGER"),
        ];
        var existing = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldNpc')").ToHashSet();
        foreach (var column in columns)
        {
            if (!existing.Contains(column.Name))
            {
                // Identifiers and definitions come exclusively from the fixed list above.
                string sql = $"ALTER TABLE WorldNpc ADD COLUMN {column.Name} {column.Definition}";
                world.Database.ExecuteSqlRaw(sql);
            }
        }
        // Build 3662168's former fixed encounter is Guristas (500010),
        // corporation 1000127. Preserve its identities and historical rewards.
        world.Database.ExecuteSqlRaw("UPDATE WorldNpc SET EncounterId=BeltId WHERE EncounterId=0");
        world.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_WorldNpc_EncounterId ON WorldNpc(EncounterId)");
        world.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_WorldNpc_SystemId_RetiredAt_DestroyedAt ON WorldNpc(SystemId,RetiredAt,DestroyedAt)"
        );
    }
}
