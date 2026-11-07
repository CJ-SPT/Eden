using Eden.World.Combat.Npcs;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class NpcSchemaReconciliation
{
    internal static void Apply(WorldDbContext world, int version)
    {
        if (version < 81)
        {
            // Historical interaction backfills materialize WorldNpc with the
            // current model, so its new columns must exist before those reads.
            NpcRepairSchema.Upgrade(world);
        }
        if (version < 82)
        {
            NpcEwarSchema.Upgrade(world);
        }
        // Idempotent even when another feature upgraded a save to a newer
        // version before the mining tables were introduced on its branch.
        NpcMiningSchema.Upgrade(world);
    }
}
