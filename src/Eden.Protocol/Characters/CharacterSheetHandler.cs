using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterSheetHandler(ICharacterCreation creation) : ISelectedPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        var pilot = context.Pilot;
        if (service == "paperDollServer" && method == "GetPaperDollData" && args is [long characterId] && characterId == pilot.PilotId)
        {
            return creation.ReadAppearance(characterId, false) is { } data ? BlueMarshal.Decode(data) : null;
        }
        if (service == "paperDollServer" && method == "GetPaperDollPortraitDataFor" && args is [long portraitId] && portraitId == pilot.PilotId)
        {
            var data = creation.ReadAppearance(portraitId, true);
            return new object?[] { data is null ? new List<object?>() : new List<object?> { BlueMarshal.Decode(data) }, null };
        }
        if (service == "charMgr" && method == "GetPublicInfo" && args is [long id] && id == pilot.PilotId)
        {
            var profile = pilot.Profile;
            return new BlueInstance("utillib.KeyVal", new Dictionary<string, object?>
            {
                ["characterID"] = id,
                ["characterName"] = pilot.Name,
                ["corporationID"] = pilot.CorpId,
                ["raceID"] = profile.RaceId,
                ["bloodlineID"] = profile.BloodlineId,
                ["gender"] = profile.GenderId,
                ["schoolID"] = profile.SchoolId,
                ["ancestryID"] = profile.AncestryId,
                ["createDateTime"] = profile.CreatedAt,
            });
        }
        return PilotCallRouter.Unsupported;
    }
}
