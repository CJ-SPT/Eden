using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Modding;

internal static class ModStateSchema
{
    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS WorldModState(Id TEXT PRIMARY KEY NOT NULL, Json TEXT NOT NULL)");

    internal static string Id(string modId, string key) => FormattableString.Invariant($"{modId.Length}:{modId}{key}");
}
