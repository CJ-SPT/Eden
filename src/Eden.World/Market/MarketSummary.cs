using Eden.Gameplay.Market;

namespace Eden.World.Market;

internal sealed record MarketSummary(
    long PilotId,
    long ShipId,
    int StationId,
    int SystemId,
    int RegionId,
    IReadOnlyList<MarketOrder> Orders,
    long ExpiresAt,
    long ActiveUntil
);
