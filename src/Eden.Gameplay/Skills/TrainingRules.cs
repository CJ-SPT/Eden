namespace Eden.Gameplay.Skills;

public sealed record TrainingSkill(
    long Points,
    double Rank,
    double PrimaryAttribute,
    double SecondaryAttribute,
    int AlphaLevel,
    IReadOnlyList<TrainingTarget> Prerequisites
);

public sealed record TrainingInputs(IReadOnlyDictionary<int, TrainingSkill> Skills);

public static class TrainingRules
{
    public const int MaximumEntries = 150;
    private const int MaximumQueueDays = 3650;
    private const long MaximumAlphaPoints = 5_000_000;

    public static List<TrainingEntry> Plan(
        IReadOnlyList<TrainingTarget> targets,
        TrainingInputs inputs,
        bool activate,
        TrainingEntry? running,
        long now
    )
    {
        if (targets.Count > MaximumEntries)
        {
            throw new InvalidTrainingQueue("QueueTooManySkills");
        }
        List<TrainingEntry> result = [];
        var points = inputs.Skills.ToDictionary(s => s.Key, s => s.Value.Points);
        long start = now;
        long total = points.Values.Sum();

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
            if (activate && target.Level > skill.AlphaLevel)
            {
                throw new InvalidTrainingQueue("SkillInQueueRequiresOmegaCloneState");
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
            total += after - before;
            if (activate && total > MaximumAlphaPoints)
            {
                throw new InvalidTrainingQueue("SkillInQueueOverAlphaSpTrainingSize");
            }
            // Local accounts are Alpha. Training earns half the Omega rate.
            double primary = skill.PrimaryAttribute;
            double secondary = skill.SecondaryAttribute;
            long duration = (long)Math.Ceiling((after - before) * TimeSpan.TicksPerMinute / ((primary + secondary / 2) / 2));
            var entry =
                result.Count == 0 && running is not null
                    ? running
                    : new TrainingEntry(target, before, after, activate ? start : null, activate ? start + duration : null);
            start = entry.EndTime ?? start + duration;
            points[target.TypeId] = after;
            result.Add(entry);
        }
        return result;
    }
}
