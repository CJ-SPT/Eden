using Eden.Data.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal static class NpcGuardSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldNpc')").Contains("SpawnSlot"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldNpc ADD COLUMN SpawnSlot INTEGER NOT NULL DEFAULT 0");
        }
        world.Database.ExecuteSqlRaw(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldNpc_EncounterId_SpawnSlot ON WorldNpc(EncounterId,SpawnSlot)
                WHERE Role<>0 AND DestroyedAt IS NULL AND RetiredAt IS NULL;
            """
        );
    }
}
