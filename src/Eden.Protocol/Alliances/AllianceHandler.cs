using Eden.Gameplay.Alliances;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Alliances;

[Injectable]
internal sealed partial class AllianceHandler(IAlliances alliances) : ISelectedPilotCallHandler
{
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
                    return new AllianceReply(Require((int)id));
                case ("GetAlliance", []):
                    return new AllianceReply(Require(Current(context)));
                case ("GetRankedAlliances", [] or [long]):
                    int limit = args is [long count] ? checked((int)Math.Clamp(count, 1, 1000)) : 100;
                    return new AllianceRankingsReply(
                        alliances
                            .List()
                            .Where(a => a.ClosedAt is null)
                            .OrderByDescending(a => a.MemberCount)
                            .ThenBy(a => a.Id)
                            .Take(limit)
                            .ToArray()
                    );
                case ("GetAllianceMembers", [long id]) when id is > 0 and <= int.MaxValue:
                    Require((int)id);
                    return new AlliancePublicMembersReply(alliances.Members((int)id));
                case ("GetMembers", []):
                    return new AllianceMembersReply(alliances.Members(Current(context)));
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

    private int Current(PilotCallContext context) =>
        alliances.Affiliation(context.Pilot.CorpId) ?? throw new InvalidDataException("Your corporation is not in an alliance.");

    internal static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected alliance text.");
}
