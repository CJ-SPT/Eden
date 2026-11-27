using Eden.Gameplay.Market;

namespace Eden.Gameplay.Alliances;

public sealed record AllianceDetails(string Name, string Ticker, string Description, string Url);

public sealed record Alliance(
    int Id,
    AllianceDetails Details,
    long CreatorId,
    int CreatorCorporationId,
    int? ExecutorCorporationId,
    long CreatedAt,
    long? ClosedAt,
    int CorporationCount,
    int MemberCount
);

public sealed record AllianceMember(int CorporationId, int AllianceId, long JoinedAt, int? ChosenExecutorId)
{
    // Build 3662168 displays None as Secret in the member list.
    public AllianceMember ForViewer(int? corporationId) => CorporationId == corporationId ? this : this with { ChosenExecutorId = null };
}

public sealed record AllianceCreation(Alliance Alliance, long BalanceCents);

public interface IAlliances
{
    Alliance? Read(int allianceId);
    int? Affiliation(int corporationId);
    int? PilotAffiliation(long pilotId);
    IReadOnlyList<Alliance> List();
    IReadOnlyList<AllianceMember> Members(int allianceId);
    IReadOnlyList<AllianceMember> MembersForPilot(long pilotId);
    int DaysInAlliance(int allianceId, int corporationId);
    IReadOnlyList<int> MembersOlderThan(int allianceId, int minimumDays);
    AllianceCreation Create(long actorId, AllianceDetails details);
    void Update(long actorId, string description, string url);
    IReadOnlyList<string> SuggestTickers(string name);
}

public static class AllianceRules
{
    // UniWiki Alliances; skill ID is pinned catalog Empire Control, not the Sovereignty skill.
    public const int EmpireControlSkill = 3732;
    public const int RequiredEmpireControlLevel = 5;
    public const long CreationFeeCents = 1_000_000_000L * MarketDefinitions.CentsPerIsk;
    public const long MaintenancePerCorporationCents = 2_000_000L * MarketDefinitions.CentsPerIsk;
    public const int FirstAllianceId = 99000000;
    public const int AllianceTypeId = 16159;
    public const int MinimumNameLength = 4;
    public const int MaximumNameLength = 50;
    public const int MaximumTickerLength = 5;
    public const int MaximumDescriptionLength = 5000;
    public const int MaximumUrlLength = 2048;

    public static long FormationBalance(bool isCeo, bool affiliated, int empireControlLevel, long balanceCents)
    {
        if (!isCeo || affiliated || empireControlLevel < RequiredEmpireControlLevel)
        {
            throw new InvalidDataException("An unaffiliated player corporation's CEO with Empire Control V must form the alliance.");
        }
        if (balanceCents < CreationFeeCents)
        {
            throw new InvalidDataException("Alliance formation requires one billion ISK in your personal wallet.");
        }
        return balanceCents - CreationFeeCents;
    }

    public static AllianceDetails Validate(AllianceDetails details)
    {
        string name = details.Name.Trim().Normalize();
        string ticker = details.Ticker.Trim().Normalize().ToUpperInvariant();
        if (name.Length is < MinimumNameLength or > MaximumNameLength || name.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose an alliance name between 4 and 50 characters.");
        }
        if (ticker.Length is < 1 or > MaximumTickerLength || ticker.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '-'))
        {
            throw new InvalidDataException("Choose an alliance ticker of one to five letters, digits, dots or hyphens.");
        }
        if (details.Description.Length > MaximumDescriptionLength || details.Description.Contains('\0'))
        {
            throw new InvalidDataException("The alliance description is too long or contains nulls.");
        }
        if (details.Url.Length > MaximumUrlLength || details.Url.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose a valid alliance website.");
        }
        return details with { Name = name, Ticker = ticker };
    }
}
