using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Skills;

public sealed record SkillExtractionRequest(int TypeId, long Points);

public sealed record SkillExtractionFloor(int TypeId, int Level, string Reason);

public sealed record SkillExtractionState(long Revision, TrainingState Training);

public sealed record SkillExtractionChange(long Revision, TrainingState Training, IReadOnlyList<InventoryChange> Changes);

public interface IPilotSkillExtraction
{
    long Revision(long pilotId);
    SkillExtractionState Snapshot(long pilotId);
    SkillExtractionChange Extract(long pilotId, long itemId, IReadOnlyList<SkillExtractionRequest> requests);
}

public sealed class InvalidSkillExtraction(string reason) : Exception(reason);

public static class SkillExtractionRules
{
    // Build 3662168 appConst, inventorycommon.const and skillExtractorController.
    public const int ExtractorType = 40519;
    public const long Points = 500_000;
    public const long RemainingMinimum = 5_000_000;
    public const int HistoryEvent = 431;

    public static IReadOnlyList<SkillPointAllocation> Plan(
        IReadOnlyList<SkillExtractionRequest> requests,
        TrainingInputs inputs,
        IReadOnlyList<TrainingTarget> queue,
        IReadOnlyList<SkillExtractionFloor> floors
    )
    {
        if (
            requests.Count == 0
            || requests.Select(r => r.TypeId).Distinct().Count() != requests.Count
            || requests.Any(r => r.TypeId <= 0 || r.Points <= 0 || r.Points > Points)
            || requests.Sum(r => r.Points) != Points
        )
        {
            throw new InvalidSkillExtraction("Select exactly 500,000 skill points to extract.");
        }
        if (!Enum.IsDefined(inputs.CloneGrade))
        {
            throw new InvalidDataException("Invalid clone grade.");
        }
        long allocated = 0;
        foreach (var skill in inputs.Skills.Values)
        {
            if (skill.Points < 0 || !double.IsFinite(skill.Rank) || skill.Rank <= 0 || skill.AlphaLevel is < 0 or > 5)
            {
                throw new InvalidDataException("Invalid skill extraction state.");
            }
            allocated = checked(allocated + skill.Points);
        }
        if (allocated < RemainingMinimum + Points)
        {
            throw new InvalidSkillExtraction("SkillExtractionNotEnoughSP");
        }
        Dictionary<int, SkillPointAllocation> changes = [];
        foreach (var request in requests)
        {
            if (!inputs.Skills.TryGetValue(request.TypeId, out var skill) || request.Points > skill.Points)
            {
                throw new InvalidSkillExtraction("You do not have those skill points.");
            }
            if (skill.Points > TrainedSkill.PointsForLevel(skill.Rank, 5))
            {
                throw new InvalidSkillExtraction("Overtrained skills cannot be extracted.");
            }
            if (queue.Any(t => t.TypeId == request.TypeId))
            {
                throw new InvalidSkillExtraction("Skills in your training queue cannot be extracted.");
            }
            long after = skill.Points - request.Points;
            if (inputs.CloneGrade == AccountCloneGrade.Alpha && after < TrainedSkill.PointsForLevel(skill.Rank, skill.AlphaLevel))
            {
                throw new InvalidSkillExtraction("Alpha skill levels cannot be extracted.");
            }
            foreach (var floor in floors.Where(f => f.TypeId == request.TypeId))
            {
                if (floor.Level is < 0 or > 5)
                {
                    throw new InvalidDataException("Invalid skill extraction restriction.");
                }
                if (after < TrainedSkill.PointsForLevel(skill.Rank, floor.Level))
                {
                    throw new InvalidSkillExtraction(floor.Reason);
                }
            }
            changes.Add(request.TypeId, new(request.TypeId, skill.Points, after));
        }
        foreach (var (type, skill) in inputs.Skills)
        {
            // A dependent skill must reach zero in the same extraction before its
            // prerequisite can fall below the required level. Request order is inert.
            long final = changes.TryGetValue(type, out var change) ? change.After : skill.Points;
            if (final == 0 && !queue.Any(t => t.TypeId == type))
            {
                continue;
            }
            foreach (var required in skill.Prerequisites)
            {
                if (
                    changes.TryGetValue(required.TypeId, out var prerequisite)
                    && prerequisite.After < TrainedSkill.PointsForLevel(inputs.Skills[required.TypeId].Rank, required.Level)
                )
                {
                    throw new InvalidSkillExtraction("That skill is required by another trained or queued skill.");
                }
            }
        }
        return changes.Values.ToArray();
    }
}
