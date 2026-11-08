using Eden.Gameplay.Market;

namespace Eden.Data.Catalog.Market;

public sealed record LoyaltyOfferSnapshot(
    string Source,
    string RetrievedAt,
    LoyaltyOfferDefinition[] Offers,
    Dictionary<int, int[]> Stores
);

public sealed record LoyaltyOfferDefinition(
    int Id,
    int TypeId,
    int Quantity,
    long LpCost,
    long IskCents,
    int AnalysisKredits,
    LoyaltyRequiredItem[] RequiredItems
);
