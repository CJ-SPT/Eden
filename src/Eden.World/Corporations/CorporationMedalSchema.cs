using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationMedalSchema
{
    internal const int Version = 230;
    internal const string MedalTable = "WorldCorporationMedal";
    internal const string AwardTable = "WorldCorporationMedalAward";

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationMedal(
                Id INTEGER PRIMARY KEY, CorporationId INTEGER NOT NULL, DataJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationMedal_CorporationId ON WorldCorporationMedal(CorporationId);
            CREATE TABLE IF NOT EXISTS WorldCorporationMedalAward(
                Id INTEGER PRIMARY KEY, MedalId INTEGER NOT NULL, RecipientId INTEGER NOT NULL, DataJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationMedalAward_MedalId ON WorldCorporationMedalAward(MedalId);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationMedalAward_RecipientId ON WorldCorporationMedalAward(RecipientId);
            """
        );
}
