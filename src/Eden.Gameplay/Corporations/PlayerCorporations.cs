using Eden.Gameplay.Market;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationLogo(int? Shape1, int? Shape2, int? Shape3, int? Color1, int? Color2, int? Color3, int? Typeface);

public sealed record CorporationDetails(
    string Name,
    string Ticker,
    string Description,
    string Url,
    double TaxRate,
    double LoyaltyPointTaxRate,
    CorporationLogo Logo,
    bool ApplicationsEnabled,
    bool FriendlyFireEnabled,
    CorporationDivisions? Divisions = null
);

public sealed record PlayerCorporation(
    int Id,
    CorporationDetails Details,
    long CeoId,
    long CreatorId,
    int StationId,
    int SystemId,
    long CreatedAt,
    int MemberLimit,
    int MemberCount
);

public sealed record CorporationMember(
    long PilotId,
    string Name,
    int CorporationId,
    long JoinedAt,
    long Roles,
    CorporationMemberAccess? Access = null
);

public sealed record CorporationEmployment(int CorporationId, long StartedAt);

public sealed record CorporationCreation(PlayerCorporation Corporation, long BalanceCents);

public sealed record CorporationPayment(long Id, long PilotId, int CorporationId, long Time, long BalanceCents);

public sealed record CorporationIdentity(int Id, string Ticker, long Roles, long JoinedAt, CorporationMemberAccess? Access = null);

public interface IPlayerCorporations
{
    PlayerCorporation? Read(int corporationId);
    CorporationCreation Create(long pilotId, CorporationDetails details);
    IReadOnlyList<CorporationMember> Members(long viewerId);
    CorporationIdentity Identity(long pilotId);
    IReadOnlyList<CorporationEmployment> Employment(long pilotId);
    IReadOnlyList<string> SuggestTickers(string name);
}

public static class CorporationRules
{
    // Build 3662168 appConst.corporationStartupCost, corpRoleDirector, and catalog type 3363.
    public const long CreationFeeCents = 1_599_800 * MarketDefinitions.CentsPerIsk;
    public const int ManagementSkill = 3363;
    public const long DirectorRole = 1;
    public const long PersonnelManagerRole = 128;
    public const long CeoRoles = DirectorRole | PersonnelManagerRole;
    public const int FirstCorporationId = 98000000;
    public const int CorporationType = 2;
    public const int MembersPerManagementLevel = 20;
    public const long InitialShares = 1000;
    public const long FirstJournalId = 16_000_000_000_000;
    public const int MaximumNameLength = 50;
    public const int MaximumTickerLength = 5;
    public const int MaximumDescriptionLength = 4000;
    public const int MaximumUrlLength = 1000;

    public static CorporationDetails Validate(CorporationDetails details)
    {
        string name = details.Name.Trim();
        string ticker = details.Ticker.Trim().ToUpperInvariant();
        if (name.Length is < 3 or > MaximumNameLength || name.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose a corporation name between 3 and 50 characters.");
        }
        if (ticker.Length is < 1 or > MaximumTickerLength || ticker.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new InvalidDataException("Choose a ticker of one to five letters or digits.");
        }
        if (details.Description.Length > MaximumDescriptionLength || details.Description.Contains('\0'))
        {
            throw new InvalidDataException("The corporation description is too long or contains nulls.");
        }
        if (details.Url.Length > MaximumUrlLength || details.Url.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose a valid corporation website.");
        }
        if (
            !double.IsFinite(details.TaxRate)
            || details.TaxRate is < 0 or > 1
            || !double.IsFinite(details.LoyaltyPointTaxRate)
            || details.LoyaltyPointTaxRate is < 0 or > 1
        )
        {
            throw new InvalidDataException("Corporation tax rates must be between zero and 100 percent.");
        }
        if (
            new int?[]
            {
                details.Logo.Shape1,
                details.Logo.Shape2,
                details.Logo.Shape3,
                details.Logo.Color1,
                details.Logo.Color2,
                details.Logo.Color3,
                details.Logo.Typeface,
            }.Any(value => value < 0)
        )
        {
            throw new InvalidDataException("Corporation logo identifiers cannot be negative.");
        }
        details.Divisions?.Validate();
        return details with { Name = name, Ticker = ticker };
    }

    public static long Pay(long balance, bool docked, bool alreadyCeo, int managementLevel)
    {
        if (!docked || alreadyCeo || managementLevel < 1)
        {
            throw new InvalidDataException("Create a corporation while docked, with Corporation Management I, and without being a CEO.");
        }
        if (balance < CreationFeeCents)
        {
            throw new InvalidDataException("Creating a corporation costs 1,599,800 ISK.");
        }
        return balance - CreationFeeCents;
    }
}
