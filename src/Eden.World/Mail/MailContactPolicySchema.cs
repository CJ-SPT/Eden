using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mail;

internal static class MailContactPolicySchema
{
    internal const int Version = 231;
    internal const string Table = "WorldMailContactPolicy";

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldMailContactPolicy(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, CostIsk INTEGER);
            """
        );
}
