using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Corporations;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Alliances;

[Injectable]
internal sealed partial class AllianceHandler(IAlliances alliances, ICorporationWars wars, CorporationHandler monitor)
    : ISelectedPilotCallHandler
{
    private const int DefaultRankingLimit = 100;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            !(
                service == "allianceRegistry"
                && method
                    is "GetAlliance"
                        or "GetAlliancePublicInfo"
                        or "GetRankedAlliances"
                        or "GetAllianceMembers"
                        or "GetDaysInAlliance"
                        or "GetAllianceMembersOlderThan"
                        or "GetMembers"
                        or "UpdateAlliance"
            ) && !(service == "corpRegistry" && method is "CreateAlliance" or "GetSuggestedAllianceShortNames")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected alliance arguments.");
            }
            switch (method, args)
            {
                case ("CreateAlliance", [var name, var ticker, var description, var url]) when service == "corpRegistry":
                    var created = alliances.Create(context.Pilot.PilotId, new(Text(name), Text(ticker), Text(description), Text(url)));
                    context.Notifications.Add(
                        new AccountBalanceChanged(context.Pilot.PilotId, created.BalanceCents / (double)MarketDefinitions.CentsPerIsk)
                    );
                    return created.Alliance.Id;
                case ("GetSuggestedAllianceShortNames", [var name]) when service == "corpRegistry":
                    return new AllianceTickerSuggestions(alliances.SuggestTickers(Text(name)));
                case ("GetAlliance" or "GetAlliancePublicInfo", [long id]) when id is > 0 and <= int.MaxValue:
                    return Observe((int)id, alliances.PilotAffiliation(context.Pilot.PilotId));
                case ("GetAlliance", []):
                    int current = Current(context);
                    return Observe(current, current);
                case ("GetRankedAlliances", [] or [long]):
                    // The native Show All option sends zero as the requested maximum.
                    int? viewerAlliance = alliances.PilotAffiliation(context.Pilot.PilotId);
                    int limit = args switch
                    {
                        [0L] => int.MaxValue,
                        [long count] => (int)Math.Clamp(count, 1, int.MaxValue),
                        _ => DefaultRankingLimit,
                    };
                    return new AllianceRankingsReply(
                        alliances
                            .List()
                            .Where(a => a.ClosedAt is null)
                            .OrderByDescending(a => a.MemberCount)
                            .ThenBy(a => a.Id)
                            .Take(limit)
                            .Select(alliance => Reply(alliance.ForViewer(viewerAlliance)))
                            .ToArray()
                    );
                case ("GetAllianceMembers", [long id]) when id is > 0 and <= int.MaxValue:
                    Require((int)id);
                    return new AlliancePublicMembersReply(alliances.Members((int)id).Select(member => member.ForViewer(null)).ToArray());
                case ("GetDaysInAlliance", [long id, long corporation])
                    when id is > 0 and <= int.MaxValue && corporation is > 0 and <= int.MaxValue:
                    return alliances.DaysInAlliance((int)id, (int)corporation);
                case ("GetDaysInAlliance", [long id, null]) when id is > 0 and <= int.MaxValue:
                    // The personal donation panel also queries the executor of an alliance with no elected executor.
                    return 0;
                case ("GetAllianceMembersOlderThan", [long id, long days])
                    when id is > 0 and <= int.MaxValue && days is >= 0 and <= int.MaxValue:
                    return new BlueList<int>(alliances.MembersOlderThan((int)id, (int)days));
                case ("GetDaysInAlliance" or "GetAllianceMembersOlderThan", _):
                    throw new InvalidDataException("Choose a valid alliance and corporation or a nonnegative membership age.");
                case ("GetMembers", []):
                    return new AllianceMembersReply(alliances.MembersForPilot(context.Pilot.PilotId));
                case ("UpdateAlliance", [var description, var url]):
                    alliances.Update(context.Pilot.PilotId, Text(description), Text(url));
                    return PilotCallResult.None;
                default:
                    return PilotCallResult.Unsupported;
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private Alliance Require(int id) => alliances.Read(id) ?? throw new InvalidDataException("Unknown alliance.");

    private AllianceReply Reply(Alliance alliance) => new(alliance, wars.IsEligible(alliance.Id));

    private AllianceReply Observe(int id, int? viewerAlliance)
    {
        var alliance = Require(id).ForViewer(viewerAlliance);
        monitor.ObserveAlliance(alliance);
        return Reply(alliance);
    }

    private int Current(PilotCallContext context) =>
        alliances.PilotAffiliation(context.Pilot.PilotId) ?? throw new InvalidDataException("Your corporation is not in an alliance.");

    internal static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected alliance text.");
}
