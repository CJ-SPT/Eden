using Eden.Data.World.Industry;
using Eden.Data.World.Structures;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal static class StructureIndustry
{
    internal static void RequireService(
        WorldDbContext world,
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
        var type = catalog.FittingType(productTypeId);
        var required = StructureIndustryRules.Required(activity, type?.CategoryId ?? 0, type?.GroupId ?? 0);
        if (
            !PilotStructureStore.Access(world, structure, pilotId)
            || !PilotStructureStore.Snapshot(world, catalog, structure).Services.Contains(required)
        )
        {
            throw new InvalidDataException("The required structure industry service is offline or access is denied.");
        }
    }

    internal static void Synchronize(WorldDbContext world, CatalogDbContext catalog, SavedStructure structure, long at)
    {
        var services = PilotStructureStore.Snapshot(world, catalog, structure).Services;
        foreach (var job in world.Set<IndustryJob>().Where(j => j.StationId == structure.Id && j.Status == IndustryJobStatus.Installed))
        {
            var type = catalog.RequireItem(job.ProductTypeId);
            var required =
                job.Activity == IndustryActivity.Reaction
                    ? ReactionCatalog.Service(job.BlueprintTypeId, catalog.RequireItem(job.BlueprintTypeId).GroupId)
                    : StructureIndustryRules.Required(job.Activity, type.CategoryId, type.GroupId);
            bool available = required is { } service && services.Contains(service);
            if (!available && job.SuspendedAt is null && job.EndTime > at)
            {
                job.SuspendedAt = at;
            }
            else if (available && job.SuspendedAt is long suspended)
            {
                job.EndTime = checked(job.EndTime + Math.Max(0, at - suspended));
                job.SuspendedAt = null;
            }
        }
    }
}
