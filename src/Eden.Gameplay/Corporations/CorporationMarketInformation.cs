namespace Eden.Gameplay.Corporations;

public sealed record CorporationMarketTrade(long PriceCents, int Quantity, long Time, int StationId);

public sealed record CorporationMarketActivity(int TypeId, CorporationMarketTrade? Sell, CorporationMarketTrade? Buy);

public interface ICorporationMarketInformation
{
    IReadOnlyList<CorporationMarketActivity> Read(int corporationId);
}
