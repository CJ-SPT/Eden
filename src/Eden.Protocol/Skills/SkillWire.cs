using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

internal sealed record TrainedSkillReply(TrainedSkill Skill) : IBlueWritable
{
    public object ToBlue() =>
        new BlueConstructor(
            "characterskills.common.character_skill_entry.CharacterSkillEntry",
            [Skill.TypeId, Skill.Level, Skill.Points, Skill.Rank, null]
        );
}

internal sealed record SkillQueueRow(int Position, TrainingEntry Entry) : IBlueWritable
{
    public object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["queuePosition"] = Position,
                ["trainingStartTime"] = Entry.StartTime,
                ["trainingEndTime"] = Entry.EndTime,
                ["trainingTypeID"] = Entry.Target.TypeId,
                ["trainingToLevel"] = Entry.Target.Level,
                ["trainingStartSP"] = Entry.StartPoints,
                ["trainingDestinationSP"] = Entry.EndPoints,
            }
        );
}

internal sealed record SkillQueueReply(IReadOnlyList<SkillQueueRow> Queue) : IBlueWritable
{
    public object ToBlue() => new object?[] { new BlueList<SkillQueueRow>(Queue), 0 };
}

internal sealed record SkillHistoryRow(TrainingEvent Event) : IBlueWritable
{
    public object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["logDate"] = Event.Time,
                ["eventTypeID"] = Event.EventId,
                ["skillTypeID"] = Event.TypeId,
                ["absolutePoints"] = Event.Points,
            }
        );
}

internal sealed record ServerSkillsChanged(BlueMap<int, TrainedSkillReply> Skills, long Time) : PilotNotification("OnServerSkillsChanged")
{
    public override object?[] Arguments => [Skills.Entries.ToDictionary(p => (long)p.Key, p => (object?)p.Value.ToBlue()), null, Time];
}

internal sealed record SkillQueueSaved(IReadOnlyList<SkillQueueRow> Queue) : PilotNotification("OnNewSkillQueueSaved")
{
    public override object?[] Arguments => [Queue.Select(row => (object?)row.ToBlue()).ToList()];
}

internal sealed record SkillQueuePaused() : PilotNotification("OnSkillQueuePausedServer")
{
    public override object?[] Arguments => [];
}

internal enum BrainModifier
{
    Item,
    LocationRequiredSkill,
    LocationGroup,
}

internal sealed record BrainEffect(
    long TargetId,
    BrainModifier Modifier,
    DogmaAttributeId Attribute,
    BrainOperation Operation,
    IReadOnlyList<int> Extras,
    IReadOnlyList<int> Skills,
    double Value
) : IBlueWritable
{
    public object ToBlue() =>
        new BlueNewObject(
            BlueNewObject.BrainEffectClass,
            new Dictionary<string, object?>
            {
                ["fromAttrib"] = null,
                ["toItemID"] = TargetId,
                ["modifierType"] = Modifier switch
                {
                    BrainModifier.Item => "M",
                    BrainModifier.LocationRequiredSkill => "LRS",
                    BrainModifier.LocationGroup => "LG",
                    _ => throw new InvalidOperationException("Unknown brain modifier."),
                },
                ["toAttribID"] = (int)Attribute,
                ["operation"] = (int)Operation,
                ["extras"] = Extras.Select(value => (object?)value).ToArray(),
                ["skills"] = new BlueList<int>(Skills),
                ["value"] = Value,
            }
        );
}

internal sealed record PilotBrainState(
    long Version,
    IReadOnlyList<BrainEffect> CharacterEffects,
    IReadOnlyList<BrainEffect> ShipEffects,
    IReadOnlyList<BrainEffect> StructureEffects
) : IBlueWritable
{
    public object ToBlue() =>
        new object?[]
        {
            Version,
            new BlueList<BrainEffect>(CharacterEffects),
            new BlueList<BrainEffect>(ShipEffects),
            new BlueList<BrainEffect>(StructureEffects),
        };

    public BrainUpdate Update() =>
        new(
            Version,
            BlueMarshal.Encode(
                new object?[]
                {
                    new BlueList<BrainEffect>(CharacterEffects),
                    new BlueList<BrainEffect>(ShipEffects),
                    new BlueList<BrainEffect>(StructureEffects),
                }
            )
        );
}

internal sealed record BrainUpdate(long Version, byte[] GrayMatter) : IBlueWritable
{
    public object ToBlue() => new object?[] { Version, GrayMatter };
}

internal sealed record ServerBrainUpdated(BrainUpdate Brain) : PilotNotification("OnServerBrainUpdated")
{
    public override object?[] Arguments => [Brain.ToBlue()];
}
