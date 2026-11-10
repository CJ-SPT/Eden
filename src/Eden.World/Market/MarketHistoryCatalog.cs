using System.Collections.Concurrent;
using Eden.Gameplay.Market;

namespace Eden.World.Market;

internal static class MarketHistoryCatalog
{
    // Anchor the synthetic baseline to the existing price snapshot. Restarting
    // cannot reroll the series or pretend new historical trades occurred.
    internal static DateOnly LastReferenceDate { get; } =
        DateOnly.FromDateTime(MarketSnapshotCatalog.Snapshot.RetrievedAt.UtcDateTime).AddDays(-1);
    private static readonly ConcurrentDictionary<int, IReadOnlyList<MarketHistoryDay>> s_days = new();

    internal static IReadOnlyList<MarketHistoryDay> Days(int typeId) =>
        s_days.GetOrAdd(
            typeId,
            type =>
                MarketSnapshotCatalog.Quotes.TryGetValue(type, out var quote)
                    ? MarketReferenceHistoryRules.Generate(new(type, quote.BuyCents, quote.SellCents), LastReferenceDate)
                    : []
        );

    internal static IReadOnlyList<MarketHistoryDay> Merge(
        IReadOnlyList<MarketHistoryDay> local,
        IReadOnlyList<int> typeIds,
        long from,
        long now
    )
    {
        var localDays = local.Select(d => (d.TypeId, d.Date)).ToHashSet();
        return typeIds
            .Distinct()
            .SelectMany(Days)
            .Where(d => d.Date >= from && d.Date <= now && !localDays.Contains((d.TypeId, d.Date)))
            .Concat(local)
            .OrderBy(d => d.TypeId)
            .ThenBy(d => d.Date)
            .ToArray();
    }
}
