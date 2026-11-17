namespace Eden.Data.Catalog.Market;

public sealed record LiveMarketSnapshotManifest(
    int FormatVersion,
    string Generation,
    string Source,
    DateTimeOffset CapturedAt,
    string CatalogFingerprint,
    int ImportedOrders,
    IReadOnlyDictionary<string, int> ExcludedOrders,
    LiveMarketRegion[] Regions
);

public sealed record LiveMarketRegion(int RegionId, int Orders, string Sha256);

public sealed record LiveMarketOrder(
    long SourceOrderId,
    int StationId,
    int SystemId,
    int RegionId,
    int TypeId,
    bool Bid,
    long PriceCents,
    int Remaining,
    int Entered,
    long Issued,
    int DurationDays,
    int Range,
    int MinimumVolume
);
