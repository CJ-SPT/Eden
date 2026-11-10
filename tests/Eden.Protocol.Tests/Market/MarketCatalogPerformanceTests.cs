using System.Diagnostics;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Industry;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;
using Eden.World.Catalog;
using Eden.World.Market;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Market;

public sealed class MarketCatalogPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void FullCatalogValuationFeedFitsTheTransportAndWarmReferenceReadsUseNoSql()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_MARKET_PERFORMANCE_CATALOG");
        if (source is null)
        {
            return;
        }
        using var scope = new CatalogReadScope().Begin();
        using CatalogDbContext catalog = new(source);
        var prices = MarketValuations.ReferencePrices(catalog);
        Assert.True(prices.Count > 10000);
        var reply = new BlueMap<int, EconomicPriceReply>(
            prices.ToDictionary(p => p.Key, p => new EconomicPriceReply(new(p.Key, p.Value, null)))
        );
        byte[] encoded = BlueMarshal.Encode(reply);
        // Reserve at least half the packet budget for its Macho envelope and future fields.
        Assert.InRange(encoded.Length, 1, EncryptedPackets.MaximumOutgoingBytes / 2);
        Assert.Equal(prices.Count, Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(encoded)).Count);
        output.WriteLine($"Valuation feed: {prices.Count} types, {encoded.Length} encoded bytes.");
        using var warm = SessionPerformance.Begin("warm valuation reference", output.WriteLine, force: true)!;
        Assert.Same(prices, MarketValuations.ReferencePrices(catalog));
        Assert.Equal(0, warm.Commands);
        Assert.Equal(0, warm.Connections);
        Assert.Equal(0, warm.FittingCalculations);
    }

    [Fact]
    public void FullCatalogProfilesBatchReadsAndWarmProfilesDoNotUseSql()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_MARKET_PERFORMANCE_CATALOG");
        if (source is null)
        {
            return;
        }
        using var scope = new CatalogReadScope().Begin();
        using CatalogDbContext catalog = new(source);
        var started = Stopwatch.GetTimestamp();
        RegionalMarketCatalog profile;
        using (var cold = SessionPerformance.Begin("cold station profiles", output.WriteLine, force: true)!)
        {
            profile = RegionalMarketCatalog.Read(catalog);
            Assert.NotEmpty(profile.Stations);
            Assert.False(profile.DemandLoaded);
            Assert.InRange(cold.Commands, 1, 300);
        }
        output.WriteLine($"Station profile elapsed: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
        using (var demand = SessionPerformance.Begin("cold demand profiles", output.WriteLine, force: true)!)
        {
            Assert.Contains(34, profile.Demand.Keys);
            Assert.Contains(603, profile.Demand.Keys);
            Assert.False(profile.Demand[603].RequiresUnlock);
            Assert.True(profile.Demand[22436].RequiresUnlock);
            Assert.InRange(demand.Commands, 0, 300);
        }
        using var warm = SessionPerformance.Begin("warm market profiles", output.WriteLine, force: true)!;
        Assert.Same(profile, RegionalMarketCatalog.Read(catalog));
        Assert.NotEmpty(profile.Demand);
        Assert.Equal(0, warm.Commands);
        Assert.Equal(0, warm.Connections);
        Assert.Equal(0, warm.FittingCalculations);
    }
}
