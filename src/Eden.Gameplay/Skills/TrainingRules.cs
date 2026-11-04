using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Skills;

public sealed record TrainingSkill(
    long Points,
    double Rank,
    double PrimaryAttribute,
    double SecondaryAttribute,
    int AlphaLevel,
    IReadOnlyList<TrainingTarget> Prerequisites
)
{
    public IReadOnlyList<TrainingRateChange> RateChanges { get; init; } = [];
}

public sealed record TrainingInputs(
    IReadOnlyDictionary<int, TrainingSkill> Skills,
    AccountCloneGrade CloneGrade = AccountCloneGrade.Alpha,
    long UnallocatedPoints = 0
);

public static class TrainingRules
{
    public const int MaximumEntries = 150;
    private const int MaximumQueueDays = 3650;
    public const long MaximumAlphaPoints = 5_000_000;

    public static List<TrainingEntry> Plan(
        IReadOnlyList<TrainingTarget> targets,
        TrainingInputs inputs,
        bool activate,
        TrainingEntry? running,
        long now,
        bool clampAlphaBudget = false
    )
    {
        if (targets.Count > MaximumEntries)
        {
            throw new InvalidTrainingQueue("QueueTooManySkills");
        }
        if (inputs.UnallocatedPoints < 0)
        {
            throw new InvalidDataException("Invalid unallocated skill point balance.");
        }
        List<TrainingEntry> result = [];
        var points = inputs.Skills.ToDictionary(s => s.Key, s => s.Value.Points);
        long start = now;
        long total = checked(points.Values.Sum() + inputs.UnallocatedPoints);
        if (!Enum.IsDefined(inputs.CloneGrade))
        {
            throw new InvalidDataException("Unknown account clone grade.");
        }
        bool alpha = inputs.CloneGrade == AccountCloneGrade.Alpha;
        bool budgetExhausted = false;

        foreach (var target in targets)
        {
            if (!points.TryGetValue(target.TypeId, out long before))
            {
                throw new InvalidTrainingQueue("QueueSkillNotUploaded");
            }
            if (start - now > TimeSpan.FromDays(MaximumQueueDays).Ticks)
            {
                throw new InvalidTrainingQueue("QueueTooLong");
            }
            var skill = inputs.Skills[target.TypeId];
            double rank = skill.Rank;
            if (target.Level is < 1 or > 5 || target.Level != new TrainedSkill(target.TypeId, before, rank).Level + 1)
            {
                throw new InvalidTrainingQueue("QueueInvalidSkillLevel");
            }
            bool active = activate && !budgetExhausted;
            if (active && alpha && target.Level > skill.AlphaLevel)
            {
                if (!clampAlphaBudget)
                {
                    throw new InvalidTrainingQueue("SkillInQueueRequiresOmegaCloneState");
                }
                active = false;
                budgetExhausted = true;
            }
            if (skill.Prerequisites.Count > 0)
            {
                foreach (var prerequisite in skill.Prerequisites)
                {
                    int id = prerequisite.TypeId;
                    int level = prerequisite.Level;
                    if (!points.TryGetValue(id, out long sp) || new TrainedSkill(id, sp, inputs.Skills[id].Rank).Level < level)
                    {
                        throw new InvalidTrainingQueue("QueueSkillPrerequisitesNotMet");
                    }
                }
            }
            long after = TrainedSkill.PointsForLevel(rank, target.Level);
            long endPoints = after;
            long available = Math.Max(0, MaximumAlphaPoints - total);
            total = checked(total + after - before);
            if (active && alpha && total > MaximumAlphaPoints)
            {
                if (!clampAlphaBudget)
                {
                    throw new InvalidTrainingQueue("SkillInQueueOverAlphaSpTrainingSize");
                }
                budgetExhausted = true;
                active = available > 0;
                if (active)
                {
                    endPoints = checked(before + available);
                }
            }
            double primary = skill.PrimaryAttribute;
            double secondary = skill.SecondaryAttribute;
            double rate = (primary + secondary / 2) * (alpha ? .5 : 1);
            long duration = (long)Math.Ceiling((endPoints - before) * TimeSpan.TicksPerMinute / rate);
            var periods = TrainingTiming.Plan(skill, before, endPoints, start, alpha);
            if (periods.Count > 0)
            {
                duration = periods[^1].EndTime - start;
            }
            var entry =
                result.Count == 0 && running is not null
                    ? running
                    : new TrainingEntry(target, before, endPoints, active ? start : null, active ? start + duration : null)
                    {
                        Periods = active ? periods : [],
                    };
            start = entry.EndTime ?? start + duration;
            points[target.TypeId] = after;
            result.Add(entry);
        }
        return result;
    }
}
