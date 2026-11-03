using Eden.Gameplay.Characters;

namespace Eden.Protocol.Characters;

internal static class CharacterNameWire
{
    // Build 3662168 ChooseNameSection uses these localized validation outcomes.
    public static int Code(CharacterNameResult result) =>
        result switch
        {
            CharacterNameResult.Available => 1,
            CharacterNameResult.TooShort => -1,
            CharacterNameResult.TooLong => -2,
            CharacterNameResult.InvalidCharacters => -5,
            CharacterNameResult.TooManySpaces => -6,
            CharacterNameResult.ConsecutiveSpaces => -7,
            CharacterNameResult.Taken => -101,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
}
