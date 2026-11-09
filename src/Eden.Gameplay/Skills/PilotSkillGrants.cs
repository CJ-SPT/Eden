namespace Eden.Gameplay.Skills;

public interface IPilotSkillGrants
{
    FreePointChange Grant(long pilotId, TrainingTarget target);
}

public static class SkillGrantRules
{
    public static long Points(TrainingTarget target, double rank, long current)
    {
        if (target.TypeId <= 0 || target.Level is < 0 or > 5 || !double.IsFinite(rank) || rank <= 0 || current < 0)
        {
            throw new InvalidDataException("Choose a skill and a level from zero to five.");
        }
        // Applying a lower profile never removes previously earned progress.
        return Math.Max(current, TrainedSkill.PointsForLevel(rank, target.Level));
    }
}
