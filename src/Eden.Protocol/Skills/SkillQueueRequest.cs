using Eden.Gameplay.Skills;

namespace Eden.Protocol.Skills;

internal sealed record SkillQueueRequest(IReadOnlyList<TrainingTarget> Targets, bool Activate)
{
    internal static SkillQueueRequest? Read(object?[] args, Dictionary<string, object?>? keywords)
    {
        if (args.Length != 1)
        {
            return null;
        }
        // Python's empty dictionary has no key-type information.
        var entries = args[0] switch
        {
            Dictionary<long, object?> map => map,
            Dictionary<string, object?> { Count: 0 } => [],
            _ => throw new InvalidTrainingQueue("QueueInvalidPosition"),
        };
        if (entries.Count > 150)
        {
            throw new InvalidTrainingQueue("QueueTooManySkills");
        }
        List<TrainingTarget> targets = [];
        for (int index = 0; index < entries.Count; index++)
        {
            if (
                entries.GetValueOrDefault(index) is not object?[] pair
                || pair is not [long typeId, long level]
                || typeId is < 1 or > int.MaxValue
                || level is < 1 or > 5
            )
            {
                throw new InvalidTrainingQueue("QueueInvalidPosition");
            }
            targets.Add(new((int)typeId, (int)level));
        }
        bool activate = keywords?.GetValueOrDefault("activate") switch
        {
            true or 1L => true,
            null or false or 0L => false,
            _ => throw new InvalidTrainingQueue("Invalid activation flag"),
        };
        return new(targets, activate);
    }
}
