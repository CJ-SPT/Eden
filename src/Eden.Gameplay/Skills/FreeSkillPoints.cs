using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Skills;

public abstract record FreePointRequest
{
    public sealed record Single(int TypeId, long Points) : FreePointRequest;

    public sealed record Targets(IReadOnlyList<TrainingTarget> Levels) : FreePointRequest;

    public sealed record Queue : FreePointRequest;
}

public sealed record SkillPointAllocation(int TypeId, long Before, long After)
{
    public long Spent => After - Before;
}

public sealed record FreePointPlan(long Remaining, IReadOnlyList<SkillPointAllocation> Allocations);

public sealed record FreePointChange(
    long Remaining,
    TrainingState Training,
    IReadOnlyList<SkillPointAllocation> Allocations,
    long Revision
);

public sealed record FreeSkillPointState(long Balance, long Revision, TrainingState Training);

public sealed record FreeSkillPointBalance(long Points, long Revision);

public interface IPilotFreeSkillPoints
{
    long Balance(long pilotId);
    FreeSkillPointBalance Observe(long pilotId);
    FreeSkillPointState Snapshot(long pilotId);
    FreePointPlan Preview(long pilotId, FreePointRequest request);
    FreePointChange Apply(long pilotId, FreePointRequest request);
}

public static class FreeSkillPointRules
{
    // Build 3662168 appConst.skillEventFreeSkillPointsUsed.
    public const int UsedEvent = 307;

    public static long Credit(long balance, long amount)
    {
        if (balance < 0 || amount <= 0 || amount > long.MaxValue - balance)
        {
            throw new InvalidDataException("Invalid unallocated skill point credit.");
        }
        return balance + amount;
    }

    public static FreePointPlan Plan(long balance, FreePointRequest request, TrainingInputs inputs, IReadOnlyList<TrainingTarget> queue)
    {
        if (balance < 0 || !Enum.IsDefined(inputs.CloneGrade))
        {
            throw new InvalidDataException("Invalid unallocated skill point state.");
        }
        IReadOnlyList<TrainingTarget> targets = request switch
        {
            FreePointRequest.Single single when single.TypeId > 0 && single.Points > 0 => [new(single.TypeId, 5)],
            FreePointRequest.Targets selected => selected.Levels,
            FreePointRequest.Queue => queue,
            _ => throw new InvalidTrainingQueue("Invalid free skill point request."),
        };
        if (
            targets.Count > TrainingRules.MaximumEntries
            || targets.Distinct().Count() != targets.Count
            || targets.Any(t => t.TypeId <= 0 || t.Level is < 1 or > 5)
        )
        {
            throw new InvalidTrainingQueue("Invalid free skill point targets.");
        }
        var points = inputs.Skills.ToDictionary(p => p.Key, p => p.Value.Points);
        List<int> order = [];
        foreach (var target in targets)
        {
            if (!inputs.Skills.TryGetValue(target.TypeId, out var skill))
            {
                throw new InvalidTrainingQueue("CannotApplyFreePointsDoNotHaveSkill");
            }
            if (!double.IsFinite(skill.Rank) || skill.Rank <= 0 || skill.Points < 0)
            {
                throw new InvalidDataException("Invalid skill training multiplier or points.");
            }
            long before = points[target.TypeId];
            long maximum = TrainedSkill.PointsForLevel(skill.Rank, target.Level);
            long amount = Math.Max(0, maximum - before);
            if (request is FreePointRequest.Single single)
            {
                amount = Math.Min(amount, single.Points);
                if (amount > balance)
                {
                    throw new InvalidTrainingQueue("CannotApplyFreePointsNotEnoughRemaining");
                }
            }
            else
            {
                amount = Math.Min(amount, balance);
            }
            if (amount == 0)
            {
                continue;
            }
            if (inputs.CloneGrade == AccountCloneGrade.Alpha && before + amount > TrainedSkill.PointsForLevel(skill.Rank, skill.AlphaLevel))
            {
                throw new InvalidTrainingQueue("SkillInQueueRequiresOmegaCloneState");
            }
            foreach (var required in skill.Prerequisites)
            {
                if (
                    !inputs.Skills.TryGetValue(required.TypeId, out var prerequisite)
                    || new TrainedSkill(required.TypeId, points[required.TypeId], prerequisite.Rank).Level < required.Level
                )
                {
                    throw new InvalidTrainingQueue("QueueSkillPrerequisitesNotMet");
                }
            }
            if (!order.Contains(target.TypeId))
            {
                order.Add(target.TypeId);
            }
            points[target.TypeId] = checked(before + amount);
            balance -= amount;
        }
        return new(balance, order.Select(id => new SkillPointAllocation(id, inputs.Skills[id].Points, points[id])).ToArray());
    }
}
