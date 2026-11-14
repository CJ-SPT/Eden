using Eden.Data.World.Skills;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Skills;

internal sealed partial class PilotTrainingStore
{
    private sealed record PointOperation(FreePointRequest? Request, long Credit = 0, TrainingTarget? Grant = null);

    private sealed record TrainingUpdateResult(TrainingState Training, bool Remapped, FreePointPlan? Points, long PointRevision = 0);

    public long Balance(long pilotId) => Observe(pilotId).Points;

    public FreeSkillPointBalance Observe(long pilotId)
    {
        using var phase = SessionPerformance.Phase("read.freePoints");
        FreeSkillPointBalance Load() => WorldActionScope.Inspect(database, world => ReadPointBalance(world, pilotId));
        return LiveReadModels.Find(database.Path)?.Points(pilotId, Load) ?? Load();
    }

    public FreeSkillPointState Snapshot(long pilotId)
    {
        using WorldActionScope action = new(database, new(pilots: [pilotId]));
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = UpdateCore(action.World, pilotId, null, false, false, null, null, false, now);
        var balance = ReadPointBalance(action.World, pilotId);
        action.Commit();
        return new(balance.Points, balance.Revision, result.Training);
    }

    internal static long ReadFreePoints(WorldDbContext world, long pilotId) => ReadPointBalance(world, pilotId).Points;

    internal static void CreditSilentFreePoints(WorldDbContext world, long pilotId, long amount)
    {
        long balance = ReadFreePoints(world, pilotId);
        SavePointBalance(world, pilotId, FreeSkillPointRules.Credit(balance, amount), advanceRevision: false);
    }

    private static void SavePointBalance(WorldDbContext world, long pilotId, long balance, bool advanceRevision = true)
    {
        var state = world.Set<SavedFreeSkillPoints>().Find(pilotId) ?? world.Add(new SavedFreeSkillPoints { PilotId = pilotId }).Entity;
        state.Points = balance;
        // Pool-only activity credits stay silent. Revisions request skill/brain/queue notifications.
        if (advanceRevision)
        {
            state.Revision = checked(state.Revision + 1);
        }
    }

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

    public FreePointPlan Preview(long pilotId, FreePointRequest request) => Allocate(pilotId, request, preview: true).Points!;

    public FreePointChange Apply(long pilotId, FreePointRequest request)
    {
        var result = Allocate(pilotId, request, preview: false);
        return new(result.Points!.Remaining, result.Training, result.Points.Allocations, result.PointRevision);
    }

    private TrainingUpdateResult Allocate(long pilotId, FreePointRequest request, bool preview)
    {
        using var phase = SessionPerformance.Phase(preview ? "skills.previewPoints" : "skills.applyPoints");
        using WorldActionScope action = new(database, new(pilots: [pilotId]));
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = UpdateCore(action.World, pilotId, null, false, false, null, new(request), preview, now);
        // A preview evaluates elapsed training in the private view and then discards it.
        if (!preview)
        {
            action.Commit();
        }
        return result;
    }

    internal FreePointChange Credit(WorldDbContext world, long pilotId, long amount, long now) =>
        Credit(new SqlWorldAction(world), pilotId, amount, now);

    internal FreePointChange Credit(WorldAction world, long pilotId, long amount, long now)
    {
        var result = UpdateCore(world, pilotId, null, false, false, null, new(null, amount), false, now);
        return new(result.Points!.Remaining, result.Training, result.Points.Allocations, result.PointRevision);
    }

    private FreePointPlan ChangePoints(
        WorldAction world,
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
        else if (operation.Grant is { } grant)
        {
            plan = GrantPoints(world, pilotId, grant, balance);
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
            SavePointBalance(world, pilotId, plan.Remaining);
        }
        else if (operation.Grant is not null && plan.Allocations.Count > 0)
        {
            // Skill-only grants must invalidate another online recipient's brain/queue.
            SavePointBalance(world, pilotId, balance);
        }
        foreach (var allocation in plan.Allocations)
        {
            world.Set<PilotSkill>().Find(pilotId, allocation.TypeId)!.Points = allocation.After;
            changed.Add(allocation.TypeId);
            Record(
                world,
                pilotId,
                now,
                allocation.TypeId,
                allocation.After,
                operation.Grant is not null ? GameMasterSkillGranted : FreeSkillPointRules.UsedEvent
            );
        }
        world.SaveChanges();
        return plan;
    }
}
