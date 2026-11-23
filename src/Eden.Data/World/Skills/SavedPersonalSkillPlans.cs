using System.Collections.Immutable;

namespace Eden.Data.World.Skills;

public sealed class SavedPersonalSkillPlans
{
    public long PilotId { get; set; }
    public SavedSkillPlanBook Book { get; set; } = SavedSkillPlanBook.Empty;
}

public sealed record SavedSkillPlanBook(Guid TrackedPlanId, ImmutableArray<SavedPersonalSkillPlan> Plans)
{
    public static SavedSkillPlanBook Empty { get; } = new(Guid.Empty, []);
}

public sealed record SavedPersonalSkillPlan(
    Guid Id,
    string Name,
    string Description,
    ImmutableArray<SavedSkillPlanSkill> Skills,
    ImmutableArray<SavedSkillPlanMilestone> Milestones
);

public readonly record struct SavedSkillPlanSkill(int TypeId, int Level);

public sealed record SavedSkillPlanMilestone(Guid Id, int TypeId, int? Level, string Description);
