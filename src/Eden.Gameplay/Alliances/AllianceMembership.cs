namespace Eden.Gameplay.Alliances;

// Build 3662168 appConst.allianceApplication* values.
public enum AllianceApplicationStatus
{
    New = 1,
    Accepted = 2,
    Effective = 3,
    Rejected = 4,
}

public sealed record AllianceApplication(
    long Id,
    int CorporationId,
    int AllianceId,
    string Text,
    AllianceApplicationStatus Status,
    long AppliedAt,
    long? EffectiveAt
);

public sealed record AllianceHistory(int? AllianceId, long StartedAt);

public sealed record AllianceChange(
    long Id,
    int AllianceId,
    int CorporationId,
    Alliance? Before = null,
    Alliance? After = null,
    AllianceMember? MemberBefore = null,
    AllianceMember? MemberAfter = null,
    AllianceApplication? ApplicationBefore = null,
    AllianceApplication? ApplicationAfter = null,
    Eden.Gameplay.Calendar.CalendarChange? Calendar = null
);

public interface IAllianceMembership
{
    AllianceApplication Apply(long actorId, int allianceId, string text);
    void Withdraw(long actorId, int allianceId);
    void Decide(long actorId, int corporationId, string text, AllianceApplicationStatus status);
    IReadOnlyList<AllianceApplication> Applications(long actorId, bool forAlliance);
    void DeclareSupport(long actorId, int corporationId);
    void Remove(long actorId, int corporationId);
    IReadOnlyList<AllianceHistory> History(int corporationId);
    long Head { get; }
    IReadOnlyList<AllianceChange> Changes(long actorId, long after);
    void Advance();
}

public static class AllianceMembershipRules
{
    public static readonly TimeSpan AdmissionDelay = TimeSpan.FromHours(24);
    public const int MaximumApplicationLength = 1000;

    public static void ValidateApplication(string text)
    {
        if (text.Length > MaximumApplicationLength || text.Contains('\0'))
        {
            throw new InvalidDataException("Alliance applications allow up to 1000 characters and no nulls.");
        }
    }

    // UniWiki requires at least half of member corporations. With two equally
    // supported candidates retain the incumbent; without an incumbent suspend administration.
    public static int? Executor(IReadOnlyList<AllianceMember> members, int? incumbent)
    {
        if (members.Count == 0)
        {
            return null;
        }
        var eligible = members.Select(m => m.CorporationId).ToHashSet();
        var votes = members
            .Where(m => m.ChosenExecutorId.HasValue && eligible.Contains(m.ChosenExecutorId.Value))
            .GroupBy(m => m.ChosenExecutorId!.Value)
            .Select(group => (Corporation: group.Key, Votes: group.Count()))
            .Where(group => group.Votes * 2 >= members.Count)
            .OrderByDescending(group => group.Votes)
            .ThenBy(group => group.Corporation)
            .ToArray();
        if (votes.Length == 0)
        {
            return null;
        }
        var leaders = votes.Where(v => v.Votes == votes[0].Votes).ToArray();
        return leaders.Length == 1 ? leaders[0].Corporation
            : leaders.Any(v => v.Corporation == incumbent) ? incumbent
            : null;
    }
}
