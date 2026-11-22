namespace Eden.Gameplay.Skills;

public abstract record DeveloperSkillAction
{
    private DeveloperSkillAction() { }

    public sealed record Give(string Skill, int Level) : DeveloperSkillAction;

    public sealed record Remove(string Skill) : DeveloperSkillAction;

    public sealed record Starting : DeveloperSkillAction;

    public sealed record CopyFrom(string Source) : DeveloperSkillAction;
}

public sealed record DeveloperSkillResult(long PilotId, long Balance, long Revision, TrainingState Training, IReadOnlyList<int> Removed);

public interface IPilotDeveloperSkills
{
    DeveloperSkillResult Execute(long caller, string target, DeveloperSkillAction action);
}
