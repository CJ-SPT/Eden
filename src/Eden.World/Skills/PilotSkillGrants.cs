using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Skills;

internal sealed partial class PilotTrainingStore
{
    // Build 3662168 appConst.skillEventGMGive, inspected without executing client code.
    private const int GameMasterSkillGranted = 39;

    public FreePointChange Grant(long pilotId, TrainingTarget target)
    {
        var result = Update(pilotId, null, false, false, points: new(null, Grant: target));
        return new(result.Points!.Remaining, result.Training, result.Points.Allocations, result.PointRevision);
    }

    private FreePointPlan GrantPoints(WorldDbContext world, long pilotId, TrainingTarget target, long balance)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var type = catalog.FittingType(target.TypeId);
        var definition = catalog.Read(CatalogData.Client.Types, (long)target.TypeId);
        if (type?.CategoryId != (int)InventoryCategory.Skill || definition?.Published != 1)
        {
            throw new InvalidDataException("Only published catalog skills can be granted.");
        }
        double rank = catalog.FittingAttributes(target.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
        var skill = world.Set<PilotSkill>().Find(pilotId, target.TypeId);
        long before = skill?.Points ?? 0;
        long after = SkillGrantRules.Points(target, rank, before);
        if (skill is not null && after == before)
        {
            return new(balance, []);
        }
        if (skill is null)
        {
            world.Add(new PilotSkill { PilotId = pilotId, TypeId = target.TypeId });
        }
        return new(balance, [new(target.TypeId, before, after)]);
    }
}
