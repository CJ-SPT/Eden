using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal static class MiningPollutionSchema
{
    internal const int Version = 232;

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS SavedMiningPollutionCloud (
                Id INTEGER NOT NULL PRIMARY KEY,
                AsteroidId INTEGER NOT NULL UNIQUE,
                SystemId INTEGER NOT NULL,
                X REAL NOT NULL,
                Y REAL NOT NULL,
                Z REAL NOT NULL,
                StartedAt INTEGER NOT NULL,
                NextPulseAt INTEGER NOT NULL,
                ExpiresAt INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_SavedMiningPollutionCloud_SystemId ON SavedMiningPollutionCloud(SystemId);
            """
        );
}
