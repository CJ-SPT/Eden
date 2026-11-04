using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterSettingsHandler(IPilotCharacterSettings settings) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "charMgr" || method is not ("GetCharacterSettings" or "SaveCharacterSetting" or "DeleteCharacterSetting"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected character setting arguments.");
            }
            if (method == "GetCharacterSettings" && args.Length == 0)
            {
                return new CharacterSettingsReply(settings.Read(context.Pilot.PilotId));
            }
            if (method == "SaveCharacterSetting" && args is [var key, var value] && key is string or byte[])
            {
                CharacterSettingValue parsed = value switch
                {
                    string text => new CharacterSettingValue.Text(text),
                    byte[] binary => new CharacterSettingValue.Binary(binary),
                    _ => throw new InvalidDataException("Expected a text or binary character setting."),
                };
                settings.Save(context.Pilot.PilotId, new(BlueMarshal.Text(key), parsed));
                return PilotCallResult.None;
            }
            if (method == "DeleteCharacterSetting" && args is [var name] && name is string or byte[])
            {
                settings.Delete(context.Pilot.PilotId, BlueMarshal.Text(name));
                return PilotCallResult.None;
            }
            return new PilotCallResult.Failure("Invalid character setting request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CharacterSettingsReply(IReadOnlyList<CharacterSetting> Settings) : BlueModel
{
    public override object ToBlue() =>
        Settings.ToDictionary(
            s => s.Key,
            s =>
                s.Value switch
                {
                    CharacterSettingValue.Text text => (object?)new BlueUnicode(text.Value),
                    CharacterSettingValue.Binary binary => binary.Value,
                    _ => throw new InvalidDataException("Invalid saved character setting."),
                }
        );
}
