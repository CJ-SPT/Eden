namespace Eden.Gameplay.Skills;

public readonly record struct SkillPlanOwner(long AccountId, long PilotId);

public sealed record SkillPlanSkill(int TypeId, int Level);

public sealed record SkillPlanDetails(string Name, string Description, IReadOnlyList<SkillPlanSkill> Skills);

public sealed record TrackedSkillPlan(Guid Id, SkillPlanDetails? Details);

public sealed record SkillPlanMilestoneTarget(int TypeId, int? Level);

public sealed record PersonalSkillMilestone(Guid Id, Guid PlanId, SkillPlanMilestoneTarget Target, string Description);

public interface IPersonalSkillPlans
{
    IReadOnlyList<Guid> List(SkillPlanOwner owner);
    SkillPlanDetails Read(SkillPlanOwner owner, Guid planId);
    Guid Create(SkillPlanOwner owner, SkillPlanDetails details);
    void Rename(SkillPlanOwner owner, Guid planId, string name);
    void Describe(SkillPlanOwner owner, Guid planId, string description);
    void SetSkills(SkillPlanOwner owner, Guid planId, IReadOnlyList<SkillPlanSkill> skills);
    void Delete(SkillPlanOwner owner, Guid planId);
    TrackedSkillPlan ReadTracked(SkillPlanOwner owner);
    void Track(SkillPlanOwner owner, Guid planId);
    IReadOnlyList<PersonalSkillMilestone> Milestones(SkillPlanOwner owner, Guid planId);
    Guid AddMilestone(SkillPlanOwner owner, Guid planId, SkillPlanMilestoneTarget target, string description);
    void DescribeMilestone(SkillPlanOwner owner, Guid milestoneId, string description);
    void DeleteMilestone(SkillPlanOwner owner, Guid milestoneId);
}

public static class SkillPlanRules
{
    // Limits exposed by build 3662168 skills.skillplan.skillPlanConst.
    public const int MaxPlans = 10;
    public const int MaxSkills = 150;
    public const int MaxNameLength = 50;
    public const int MaxDescriptionLength = 1_000;
    public const int MaxMilestones = 5;

    public static string NormalizeName(string name)
    {
        name = name.Trim();
        return name.Length is >= 1 and <= MaxNameLength
            ? name
            : throw new InvalidDataException($"Choose a skill plan name of 1 to {MaxNameLength} characters.");
    }

    public static void CheckDescription(string description)
    {
        if (description.Length > MaxDescriptionLength)
        {
            throw new InvalidDataException($"Use at most {MaxDescriptionLength} characters in the description.");
        }
    }

    public static bool ValidSkillLevel(int level) => level is >= 1 and <= 5;
}
