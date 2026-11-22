using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Skills;

internal sealed partial class PilotTrainingStore
{
    // Build 3662168 appConst.skillEventGMGive, inspected without executing client code.
    private const int GameMasterSkillGranted = 39;

    public FreePointChange Grant(long pilotId, TrainingTarget target)
    {
        var result = Update(pilotId, null, false, false, points: new(null, Grant: [target]));
        return new(result.Points!.Remaining, result.Training, result.Points.Allocations, result.PointRevision);
    }

    private FreePointPlan GrantPoints(
        WorldAction world,
        long pilotId,
        IReadOnlyList<TrainingTarget> targets,
        long balance,
        bool includeUnpublished
    )
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<SkillPointAllocation> changes = [];
        foreach (var target in targets)
        {
            var type = catalog.FittingType(target.TypeId);
            var definition = catalog.Read(CatalogData.Client.Types, (long)target.TypeId);
            if (type?.CategoryId != (int)InventoryCategory.Skill || !includeUnpublished && definition?.Published != 1)
            {
                throw new InvalidDataException("Choose a catalog skill eligible for this grant.");
            }
            double rank = catalog.FittingAttributes(target.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
            var skill = world.Set<PilotSkill>().Find(pilotId, target.TypeId);
            long before = skill?.Points ?? 0;
            long after = SkillGrantRules.Points(target, rank, before);
            if (skill is not null && after == before)
            {
                continue;
            }
            if (skill is null)
            {
                world.Add(new PilotSkill { PilotId = pilotId, TypeId = target.TypeId });
            }
            changes.Add(new(target.TypeId, before, after));
        }
        return new(balance, changes);
    }
}
