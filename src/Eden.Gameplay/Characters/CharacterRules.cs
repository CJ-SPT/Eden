namespace Eden.Gameplay.Characters;

public enum CharacterNameResult
{
    Available,
    TooShort,
    TooLong,
    InvalidCharacters,
    TooManySpaces,
    ConsecutiveSpaces,
    Taken,
}

public sealed record CharacterOrigin(
    int SchoolRace,
    int BloodlineRace,
    long ShipTypeId,
    long CorporationId,
    IReadOnlyList<long> StartingStations,
    long? AncestryBloodline
);

public static class CharacterRules
{
    public const int UnspecifiedAncestry = 64;
    private const int MinimumNameLength = 3;
    private const int MaximumNameLength = 37;
    private const int MaximumNameSpaces = 2;

    public static CharacterNameResult ValidateName(string name, bool taken)
    {
        if (name.Length < MinimumNameLength)
        {
            return CharacterNameResult.TooShort;
        }
        if (name.Length > MaximumNameLength)
        {
            return CharacterNameResult.TooLong;
        }
        if (name.Trim() != name || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '\'' or '-')))
        {
            return CharacterNameResult.InvalidCharacters;
        }
        if (name.Count(c => c == ' ') > MaximumNameSpaces)
        {
            return CharacterNameResult.TooManySpaces;
        }
        if (name.Contains("  ", StringComparison.Ordinal))
        {
            return CharacterNameResult.ConsecutiveSpaces;
        }
        return taken ? CharacterNameResult.Taken : CharacterNameResult.Available;
    }

    public static void ValidateOrigin(NewCharacter character, CharacterOrigin origin)
    {
        if (
            origin.SchoolRace != character.RaceId
            || origin.BloodlineRace != character.RaceId
            || origin.ShipTypeId <= 0
            || origin.CorporationId <= 0
            || origin.StartingStations.Count == 0
            || character.GenderId is not (0 or 1)
            || (character.AncestryId != UnspecifiedAncestry && origin.AncestryBloodline != character.BloodlineId)
        )
        {
            throw new InvalidDataException("Invalid character origin.");
        }
    }
}
