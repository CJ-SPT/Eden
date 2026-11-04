using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterSheetHandler(ICharacterCreation creation, IPilotCharacterSheet sheet) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        var pilot = context.Pilot;
        if (service == "charMgr" && method is "GetCharacterDescription" or "SetCharacterDescription")
        {
            try
            {
                if (method == "GetCharacterDescription" && args is [long character])
                {
                    return new CharacterBiographyReply(sheet.ReadBiography(character));
                }
                if (method == "SetCharacterDescription" && args.Length == 1 && args[0] is string or byte[])
                {
                    // This call has no target ID: only the selected pilot can be edited.
                    sheet.SaveBiography(pilot.PilotId, BlueMarshal.Text(args[0]));
                    return PilotCallResult.None;
                }
                return new PilotCallResult.Failure("Invalid biography request.");
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        if (service == "paperDollServer" && method == "GetPaperDollData" && args is [long characterId] && characterId == pilot.PilotId)
        {
            return creation.ReadAppearance(characterId, false) is { } data ? new CharacterAppearanceReply(data) : PilotCallResult.None;
        }
        if (
            service == "paperDollServer"
            && method == "GetPaperDollPortraitDataFor"
            && args is [long portraitId]
            && portraitId == pilot.PilotId
        )
        {
            var data = creation.ReadAppearance(portraitId, true);
            return new CharacterPortraitReply(data);
        }
        if (service == "charMgr" && method == "GetPublicInfo" && args is [long id] && id == pilot.PilotId)
        {
            return new PublicCharacterInfo(pilot);
        }
        return PilotCallResult.Unsupported;
    }
}
