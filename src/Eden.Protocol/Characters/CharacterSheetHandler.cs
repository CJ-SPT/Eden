using Eden.Gameplay.Agents;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Corporations;
using Eden.Protocol.FactionWarfare;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterSheetHandler(
    IPilotCharacterSheet sheet,
    INpcDirectory npcs,
    IPlayerCorporations corporations,
    ICorporationRoles corporationRoles,
    IAlliances alliances,
    ICharacterDirectory directory,
    ICorporationMedals medals,
    IMilitiaProgression? militia = null
) : ISelectedPilotCallHandler
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
        if (service == "charMgr" && method == "GetCharacterCreationDate")
        {
            return args.Length == 0 && keywords is null or { Count: 0 }
                ? pilot.Identity.Profile.CreatedAt
                : new PilotCallResult.Failure("The selected character's creation date does not accept arguments.");
        }
        if (service == "charMgr" && method == "GetOrganizationInfoForCharacters")
        {
            if (
                keywords is { Count: > 0 }
                || args is not [List<object?> ids]
                || ids.Count > ContactRules.MaximumBatchSize
                || ids.Any(id => id is not (long and > 0))
            )
            {
                return new PilotCallResult.Failure("Supply a list of positive character IDs.");
            }
            var requested = ids.Cast<long>().Distinct().ToArray();
            var affiliations = directory.Affiliations(requested);
            return new BlueMap<long, CharacterAffiliationReply>(
                requested.ToDictionary(id => id, id => new CharacterAffiliationReply(affiliations.GetValueOrDefault(id)))
            );
        }
        if (service == "charMgr" && method == "GetPrivateInfo")
        {
            if (keywords is { Count: > 0 } || args is not [long character] || character <= 0)
            {
                return new PilotCallResult.Failure("Invalid member character request.");
            }
            var profile = sheet.ReadPublicProfile(character);
            if (profile is null)
            {
                return new PilotCallResult.Failure("Unknown character.");
            }
            if (character != pilot.PilotId && profile.CorporationId != corporations.Identity(pilot.PilotId).Id)
            {
                return new PilotCallResult.Failure("That pilot is not a member of your corporation.");
            }
            return new PrivateCharacterInfo(profile);
        }
        if (service == "charMgr" && method is "GetCharacterDescription" or "SetCharacterDescription")
        {
            try
            {
                if (method == "GetCharacterDescription" && args is [long character])
                {
                    if (npcs.Character(character) is { } npc)
                    {
                        return new CharacterBiographyReply(npc.Description);
                    }
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
        if (service == "facWarMgr" && method is "GetMyCharacterRankInfo" or "GetMyCharacterRankOverview")
        {
            if (args.Length != 0 || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Invalid public character request.");
            }
            return method == "GetMyCharacterRankOverview"
                ? new MilitiaRankOverview(militia?.Ranks(pilot.PilotId) ?? [])
                : Rank(pilot.PilotId);
        }
        if (
            (service == "charMgr" && method is "GetPublicInfo" or "GetPublicInfo3")
            || (service == "corpRegistry" && method == "GetInfoWindowDataForChar")
            || (
                service == "corporationSvc"
                && method is "GetMedalsReceived" or "GetEmployementRecordAndCharacterTransfers" or "GetEmploymentRecord"
            )
            || (service == "facWarMgr" && method is "GetCharacterRankInfo" or "GetCharacterRankOverview")
        )
        {
            if (keywords is { Count: > 0 } || args is not [long id] || id <= 0)
            {
                return new PilotCallResult.Failure("Invalid public character request.");
            }
            var profile = sheet.ReadPublicProfile(id);
            bool player = profile is not null;
            if (profile is null && npcs.Character(id) is { } npc)
            {
                if (method == "GetPublicInfo")
                {
                    return new PublicNpcInfo(npc);
                }
                profile = new(
                    npc.Identity.Id,
                    npc.Identity.Name,
                    npc.CorporationId,
                    npc.RaceId,
                    npc.BloodlineId,
                    npc.Identity.Gender,
                    npc.SchoolId,
                    npc.AncestryId,
                    npc.CreatedAt,
                    npc.Description
                );
            }
            if (profile is null)
            {
                return new PilotCallResult.Failure("Unknown character.");
            }
            return method switch
            {
                "GetPublicInfo3" => new PublicCharacterInfo3(profile),
                "GetInfoWindowDataForChar" => player
                    ? PublicCorporationInfo(profile)
                    : new CharacterCorporationInfo(profile.CorporationId, AllianceId: alliances.Affiliation(profile.CorporationId)),
                "GetMedalsReceived" => ReceivedMedalsReply.Create(medals.Received(pilot.PilotId, id)),
                "GetEmploymentRecord" => new CharacterEmploymentHistory(
                    player ? corporations.Employment(id)
                    : profile.CreatedAt is long born ? [new(profile.CorporationId, born)]
                    : []
                ),
                "GetEmployementRecordAndCharacterTransfers" => new PublicCharacterEmployment(
                    profile,
                    player ? corporations.Employment(id) : null
                ),
                "GetCharacterRankInfo" => player ? Rank(id) : PilotCallResult.None,
                "GetCharacterRankOverview" => new MilitiaRankOverview(player ? militia?.Ranks(id) ?? [] : []),
                _ => new PublicCharacterInfo(profile),
            };
        }
        return PilotCallResult.Unsupported;
    }

    private PilotCallResult Rank(long id) => militia?.Rank(id) is { } rank ? new MilitiaRankReply(rank) : PilotCallResult.None;

    private CharacterCorporationInfo PublicCorporationInfo(PublicCharacterProfile profile)
    {
        long pilotId = profile.CharacterId;
        int corporationId = profile.CorporationId;
        var member = corporations.Members(pilotId).Single(m => m.PilotId == pilotId);
        long mask = member.Access?.TitleMask ?? 0;
        return new(
            corporationId,
            member.Access?.Title ?? "",
            corporationRoles.Titles(pilotId).Where(t => (mask & t.Id) != 0).ToArray(),
            alliances.Affiliation(corporationId),
            (int?)profile.MilitiaFaction
        );
    }
}
