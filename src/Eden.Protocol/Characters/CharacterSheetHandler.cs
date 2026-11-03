using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterSheetHandler(ICharacterCreation creation) : ISelectedPilotCallHandler
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
