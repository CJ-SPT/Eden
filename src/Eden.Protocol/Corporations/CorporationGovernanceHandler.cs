using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationGovernanceHandler(ICorporationVoting voting, ICorporationMemberRemovals removals)
    : ISelectedPilotCallHandler
{
    // Build 3662168 corporation.voting requests both statuses when viewing another corporation's cases.
    private const long AllVoteCaseStatuses = 0;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            !(service == "corpRegistry" && method == "GetPendingAutoKicks")
            && (
                service != "voteManager"
                || method
                    is not (
                        "CanViewVotes"
                        or "CanRunForCEO"
                        or "GetVoteCasesByCorporation"
                        or "GetVoteCase"
                        or "GetVoteCaseOptions"
                        or "GetVotes"
                        or "InsertVoteCase"
                        or "InsertVote"
                        or "GetCorpSanctionedActions"
                        or "ActivateSanctionedAction"
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
                throw new InvalidDataException("Unexpected voting arguments.");
            }
            long actor = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetPendingAutoKicks", []):
                    return new CorporationPendingExpulsionsReply(removals.Pending(actor));
                case ("CanViewVotes", [long corporation]) when corporation is > 0 and <= int.MaxValue:
                    return voting.CanView(actor, (int)corporation);
                case ("CanRunForCEO", []):
                    return voting.CanRunForCeo(actor);
                case ("GetVoteCasesByCorporation", [long corporation, .. var statuses])
                    when corporation is > 0 and <= int.MaxValue && statuses is [] or [AllVoteCaseStatuses]:
                    return new CorporationVoteCasesReply(voting.List(actor, (int)corporation));
                case ("GetVotes", [long id]) when id > 0:
                    return new CorporationBallotsReply(id, voting.Ballots(actor, id));
                case ("GetVoteCase" or "GetVoteCaseOptions" or "GetVotes", [long corporation, long id])
                    when corporation is > 0 and <= int.MaxValue && id > 0:
                    var vote = voting.Read(actor, (int)corporation, id);
                    return method switch
                    {
                        "GetVoteCase" => new CorporationVoteRow(vote),
                        "GetVoteCaseOptions" => new CorporationVoteOptions(vote),
                        _ => new CorporationBallotsReply(vote.Id, vote.Data.Ballots.Where(ballot => ballot.PilotId == actor).ToArray()),
                    };
                case ("InsertVoteCase", [var title, var description, long type, var options, long days])
                    when type is >= 0 and <= 6 && Enum.IsDefined(typeof(CorporationVoteType), (int)type) && days is >= 1 and <= 30:
                    return voting.Create(actor, Draft(Text(title), Text(description), (CorporationVoteType)type, (int)days, options));
                case ("InsertVote", [long corporation, long id, long option])
                    when corporation is > 0 and <= int.MaxValue && id > 0 && option is >= 0 and < CorporationVotingRules.MaximumOptions:
                    voting.Cast(actor, (int)corporation, id, (int)option);
                    return PilotCallResult.None;
                case ("GetCorpSanctionedActions", []):
                    return new CorporationSanctionsReply(voting.Actions(actor));
                case ("ActivateSanctionedAction", [long id]) when id > 0:
                    voting.Activate(actor, id);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid corporation voting request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static CorporationVoteDraft Draft(string title, string description, CorporationVoteType type, int days, object? options)
    {
        if (type == CorporationVoteType.General && options is List<object?> labels)
        {
            return new(title, description, type, days, labels.Select(Text).ToArray());
        }
        var tuple = BlueRead.Tuple(options, 5);
        // Native binary votes send localization labels. Only the numeric payload selects an action.
        foreach (var label in tuple.Take(2))
        {
            if (
                label is not object?[] parts
                || parts.Length is < 1 or > 2
                || parts[0] is not (string or BlueUnicode or byte[])
                || parts.Length == 2 && parts[1] is not Dictionary<string, object?>
            )
            {
                throw new InvalidDataException("Invalid vote option label.");
            }
        }
        static long Parameter(object? value) =>
            value switch
            {
                null => 0,
                long number when number >= 0 => number,
                _ => throw new InvalidDataException("Invalid vote parameter."),
            };
        return new(title, description, type, days, ["Yes", "No"], Parameter(tuple[2]), Parameter(tuple[3]), Parameter(tuple[4]));
    }

    private static string Text(object? value) =>
        value is string or BlueUnicode or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected text.");
}
