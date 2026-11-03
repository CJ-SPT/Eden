namespace Eden.Gameplay.Skills;

public interface IPilotSkillReader
{
    IReadOnlyList<TrainedSkill> ReadSkills(long pilotId);
}

public sealed record TrainedSkill(int TypeId, long Points, double Rank)
{
    // Build 3662168 rounds the threshold up after applying the type's training multiplier.
    public static long PointsForLevel(double rank, int level) =>
        level == 0 ? 0 : checked((long)Math.Ceiling(250 * rank * Math.Pow(2, 2.5 * (level - 1))));

    public int Level
    {
        get
        {
            for (int level = 5; level > 0; level--)
            {
                if (Points >= PointsForLevel(Rank, level))
                {
                    return level;
                }
            }
            return 0;
        }
    }
}
