using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal static class NpcEnergySchema
{
    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldNpcEnergy(
                NpcId INTEGER PRIMARY KEY REFERENCES WorldNpc(Id) ON DELETE CASCADE,
                Charge REAL NOT NULL CHECK(Charge>=0),UpdatedAt INTEGER NOT NULL);
            """
        );
}
