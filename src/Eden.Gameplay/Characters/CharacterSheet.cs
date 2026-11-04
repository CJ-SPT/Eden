using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Characters;

public sealed record CharacterAttributes(int Intelligence, int Memory, int Charisma, int Perception, int Willpower)
{
    public static CharacterAttributes Default { get; } = new(20, 20, 19, 20, 20);

    public IReadOnlyDictionary<DogmaAttributeId, int> ToDogma() =>
        new Dictionary<DogmaAttributeId, int>
        {
            [DogmaAttributeId.Intelligence] = Intelligence,
            [DogmaAttributeId.Memory] = Memory,
            [DogmaAttributeId.Charisma] = Charisma,
            [DogmaAttributeId.Perception] = Perception,
            [DogmaAttributeId.Willpower] = Willpower,
        };
}

public sealed record NeuralRemapState(CharacterAttributes Attributes, int BonusRemaps, long? NextTimedRemap)
{
    public static NeuralRemapState Default { get; } = new(CharacterAttributes.Default, CharacterSheetRules.StartingBonusRemaps, null);
}

public sealed record CharacterRemapResult(TrainingState Training, bool Changed);

public interface IPilotCharacterSheet
{
    NeuralRemapState ReadAttributes(long pilotId);
    CharacterRemapResult Remap(long pilotId, CharacterAttributes attributes);
    string ReadBiography(long pilotId);
    void SaveBiography(long pilotId, string biography);
}

public sealed class InvalidCharacterSheet(string reason, long? nextTime = null) : Exception(reason)
{
    public long? NextTime { get; } = nextTime;
}

public static class CharacterSheetRules
{
    // Build 3662168 appConst and charsheet/bioPanel.py.
    public const int MinimumAttribute = 17;
    public const int MaximumAttribute = 27;
    public const int TotalAttributes = 99;
    public const int MaximumBiographyLength = 2500;
    public const int StartingBonusRemaps = 2;
    public static TimeSpan RemapInterval { get; } = TimeSpan.FromDays(365);

    public static NeuralRemapState Remap(NeuralRemapState current, CharacterAttributes attributes, long now)
    {
        var values = attributes.ToDogma().Values;
        if (values.Any(value => value < MinimumAttribute))
        {
            throw new InvalidCharacterSheet("RespecAttributesTooLow");
        }
        if (values.Any(value => value > MaximumAttribute))
        {
            throw new InvalidCharacterSheet("RespecAttributesTooHigh");
        }
        if (values.Sum() != TotalAttributes)
        {
            throw new InvalidCharacterSheet("RespecAttributesMisallocated");
        }
        if (attributes == current.Attributes)
        {
            return current;
        }
        if (current.NextTimedRemap is null || current.NextTimedRemap <= now)
        {
            return current with { Attributes = attributes, NextTimedRemap = checked(now + RemapInterval.Ticks) };
        }
        if (current.BonusRemaps == 0)
        {
            throw new InvalidCharacterSheet("RespecTooSoon", current.NextTimedRemap);
        }
        return current with { Attributes = attributes, BonusRemaps = current.BonusRemaps - 1 };
    }

    public static void CheckBiography(string biography)
    {
        if (biography.Length > MaximumBiographyLength || biography.Contains('\0'))
        {
            throw new InvalidDataException($"Biography must contain at most {MaximumBiographyLength} characters and no nulls.");
        }
    }
}
