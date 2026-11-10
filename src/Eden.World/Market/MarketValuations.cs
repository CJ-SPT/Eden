using Eden.Gameplay.Economy;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Market;

[Injectable]
internal sealed class MarketValuations(WorldDatabase database, IEconomicPrices adjusted) : IMarketValuations
{
    private static readonly CatalogDefinition<IReadOnlyDictionary<int, decimal>> s_prices = new("market-valuations", Load);

    public IReadOnlyList<MarketValuation> Prices()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var averages = ReferencePrices(catalog);
        var industrial = adjusted.Prices().ToDictionary(p => p.TypeId, p => p.PriceIsk);
        return averages
            .Keys.Union(industrial.Keys)
            .Order()
            .Select(id => new MarketValuation(
                id,
                averages.TryGetValue(id, out var average) ? average : null,
                industrial.TryGetValue(id, out var price) ? price : null
            ))
            .ToArray();
    }

    internal static IReadOnlyDictionary<int, decimal> ReferencePrices(CatalogDbContext catalog) =>
        catalog.Definitions.GlobalDefinition(s_prices);

    private static IReadOnlyDictionary<int, decimal> Load(CatalogDbContext catalog, int unused)
    {
        int[] ids = MarketSnapshotCatalog.Quotes.Keys.Where(catalog.Definitions.Items.ContainsKey).ToArray();
        var definitions = catalog.ReadMany(CatalogData.Client.Types, ids.Select(id => (long)id));
        Dictionary<int, decimal> prices = [];
        foreach (int id in ids)
        {
            if (definitions.GetValueOrDefault(id) is not { Published: 1, MarketGroupID: > 0 })
            {
                continue;
            }
            var quote = MarketSnapshotCatalog.Quotes[id];
            if (MarketValuationRules.EstimateIsk(quote.BuyCents, quote.SellCents) is { } value)
            {
                prices.Add(id, value);
            }
        }
        return prices;
    }
}
