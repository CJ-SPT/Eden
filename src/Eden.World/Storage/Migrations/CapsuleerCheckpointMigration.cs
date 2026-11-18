using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class CapsuleerCheckpointMigration
{
    internal const int Version = 190;

    internal static void RemoveEmbeddedCollision(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            UPDATE WorldCapsuleerHauler
            SET Progress = json_remove(Progress, '$.Navigation.FollowTarget.Collision')
            WHERE CASE WHEN json_valid(Progress) THEN json_type(Progress, '$.Navigation.FollowTarget.Collision') = 'object' END;

            UPDATE WorldCapsuleerCommunity
            SET Progress = json_remove(Progress, '$.Navigation.FollowTarget.Collision')
            WHERE CASE WHEN json_valid(Progress) THEN json_type(Progress, '$.Navigation.FollowTarget.Collision') = 'object' END;
            """
        );
    }

    internal static void ReclaimPages(WorldDbContext world)
    {
        // The next startup step copies the file into RAM, including freelist pages.
        if (world.Database.SqlQueryRaw<int>("PRAGMA freelist_count").AsEnumerable().Single() > 0)
        {
            world.Database.ExecuteSqlRaw("PRAGMA temp_store=FILE; VACUUM;");
        }
    }
}
