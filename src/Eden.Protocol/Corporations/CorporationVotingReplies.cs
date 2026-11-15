using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

internal sealed record CorporationVoteRow(CorporationVoteCase Vote) : BlueModel
{
    internal static BlueConstructor Header() =>
        BlueRows.Descriptor([
            ("voteCaseID", BlueColumnType.Int64),
            ("voteCaseText", BlueColumnType.Unicode),
            ("description", BlueColumnType.Unicode),
            ("corporationID", BlueColumnType.Int32),
            ("createdByCharacterID", BlueColumnType.Int64),
            ("voteType", BlueColumnType.Int32),
            ("startDateTime", BlueColumnType.FileTime),
            ("endDateTime", BlueColumnType.FileTime),
            ("processed", BlueColumnType.Boolean),
        ]);

    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                Header(),
                new object?[]
                {
                    Vote.Id,
                    new BlueUnicode(Vote.Data.Draft.Title),
                    new BlueUnicode(Vote.Data.Draft.Description),
                    Vote.CorporationId,
                    Vote.Data.CreatorId,
                    (int)Vote.Data.Draft.Type,
                    Vote.StartedAt,
                    Vote.EndsAt,
                    Vote.Data.Settled,
                },
            ]
        );
}

internal sealed record CorporationVoteCasesReply(CorporationVoteListing Votes) : BlueModel
{
    private static object Rows(IReadOnlyList<CorporationVoteCase> votes) =>
        new BlueConstructor(
            "carbon.common.script.sys.crowset.CRowset",
            [CorporationVoteRow.Header(), votes.Select(vote => (object?)new CorporationVoteRow(vote).ToBlue()).ToList()]
        );

    public override object ToBlue() => new Dictionary<long, object?> { [2] = Rows(Votes.Open), [1] = Rows(Votes.Closed) };
}

internal sealed record CorporationVoteOptions(CorporationVoteCase Vote) : BlueModel
{
    public override object ToBlue() =>
        Vote
            .Data.Draft.Options.Select(
                (text, index) =>
                    (object?)
                        new BlueInstance(
                            "utillib.KeyVal",
                            new Dictionary<string, object?>
                            {
                                ["optionID"] = index,
                                ["optionText"] = new BlueUnicode(Vote.Data.Draft.Type == CorporationVoteType.General ? text : ""),
                                ["parameter"] = index == 0 ? Vote.Data.Draft.Parameter : 0,
                                ["parameter1"] = Vote.Data.Draft.Parameter1,
                                ["parameter2"] = Vote.Data.Draft.Parameter2,
                                ["votesFor"] = Vote.Data.Ballots.Where(ballot => ballot.OptionId == index).Sum(ballot => ballot.Weight),
                            }
                        )
            )
            .ToList();
}

internal sealed record CorporationBallotsReply(long CaseId, IReadOnlyList<CorporationBallot> Ballots) : BlueModel
{
    public override object ToBlue() =>
        Ballots
            .Select(ballot =>
                (object?)
                    new BlueInstance(
                        "utillib.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["voteCaseID"] = CaseId,
                            ["characterID"] = ballot.PilotId,
                            ["optionID"] = ballot.OptionId,
                            ["castDateTime"] = ballot.Time,
                            ["voteWeight"] = ballot.Weight,
                        }
                    )
            )
            .ToList();
}

internal sealed record CorporationSanctionsReply(IReadOnlyList<CorporationVoteCase> Votes) : BlueModel
{
    public override object ToBlue() =>
        Votes.ToDictionary(
            vote => vote.Id,
            vote =>
                (object?)
                    new BlueInstance(
                        "utillib.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["voteCaseID"] = vote.Id,
                            ["voteType"] = (int)vote.Data.Draft.Type,
                            ["optionID"] = 0,
                            ["parameter"] = vote.Data.Draft.Parameter,
                            ["parameter1"] = vote.Data.Draft.Parameter1,
                            ["parameter2"] = vote.Data.Draft.Parameter2,
                            ["expires"] = CorporationVotingRules.Expires(vote),
                            ["actedUpon"] = vote.Data.AppliedAt.HasValue ? 1 : 0,
                            ["inEffect"] = vote.Data.AppliedAt.HasValue ? 1 : 0,
                            ["timeActedUpon"] = vote.Data.AppliedAt,
                            ["timeRescended"] = null,
                        }
                    )
        );
}

internal sealed record CorporationVoteNotification(string Event, int CorporationId, long CaseId) : PilotNotification(Event)
{
    public override object?[] Arguments => [CorporationId, CaseId];
}
