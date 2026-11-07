using Eden.Data.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationShareSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationShareIssue(
                CorporationId INTEGER PRIMARY KEY REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                Shares INTEGER NOT NULL CHECK(Shares>0));
            CREATE TABLE IF NOT EXISTS WorldCorporationShareHolding(
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporationShareIssue(CorporationId) ON DELETE CASCADE,
                ShareholderId INTEGER NOT NULL REFERENCES WorldInventoryOwner(Id) ON DELETE RESTRICT,
                Shares INTEGER NOT NULL CHECK(Shares>=0), PRIMARY KEY(CorporationId,ShareholderId));
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationShareHolding_ShareholderId ON WorldCorporationShareHolding(ShareholderId);
            CREATE TABLE IF NOT EXISTS WorldCorporationShareTransfer(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, CorporationId INTEGER NOT NULL REFERENCES WorldCorporationShareIssue(CorporationId),
                ActorId INTEGER NOT NULL, FromOwnerId INTEGER NOT NULL, ToOwnerId INTEGER NOT NULL,
                Shares INTEGER NOT NULL CHECK(Shares>0), Time INTEGER NOT NULL);
            """
        );
        var missing = world
            .Set<SavedCorporation>()
            .Where(c => !world.Set<SavedCorporationShareIssue>().Any(i => i.CorporationId == c.Id))
            .Select(c => c.Id)
            .ToArray();
        foreach (int corporation in missing)
        {
            world.Database.ExecuteSql(
                $"INSERT INTO WorldCorporationShareIssue(CorporationId,Shares) VALUES({corporation},{CorporationRules.InitialShares})"
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationShareHolding(CorporationId,ShareholderId,Shares)
                    VALUES({corporation},{corporation},{CorporationRules.InitialShares})
                """
            );
        }
    }
}
