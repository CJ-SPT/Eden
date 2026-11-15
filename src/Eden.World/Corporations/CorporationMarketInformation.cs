using Eden.Data.World.Market;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationMarketInformation(WorldDatabase database) : ICorporationMarketInformation
{
    public IReadOnlyList<CorporationMarketActivity> Read(int corporationId)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("The world is not running.");
        return live.Economy.Inspect(
            world =>
            {
                // Only completed NPC trades are public here. Personal orders and wallet
                // entries never contribute; reading Show Info must not seed or refresh markets.
                // Materialize the indexed owner lookup before filtering by the indexed OrderId.
                // A deferred aggregation inside Contains runs again for every execution.
                var orderIds = world
                    .Set<NpcMarketOrder>()
                    .Where(order => order.OwnerId == corporationId)
                    .Select(order => order.Id)
                    .ToHashSet();
                return world
                    .Set<MarketExecution>()
                    .Where(trade => orderIds.Contains(trade.OrderId) && trade.CounterpartyId == corporationId)
                    .AsEnumerable()
                    .GroupBy(trade => trade.TypeId)
                    .OrderBy(group => group.Key)
                    .Select(group => new CorporationMarketActivity(
                        group.Key,
                        Snapshot(group.Where(trade => trade.IsBuy).MaxBy(trade => trade.Id)),
                        Snapshot(group.Where(trade => !trade.IsBuy).MaxBy(trade => trade.Id))
                    ))
                    .ToArray();
            },
            TransactionParticipants.None
        );
    }

    private static CorporationMarketTrade? Snapshot(MarketExecution? trade) =>
        trade is null ? null : new(trade.PriceCents, trade.Quantity, trade.Time, trade.StationId);
}
