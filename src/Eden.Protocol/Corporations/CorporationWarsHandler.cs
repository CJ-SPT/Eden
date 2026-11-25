using Eden.Gameplay.Alliances;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationWarsHandler(
    ICorporationWars wars,
    IUniversalSearch search,
    IPlayerCorporations corporations,
    IAlliances alliances,
    INpcCorporationDirectory npcs,
    CorporationWarMonitor monitor
) : ISelectedPilotCallHandler
{
    private const int MaximumWarOwners = 1000;
    private const int MaximumPublicWars = 1000;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            (service, method)
            is not (

                ("corpRegistry" or "allianceRegistry", "DeclareWarAgainst")
                or
                ("structureDirectory", "GetValidWarHQs")
                or
                ("lookupSvc", "LookupWarableCorporationsOrAlliances")
                or
                ("peaceTreatyMgr", "GetPeaceTreatiesForSession")
                or
                ("warRegistry", "GetWars" or "GetNegotiations" or "RetractMutualWar" or "IsAllianceOrCorpLocal")
                or
                (
                    "warsInfoMgr",
                    "GetWarsByOwnerID"
                        or "GetWarsByOwners"
                        or "GetTop50"
                        or "GetPublicWarInfo"
                        or "GetWarsForStructure"
                        or "GetWarsRequiringAssistance"
                )
                or
                (
                    "mutualWarInviteMgr",
                    "GetPendingInvitesForSession"
                        or "SendInviteByPlayer"
                        or "WithdrawInviteByPlayer"
                        or "RespondToInviteByPlayer"
                        or "IsCorpInvitesBlockedPlayer"
                        or "SetInvitesBlockedByPlayer"
                )
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected war arguments.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("DeclareWarAgainst", [long target, long headquarters]) when OwnerId(target) && headquarters > 0:
                    monitor.Watch(context);
                    var declared = wars.Declare(pilot, (int)target, headquarters, service == "allianceRegistry");
                    monitor.Advance(context);
                    return new CorporationWarReply(declared);
                case ("GetValidWarHQs", []):
                    return Headquarters(pilot, wars.Organization(pilot));
                case ("GetValidWarHQs", [long owner]) when OwnerId(owner):
                    return Headquarters(pilot, (int)owner);
                case ("LookupWarableCorporationsOrAlliances", [var query, var exact]):
                    return Search(BlueRead.Text(query), Flag(exact));
                case ("GetPeaceTreatiesForSession", []):
                    return new CorporationWarPeaceTreatiesReply(wars.PeaceTreaties(pilot));
                case ("IsAllianceOrCorpLocal", []):
                    return true;
                case ("GetWars", []):
                    monitor.Watch(context);
                    return Registry(wars.Wars(wars.Organization(pilot)));
                case ("GetWars", [long owner]) when OwnerId(owner):
                    monitor.Watch(context);
                    return Registry(wars.Wars((int)owner));
                case ("GetNegotiations", []):
                case ("GetWarsRequiringAssistance", []):
                    return BlueEmpty.List;
                case ("GetWarsByOwnerID", [long owner]) when OwnerId(owner):
                    RequireOwner((int)owner);
                    return new CorporationWarHistoryReply(wars.Wars((int)owner));
                case ("GetWarsByOwners", [HashSet<long> owners]) when owners.Count <= MaximumWarOwners && owners.All(OwnerId):
                    return new BlueMap<long, BlueMap<long, CorporationWarReply>>(
                        owners.Order().ToDictionary(owner => owner, owner => Registry(wars.Wars((int)owner)))
                    );
                case ("GetTop50", [long maximum]) when maximum >= 0:
                    return new CorporationWarHistoryReply(wars.PublicWars(maximum));
                case ("GetPublicWarInfo", [long war]) when war > 0:
                    return wars.War(war) is { } publicWar ? new CorporationWarReply(publicWar) : PilotCallResult.None;
                case ("GetPublicWarInfo", [List<object?> warIds]) when warIds.Count <= MaximumPublicWars:
                    return new BlueList<CorporationWarReply>(
                        BlueRead
                            .PositiveIds(warIds, "war IDs")
                            .Distinct()
                            .Select(wars.War)
                            .OfType<CorporationWar>()
                            .Select(war => new CorporationWarReply(war))
                            .ToArray()
                    );
                case ("GetWarsForStructure", [long structure]) when structure > 0:
                    return new BlueList<CorporationWarReply>(
                        wars.WarsForStructure(structure).Select(war => new CorporationWarReply(war)).ToArray()
                    );
                case ("RetractMutualWar", [long war]) when war > 0:
                    monitor.Watch(context);
                    wars.RetractMutual(pilot, war);
                    monitor.Advance(context);
                    return PilotCallResult.None;
                case ("GetPendingInvitesForSession", []):
                    monitor.WatchInvitations(context);
                    return new BlueList<CorporationWarInvitationReply>(
                        wars.Invitations(pilot).Select(invite => new CorporationWarInvitationReply(invite)).ToArray()
                    );
                case ("IsCorpInvitesBlockedPlayer", []):
                    monitor.WatchInvitations(context);
                    return wars.InvitationsBlocked(pilot);
                case ("SendInviteByPlayer", [long target]) when OwnerId(target):
                    monitor.WatchInvitations(context);
                    wars.SendInvitation(pilot, (int)target);
                    break;
                case ("WithdrawInviteByPlayer", [long target]) when OwnerId(target):
                    monitor.WatchInvitations(context);
                    wars.WithdrawInvitation(pilot, (int)target);
                    break;
                case ("RespondToInviteByPlayer", [long source, var accept]) when OwnerId(source):
                    monitor.Watch(context);
                    monitor.WatchInvitations(context);
                    wars.RespondInvitation(pilot, (int)source, Flag(accept));
                    monitor.Advance(context);
                    break;
                case ("SetInvitesBlockedByPlayer", [var blocked]):
                    monitor.WatchInvitations(context);
                    wars.SetInvitationsBlocked(pilot, Flag(blocked));
                    break;
                default:
                    throw new InvalidDataException("Choose valid war arguments.");
            }
            monitor.AdvanceInvitations(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private BlueList<CorporationWarHeadquartersReply> Headquarters(long pilot, int owner) =>
        new(wars.Headquarters(pilot, owner).Select(headquarters => new CorporationWarHeadquartersReply(headquarters)).ToArray());

    private BlueList<CorporationWarOwnerReply> Search(string query, bool exact)
    {
        var matches = search.Search(
            new(
                query,
                [SearchCategory.Corporation, SearchCategory.Alliance],
                exact ? CharacterSearchMatch.ExactName : CharacterSearchMatch.PartialTerms,
                HideNpc: true
            )
        );
        var owners = matches
            .SelectMany(group => group.Ids.Select(id => SearchOwner(checked((int)id), group.Category)))
            .OfType<CorporationWarOwnerReply>()
            .ToArray();
        return new(owners);
    }

    private CorporationWarOwnerReply? SearchOwner(int id, SearchCategory category) =>
        category switch
        {
            SearchCategory.Corporation when corporations.Read(id) is { ClosedAt: null } corporation => new(
                id,
                corporation.Details.Name,
                false,
                wars.IsEligible(id)
            ),
            SearchCategory.Alliance when alliances.Read(id) is { ClosedAt: null } alliance => new(
                id,
                alliance.Details.Name,
                true,
                wars.IsEligible(id)
            ),
            _ => null,
        };

    private void RequireOwner(int id)
    {
        if (corporations.Read(id) is null && alliances.Read(id) is null)
        {
            _ = npcs.Read(id);
        }
    }

    private static BlueMap<long, CorporationWarReply> Registry(IReadOnlyList<CorporationWar> entries) =>
        new(entries.ToDictionary(war => war.Id, war => new CorporationWarReply(war)));

    private static bool OwnerId(long id) => id is > 0 and <= int.MaxValue;

    private static bool Flag(object? value) =>
        value switch
        {
            bool flag => flag,
            0L => false,
            1L => true,
            _ => throw new InvalidDataException("Choose a valid war option."),
        };
}
