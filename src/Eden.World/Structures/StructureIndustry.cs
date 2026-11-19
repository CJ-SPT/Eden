using Eden.Data.World.Industry;
using Eden.Data.World.Structures;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal static class StructureIndustry
{
    internal static void RequireService(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long structureId,
        IndustryActivity activity,
        int productTypeId = 0
    )
    {
        if (structureId < StructureRules.FirstStructureId)
        {
            return;
        }
        var structure = PilotStructureStore.Require(world, structureId);
        if (!CanUse(world, catalog, structure, pilotId, activity, productTypeId))
        {
            throw new InvalidDataException("The required structure industry service is offline or access is denied.");
        }
    }

    internal static bool CanUse(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure,
        long pilotId,
        IndustryActivity activity,
        int productTypeId
    )
    {
        var type = catalog.FittingType(productTypeId);
        var required = StructureIndustryRules.Required(activity, type?.CategoryId ?? 0, type?.GroupId ?? 0);
        return PilotStructureStore.Access(world, structure, pilotId)
            && PilotStructureStore.Snapshot(world, catalog, structure).Services.Contains(required);
    }

    internal static void Synchronize(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long at)
    {
        var services = PilotStructureStore.Snapshot(world, catalog, structure).Services;
        foreach (var job in world.Filter<IndustryJob>(j => j.StationId == structure.Id && j.Status == IndustryJobStatus.Installed))
        {
            var type = catalog.RequireItem(job.ProductTypeId);
            var required =
                job.Activity == IndustryActivity.Reaction
                    ? ReactionCatalog.Service(job.BlueprintTypeId, catalog.RequireItem(job.BlueprintTypeId).GroupId)
                    : StructureIndustryRules.Required(job.Activity, type.CategoryId, type.GroupId);
            bool available = required is { } service && services.Contains(service);
            if (!available && job.SuspendedAt is null && !PilotIndustryStore.Ready(world, job, at))
            {
                job.SuspendedAt = at;
                if (job.CapsuleerEndTicks.HasValue)
                {
                    job.CapsuleerSuspendedTicks = world.ActiveTicks;
                }
            }
            else if (available && job.SuspendedAt is long suspended)
            {
                if (job.CapsuleerEndTicks is long due && job.CapsuleerSuspendedTicks is long activePause)
                {
                    job.CapsuleerEndTicks = checked(due + (world.ActiveTicks - activePause));
                    job.CapsuleerSuspendedTicks = null;
                }
                else
                {
                    job.EndTime = checked(job.EndTime + Math.Max(0, at - suspended));
                }
                job.SuspendedAt = null;
            }
        }
    }
}
