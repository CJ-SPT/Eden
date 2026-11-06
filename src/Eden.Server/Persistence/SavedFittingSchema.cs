using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class SavedFittingSchema
{
    internal static void Create(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSavedFitting(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ShipTypeId INTEGER NOT NULL, Name TEXT NOT NULL, Description TEXT NOT NULL,
                ItemsJson TEXT NOT NULL, SavedAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldSavedFitting_OwnerId ON WorldSavedFitting(OwnerId);
            """
        );
}
