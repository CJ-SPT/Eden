using Eden.Data.Skills;
using Eden.Gameplay.Skills;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Skills;

internal sealed partial class PilotTrainingStore
{
    private sealed record PointOperation(FreePointRequest? Request, long Credit = 0);

    private sealed record TrainingUpdateResult(TrainingState Training, bool Remapped, FreePointPlan? Points, long PointRevision = 0);

    public long Balance(long pilotId) => Observe(pilotId).Points;

    public FreeSkillPointBalance Observe(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.freePoints");
        return LiveReadModels.Find(database.Path) is { } resident
            ? resident.Points(pilotId, () => ObserveSql(pilotId))
            : ObserveSql(pilotId);
    }

    private FreeSkillPointBalance ObserveSql(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return ReadPointBalance(world, pilotId);
    }

    public FreeSkillPointState Snapshot(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var result = UpdateCore(world, pilotId, null, false, false, null, null, false, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        var balance = ReadPointBalance(world, pilotId);
        transaction.Commit();
        return new(balance.Points, balance.Revision, result.Training);
    }

    internal static long ReadFreePoints(WorldDbContext world, long pilotId) => ReadPointBalance(world, pilotId).Points;

    private static FreeSkillPointBalance ReadPointBalance(WorldDbContext world, long pilotId)
    {
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown skill point recipient.");
        }
        var state = world.Set<SavedFreeSkillPoints>().Find(pilotId);
        return state is null ? new(0, 0)
            : state.Points >= 0 && state.Revision >= 0 ? new(state.Points, state.Revision)
            : throw new InvalidDataException("Invalid unallocated skill point balance.");
    }

    public FreePointPlan Preview(long pilotId, FreePointRequest request) =>
        Update(pilotId, null, false, false, points: new(request), preview: true).Points!;

    public FreePointChange Apply(long pilotId, FreePointRequest request)
    {
        var result = Update(pilotId, null, false, false, points: new(request));
        return new(result.Points!.Remaining, result.Training, result.Points.Allocations, result.PointRevision);
    }

    internal FreePointChange Credit(WorldDbContext world, long pilotId, long amount, long now)
    {
        var result = UpdateCore(world, pilotId, null, false, false, null, new(null, amount), false, now);
        return new(result.Points!.Remaining, result.Training, result.Points.Allocations, result.PointRevision);
    }

    private FreePointPlan ChangePoints(
        WorldDbContext world,
        long pilotId,
        PointOperation operation,
        IReadOnlyList<TrainingEntry> queue,
        HashSet<int> changed,
        long now,
        bool preview
    )
    {
        long balance = ReadFreePoints(world, pilotId);
        FreePointPlan plan;
        if (operation.Request is { } request)
        {
            if (
                request is FreePointRequest.Single single
                && queue.FirstOrDefault() is { StartTime: not null } first
                && first.Target.TypeId == single.TypeId
            )
            {
                throw new InvalidTrainingQueue("CannotApplyFreePointsWhileTrainingSkill");
            }
            var targets = request switch
            {
                FreePointRequest.Single one => new TrainingTarget[] { new(one.TypeId, 5) },
                FreePointRequest.Targets selected => selected.Levels,
                _ => queue.Select(e => e.Target).ToArray(),
            };
            plan = FreeSkillPointRules.Plan(
                balance,
                request,
                ReadInputs(world, pilotId, targets, now),
                queue.Select(e => e.Target).ToArray()
            );
        }
        else
        {
            plan = new(FreeSkillPointRules.Credit(balance, operation.Credit), []);
        }
        if (preview)
        {
            return plan;
        }
        if (plan.Remaining != balance)
        {
            var state = world.Set<SavedFreeSkillPoints>().Find(pilotId) ?? world.Add(new SavedFreeSkillPoints { PilotId = pilotId }).Entity;
            state.Points = plan.Remaining;
            state.Revision = checked(state.Revision + 1);
        }
        foreach (var allocation in plan.Allocations)
        {
            world.Set<PilotSkill>().Find(pilotId, allocation.TypeId)!.Points = allocation.After;
            changed.Add(allocation.TypeId);
            Record(world, pilotId, now, allocation.TypeId, allocation.After, FreeSkillPointRules.UsedEvent);
        }
        world.SaveChanges();
        return plan;
    }
}
