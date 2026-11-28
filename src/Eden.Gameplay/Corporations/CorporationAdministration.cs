namespace Eden.Gameplay.Corporations;

public sealed record CorporationDivisions(
    string? Division1,
    string? Division2,
    string? Division3,
    string? Division4,
    string? Division5,
    string? Division6,
    string? Division7,
    string? WalletDivision1,
    string? WalletDivision2,
    string? WalletDivision3,
    string? WalletDivision4,
    string? WalletDivision5,
    string? WalletDivision6,
    string? WalletDivision7
)
{
    public void Validate()
    {
        string?[] names =
        [
            Division1,
            Division2,
            Division3,
            Division4,
            Division5,
            Division6,
            Division7,
            WalletDivision1,
            WalletDivision2,
            WalletDivision3,
            WalletDivision4,
            WalletDivision5,
            WalletDivision6,
            WalletDivision7,
        ];
        if (names.Any(name => name is not null && (name.Length > MaximumNameLength || name.Any(char.IsControl))))
        {
            throw new InvalidDataException("Division names allow at most 50 characters and no control characters.");
        }
    }

    public const int MaximumNameLength = 50;
}

public sealed record CorporationProperties(string Description, string Url, double TaxRate, bool IsRecruiting, double LoyaltyPointTaxRate);

public sealed record CorporationAdministrationChange(
    int CorporationId,
    CorporationDetails Before,
    CorporationDetails After,
    long? PreviousCeoId = null,
    long? CeoId = null,
    long? ClosedAt = null,
    CorporationVoteChange? Vote = null,
    CorporationHeadquartersChange? Headquarters = null,
    CorporationBlueprintLockChange? BlueprintLock = null,
    int? PreviousMemberLimit = null,
    int? MemberLimit = null
);

public sealed record CorporationHeadquartersChange(int PreviousStationId, int PreviousSystemId, int StationId, int SystemId);

public interface ICorporationHeadquarters
{
    void MoveHere(long actorId);
}

public interface ICorporationAdministration
{
    void Update(long actorId, CorporationProperties properties);
    int UpdateAbilities(long actorId);
    void UpdateLogo(long actorId, CorporationLogo logo);
    void UpdateDivisions(long actorId, CorporationDivisions divisions);
    bool RestrictsCorpMails(long actorId);
    void SetCorpMailRestriction(long actorId, bool restricted);
}
