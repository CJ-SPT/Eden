using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class WorldVersion94
{
    // Schema 93 stored only the deadline, using a fixed ten-hour preparation.
    // Preserve the original request time when shortening pending requests.
    private static readonly long s_biomassDelayReductionTicks = TimeSpan.FromHours(10).Ticks - TimeSpan.FromMinutes(10).Ticks;

    internal static void Apply(WorldDbContext world, int version)
    {
        if (version < 94)
        {
            world.Database.ExecuteSql(
                $"UPDATE WorldCharacterDeletion SET ReadyAt=ReadyAt-{s_biomassDelayReductionTicks} WHERE DeletedAt IS NULL"
            );
        }
        world.Database.ExecuteSqlRaw("PRAGMA user_version=94");
    }
}
