using Eden.Data.World.Market;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationMarketInformation(WorldDatabase database) : ICorporationMarketInformation
{
    public IReadOnlyList<CorporationMarketActivity> Read(int corporationId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<CorporationMarketActivity>>(
            database,
            world =>
            {
                // Only completed NPC trades are public here. Personal orders and wallet
                // entries never contribute; reading Show Info must not seed or refresh markets.
                var executions =
                    from trade in world.Set<MarketExecution>()
                    join order in world.Set<NpcMarketOrder>() on trade.OrderId equals order.Id
                    where order.OwnerId == corporationId && trade.CounterpartyId == corporationId
                    select trade;
                var latestIds = executions
                    .GroupBy(trade => new { trade.TypeId, trade.IsBuy })
                    .Select(group => group.Max(trade => trade.Id));
                var trades = world.Set<MarketExecution>().Where(trade => Enumerable.Contains(latestIds, trade.Id)).ToArray();
                return trades
                    .GroupBy(trade => trade.TypeId)
                    .OrderBy(group => group.Key)
                    .Select(group => new CorporationMarketActivity(
                        group.Key,
                        Snapshot(group.SingleOrDefault(trade => trade.IsBuy)),
                        Snapshot(group.SingleOrDefault(trade => !trade.IsBuy))
                    ))
                    .ToArray();
            }
        );
    }

    private static CorporationMarketTrade? Snapshot(MarketExecution? trade) =>
        trade is null ? null : new(trade.PriceCents, trade.Quantity, trade.Time, trade.StationId);
}
