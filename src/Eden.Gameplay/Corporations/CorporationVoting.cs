using System.Numerics;

namespace Eden.Gameplay.Corporations;

// Build 3662168 corporation.voting constants; war votes belong to the separate war feature.
public enum CorporationVoteType
{
    Ceo = 0,
    Shares = 2,
    Expel = 3,
    General = 4,
    LockBlueprint = 5,
    UnlockBlueprint = 6,
}

public enum CorporationVoteEvent
{
    Created,
    Cast,
    Closed,
    Sanctioned,
}

public sealed record CorporationVoteChange(long CaseId, CorporationVoteEvent Event);

public sealed record CorporationVoteRight(long OwnerId, long Weight);

public sealed record CorporationBallot(long OwnerId, long PilotId, int OptionId, long Weight, long Time);

public sealed record CorporationVoteDraft(
    string Title,
    string Description,
    CorporationVoteType Type,
    int Days,
    IReadOnlyList<string> Options,
    long Parameter = 0,
    long Parameter1 = 0,
    long Parameter2 = 0
);

public sealed record CorporationVoteData(
    long CreatorId,
    CorporationVoteDraft Draft,
    IReadOnlyList<CorporationVoteRight> Rights,
    IReadOnlyList<CorporationBallot> Ballots,
    bool Settled = false,
    long? AppliedAt = null,
    bool HoldReleased = false,
    long? PendingExpulsionActorId = null,
    CorporationCeoElection? Election = null,
    long? BlueprintLockVoteId = null
);

public sealed record CorporationCeoElection(long IncumbentId, long CandidateMembershipId, long? AuthorityAvailableAt = null);

public sealed record CorporationPendingExpulsion(long PilotId, long ActorId);

public sealed record CorporationVoteCase(long Id, int CorporationId, long StartedAt, long EndsAt, CorporationVoteData Data);

public sealed record CorporationVoteListing(IReadOnlyList<CorporationVoteCase> Open, IReadOnlyList<CorporationVoteCase> Closed);

public interface ICorporationVoting
{
    bool CanView(long actorId, int corporationId);
    bool CanRunForCeo(long actorId);
    CorporationVoteListing List(long actorId, int corporationId);
    CorporationVoteCase Read(long actorId, int corporationId, long caseId);
    long Create(long actorId, CorporationVoteDraft draft);
    void Cast(long actorId, int corporationId, long caseId, int optionId);
    IReadOnlyList<CorporationVoteCase> Actions(long actorId);
    void Activate(long actorId, long caseId);
    void Advance();
    IReadOnlyList<CorporationPendingExpulsion> PendingExpulsions(long actorId);
}

public static class CorporationVotingRules
{
    public const int MinimumCeoSharePercent = 5;
    private const int PercentScale = 100;

    // The pinned client gates on an injected Corporation Management skill. CEO Change documents at least five percent.
    public static bool HasCeoElectionShares(long shares, long issued) =>
        issued > 0 && shares > 0 && (BigInteger)shares * PercentScale >= (BigInteger)issued * MinimumCeoSharePercent;

    // Local, explicit interpretation of CCP's "few days" successor authority delay.
    public static readonly TimeSpan CeoAuthorityDelay = TimeSpan.FromDays(3);

    public static bool RequiresExpulsionVote(long shares, long issued, int shareholders) =>
        shares > 0 && (BigInteger)shares * shareholders > issued;

    public const int MaximumOpen = 1000;
    public const int ClosedPageSize = 20;
    public const int MaximumOptions = 50;
    public const int MaximumDays = 30;

    // Local sanction expiry policy; the native client consumes an absolute expires timestamp.
    public static readonly TimeSpan SanctionWindow = TimeSpan.FromDays(7);

    public static void Validate(CorporationVoteDraft draft)
    {
        if (
            !Enum.IsDefined(draft.Type)
            || draft.Days is < 1 or > MaximumDays
            || string.IsNullOrWhiteSpace(draft.Title)
            || draft.Title.Length > 100
            || draft.Title.Any(char.IsControl)
            || draft.Description.Length > 4000
            || draft.Description.Contains('\0')
            || draft.Options.Count is < 2 or > MaximumOptions
            || draft.Options.Any(text => string.IsNullOrWhiteSpace(text) || text.Length > 1000 || text.Contains('\0'))
            || draft.Type != CorporationVoteType.General && draft.Options.Count != 2
            || draft.Type == CorporationVoteType.Shares && draft.Parameter <= 0
        )
        {
            throw new InvalidDataException("Choose a valid vote, two to fifty options, and a duration of one to thirty days.");
        }
    }

    public static int? Winner(CorporationVoteData data)
    {
        BigInteger total = data.Ballots.Aggregate(BigInteger.Zero, (sum, ballot) => sum + ballot.Weight);
        foreach (var group in data.Ballots.GroupBy(ballot => ballot.OptionId))
        {
            if (group.Aggregate(BigInteger.Zero, (sum, ballot) => sum + ballot.Weight) * 2 > total)
            {
                return group.Key;
            }
        }
        return null;
    }

    public static long Expires(CorporationVoteCase vote) => checked(vote.EndsAt + SanctionWindow.Ticks);
}
