namespace Eden.Gameplay.Contracts;

public enum ContractSort
{
    Id,
    Price,
    Expires,
    Station,
    System,
    Region,
    Constellation,
    Type,
    Reward,
    Collateral,
    Volume,
    Assignee,
}

public enum ContractSecurityClass
{
    Null,
    Low,
    High,
    Safe,
}

public sealed record ContractSearch(
    IReadOnlyList<int>? ItemTypes = null,
    string? ItemTypeName = null,
    int? CategoryId = null,
    int? GroupId = null,
    int Type = (int)ContractType.AuctionAndItemExchange,
    IReadOnlyList<int>? SecurityClasses = null,
    long? LocationId = null,
    long? EndLocationId = null,
    long? IssuerId = null,
    long? MinPrice = null,
    long? MaxPrice = null,
    long? MinReward = null,
    long? MaxReward = null,
    long? MinCollateral = null,
    long? MaxCollateral = null,
    double? MinVolume = null,
    double? MaxVolume = null,
    bool ExcludeTrade = false,
    bool ExcludeMultiple = false,
    bool ExcludeNoBuyout = false,
    int Availability = 0,
    string? Description = null,
    int Hint = 0,
    ContractSort Sort = ContractSort.Id,
    bool Descending = true,
    int Offset = 0
);

public sealed record ContractSearchResult(IReadOnlyList<ContractDetails> Contracts, int Found, double Seconds);

public static class ContractSearchRules
{
    public const int PageSize = 100;
    public const int MaximumResults = 1000;

    // Pinned eveuniverse.security.SecurityClassFromLevel, including safe (1.0) space.
    public static ContractSecurityClass SecurityClass(double value) =>
        value switch
        {
            <= 0 => ContractSecurityClass.Null,
            < 0.45 => ContractSecurityClass.Low,
            < 0.95 => ContractSecurityClass.High,
            _ => ContractSecurityClass.Safe,
        };

    public static void Check(ContractSearch search)
    {
        if (
            search.Offset is < 0 or >= MaximumResults
            || search.Availability is < 0 or > 3
            || search.Hint is < 0 or > 2
            || !Enum.IsDefined(search.Sort)
            || !Enum.IsDefined((ContractType)search.Type)
            || search.SecurityClasses?.Any(s => s is < 0 or > 3) == true
            || search.ItemTypes?.Count > MaximumResults
            || search.ItemTypes?.Any(t => t <= 0) == true
            || search.ItemTypeName?.Length > ContractRules.MaximumDescription
            || search.Description?.Length > ContractRules.MaximumDescription
            || search.LocationId is <= 0
            || search.EndLocationId is <= 0
            || search.IssuerId is <= 0
            || search.CategoryId is <= 0
            || search.GroupId is <= 0
        )
        {
            throw new InvalidDataException("Invalid contract search filters.");
        }
        CheckRange(search.MinPrice, search.MaxPrice);
        CheckRange(search.MinReward, search.MaxReward);
        CheckRange(search.MinCollateral, search.MaxCollateral);
        if (
            search.MinVolume is { } min && (!double.IsFinite(min) || min < 0)
            || search.MaxVolume is { } max && (!double.IsFinite(max) || max < 0)
            || search.MinVolume > search.MaxVolume
        )
        {
            throw new InvalidDataException("Invalid contract volume range.");
        }
    }

    private static void CheckRange(long? min, long? max)
    {
        if (min is < 0 or > ContractRules.MaximumAmountCents || max is < 0 or > ContractRules.MaximumAmountCents || min > max)
        {
            throw new InvalidDataException("Invalid contract price range.");
        }
    }

    public static decimal SortKey(ContractDetails details, ContractSort sort) =>
        sort switch
        {
            ContractSort.Id => details.Contract.Id,
            ContractSort.Price => details.Contract.PriceCents,
            ContractSort.Expires => details.Contract.Expires,
            ContractSort.Station => details.Contract.StationId,
            ContractSort.System => details.Contract.SystemId,
            ContractSort.Region => details.Contract.RegionId,
            ContractSort.Constellation => details.Contract.ConstellationId,
            ContractSort.Type => (int)details.Contract.Type,
            ContractSort.Reward => details.Contract.RewardCents,
            ContractSort.Collateral => details.Contract.Courier?.CollateralCents ?? 0,
            ContractSort.Volume => (decimal)details.Contract.Volume,
            ContractSort.Assignee => details.Contract.AssigneeId ?? 0,
            _ => throw new InvalidDataException("Unknown contract sort order."),
        };
}
