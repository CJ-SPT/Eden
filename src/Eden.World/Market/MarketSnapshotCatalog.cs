using System.Text.Json;
using Eden.Data.Catalog.Market;
using Eden.Gameplay.Market;

namespace Eden.World.Market;

internal static class MarketSnapshotCatalog
{
    internal static MarketPriceSnapshot Snapshot { get; } = Load();
    internal static IReadOnlyDictionary<int, MarketPriceQuote> Quotes { get; } = Snapshot.Quotes.ToDictionary(q => q.TypeId);

    private static MarketPriceSnapshot Load()
    {
        using var stream =
            typeof(MarketSnapshotCatalog).Assembly.GetManifestResourceStream("Eden.World.MarketSnapshot.json")
            ?? throw new InvalidDataException("Missing market price snapshot.");
        var snapshot =
            JsonSerializer.Deserialize<MarketPriceSnapshot>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid market price snapshot.");
        if (
            snapshot.Quotes.Length == 0
            || snapshot.Quotes.Any(q =>
                q.TypeId <= 0
                || q.BuyCents is <= 0
                || q.SellCents is <= 0
                || q.BuyCents > MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk
                || q.SellCents > MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk
                || q.BuyCents is null && q.SellCents is null
                || q.BuyCents >= q.SellCents
            )
        )
        {
            throw new InvalidDataException("Invalid market snapshot quote.");
        }
        return snapshot;
    }
}
