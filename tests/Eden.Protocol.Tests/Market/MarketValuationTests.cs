using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Industry;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class MarketValuationTests
{
    [Fact]
    public void EncodedPublicPricesReuseBytesAndRefreshAdjustmentsWithinTheSameTick()
    {
        using IndustryTests.Fixture f = new();
        var prices = f.Db.Services.GetRequiredService<IMarketValuations>();
        var cache = f.Db.Services.GetRequiredService<EconomicPriceCache>();
        cache.Warm();
        var first = cache.ReadReply();
        byte[] original = first.EncodeReply();
        Assert.Equal(
            BlueMarshal.Encode(
                new BlueMap<int, EconomicPriceReply>(prices.Prices().ToDictionary(p => p.TypeId, p => new EconomicPriceReply(p)))
            ),
            original
        );
        using (var metrics = SessionPerformance.Begin("encoded public prices", _ => { }, force: true)!)
        {
            var repeated = cache.ReadReply();
            Assert.Same(first, repeated);
            Assert.Same(original, repeated.EncodeReply());
            // The policy remains a current read; only unchanged wire bytes are shared.
            Assert.Equal(1, metrics.Commands);
            Assert.Equal(1, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new SavedAdjustedPrice { TypeId = 34, PriceIsk = 123.45m });
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
            world.SaveChanges();
        }
        var changed = cache.ReadReply();
        Assert.NotSame(first, changed);
        Assert.Equal(123.45, Adjusted(changed.EncodeReply()));
        Assert.Null(Adjusted(original));
        var request = IncomingMachoPacket.Read(
            Eden.Protocol.Tests.PilotSessions.PilotServicesTests.Request("config", "GetAverageMarketPrices", [])
        );
        var packet = IncomingMachoPacket.Read(BlueMarshal.Decode(BlueMarshal.Encode(request.CallSucceeded(changed))));
        Assert.Equal(changed.EncodeReply(), BlueRead.Bytes(BlueRead.Tuple(packet.Fields[4], 1)[0]));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = false;
            world.SaveChanges();
        }
        Assert.Null(Adjusted(cache.ReadReply().EncodeReply()));
        Assert.Equal(123.45, Adjusted(changed.EncodeReply()));

        static object? Adjusted(byte[] bytes)
        {
            var feed = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(bytes));
            return BlueRead.Fields(Assert.IsType<BlueInstance>(feed[34]).State)["adjustedPrice"];
        }
    }

    [Fact]
    public void InventoryPricesExistWithoutServiceFeesOrProductionUnlocksAndPreserveAdjustedValues()
    {
        using IndustryTests.Fixture f = new();
        f.AddModules();
        var reader = f.Db.Services.GetRequiredService<IMarketValuations>();
        var initial = reader.Prices().ToDictionary(p => p.TypeId);
        foreach (int id in new[] { 34, 35, 215, 483, 1130 })
        {
            Assert.True(initial[id].AveragePriceIsk > 0);
            Assert.Null(initial[id].AdjustedPriceIsk);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedMarketUnlock>());
            Assert.False(world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts);
            world.Add(new SavedAdjustedPrice { TypeId = 34, PriceIsk = 123.45m });
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
            world.SaveChanges();
        }
        var enabled = reader.Prices().Single(p => p.TypeId == 34);
        Assert.Equal(123.45m, enabled.AdjustedPriceIsk);
        Assert.Equal(initial[34].AveragePriceIsk, enabled.AveragePriceIsk);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = false;
            world.SaveChanges();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(initial[34], reader.Prices().Single(p => p.TypeId == 34));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(123.45m, saved.Set<SavedAdjustedPrice>().Find(34)!.PriceIsk);
        Assert.Empty(saved.Set<MarketExecution>());
        Assert.Empty(saved.Set<NpcMarketOrder>());
    }

    [Fact]
    public void NativeAverageFeedSerializesIndependentOptionalAdjustedPricesBeforePilotSelection()
    {
        using IndustryTests.Fixture f = new();
        using PilotCallContext context = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            pilot => new(f.Db.Store, pilot, f.Clock, new(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var feed = Assert.IsType<Dictionary<long, object?>>(
            router.CallWire(context, "config", "GetAverageMarketPrices", Array.Empty<object?>(), _ => { })
        );
        var row = Assert.IsType<BlueInstance>(feed[34]);
        var values = Assert.IsType<Dictionary<string, object?>>(row.State);
        Assert.True(Assert.IsType<double>(values["averagePrice"]) > 0);
        Assert.Null(values["adjustedPrice"]);
        var handler = f.Db.Services.GetRequiredService<EconomicPriceHandler>();
        Assert.Same(PilotCallResult.Unsupported, handler.Call(context, "config", "GetAverageMarketPrices", new object?[] { 34L }, null));
        Assert.Same(
            PilotCallResult.Unsupported,
            handler.Call(context, "config", "GetAverageMarketPrices", Array.Empty<object?>(), new() { ["unexpected"] = true })
        );
        Assert.NotEmpty(BlueMarshal.Encode(new EconomicPriceReply(new(34, 4.5m, 2m))));
    }

    [Fact]
    public void ReferenceValuesUseObservedQuoteSidesIndependentlyOfSimulatedActivity()
    {
        Assert.Equal(1.5m, MarketValuationRules.EstimateIsk(100, 200));
        Assert.Equal(1m, MarketValuationRules.EstimateIsk(100, null));
        Assert.Equal(2m, MarketValuationRules.EstimateIsk(null, 200));
        Assert.Null(MarketValuationRules.EstimateIsk(null, null));
        Assert.Equal(0.015m, MarketValuationRules.EstimateIsk(1, 2));
    }
}
