namespace Eden.Gameplay.Characters;

public abstract record CharacterSettingValue
{
    public sealed record Text(string Value) : CharacterSettingValue;

    public sealed record Binary(byte[] Value) : CharacterSettingValue;
}

public sealed record CharacterSetting(string Key, CharacterSettingValue Value);

public interface IPilotCharacterSettings
{
    IReadOnlyList<CharacterSetting> Read(long pilotId);
    void Save(long pilotId, CharacterSetting setting);
    void Delete(long pilotId, string key);
}

public static class CharacterSettingsRules
{
    // CharacterSettingsSvc.Save in build 3662168 caps each serialized value.
    public const int MaximumValueLength = 102400;

    // Eden bounds opaque setting names; client names fit comfortably within this limit.
    public const int MaximumKeyLength = 128;

    public static void CheckKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumKeyLength || key.Contains('\0'))
        {
            throw new InvalidDataException("Choose a valid character setting name.");
        }
    }

    public static void Check(CharacterSetting setting)
    {
        CheckKey(setting.Key);
        int length = setting.Value switch
        {
            CharacterSettingValue.Text text => text.Value.Length,
            CharacterSettingValue.Binary binary => binary.Value.Length,
            _ => throw new InvalidDataException("Expected a text or binary character setting."),
        };
        if (length > MaximumValueLength)
        {
            throw new InvalidDataException($"Character settings must contain at most {MaximumValueLength} characters or bytes.");
        }
    }
}
