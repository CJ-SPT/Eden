using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

internal sealed record NeuralRemapReply(NeuralRemapState State) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?> { ["freeRespecs"] = State.BonusRemaps, ["nextTimedRespec"] = State.NextTimedRemap };
}

internal sealed record RemapErrorValues(long? NextTime) : BlueModel
{
    public override object ToBlue() =>
        NextTime is long next ? new Dictionary<string, object?> { ["nextTime"] = next } : new Dictionary<string, object?>();
}

internal sealed record RespecInfoChanged() : PilotNotification("OnRespecInfoChanged")
{
    public override object?[] Arguments => [];
}

internal static class CharacterAttributesWire
{
    internal static CharacterAttributes Read(object?[] args)
    {
        if (args is not [Dictionary<long, object?> values] || values.Count != CharacterAttributes.Default.ToDogma().Count)
        {
            throw new InvalidDataException("Expected the five neural attributes.");
        }
        int Attribute(DogmaAttributeId id) =>
            values.GetValueOrDefault((long)id) is long value && value is >= int.MinValue and <= int.MaxValue
                ? (int)value
                : throw new InvalidDataException("Expected an integer neural attribute.");
        return new(
            Attribute(DogmaAttributeId.Intelligence),
            Attribute(DogmaAttributeId.Memory),
            Attribute(DogmaAttributeId.Charisma),
            Attribute(DogmaAttributeId.Perception),
            Attribute(DogmaAttributeId.Willpower)
        );
    }
}
