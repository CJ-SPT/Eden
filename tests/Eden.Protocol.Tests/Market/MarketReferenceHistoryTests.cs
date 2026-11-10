using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Protocol.Tests.Industry;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class MarketReferenceHistoryTests
{
    [Fact]
    public void EverySnapshotPricedTypeHasThirtySimulatedReferenceDays()
    {
        foreach (int type in MarketSnapshotCatalog.Quotes.Keys)
        {
            var days = MarketHistoryCatalog.Days(type);
            Assert.Equal(MarketReferenceHistoryRules.Days, days.Count);
            Assert.All(days, day => Assert.Equal(MarketHistorySource.SimulatedReference, day.Source));
        }
    }

    [Fact]
    public void ReferenceGenerationIsStableBoundedAndAnchoredToItsQuote()
    {
        MarketReferenceQuote quote = new(34, 397, 426);
        DateOnly last = new(2026, 11, 8);
        var days = MarketReferenceHistoryRules.Generate(quote, last);
        Assert.Equal(days, MarketReferenceHistoryRules.Generate(quote, last));
        Assert.Equal(411.5m, days[^1].AveragePriceCents);
        Assert.Equal(last.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToFileTimeUtc(), days[^1].Date);
        Assert.Equal(30, days.Select(d => d.Date).Distinct().Count());
        Assert.True(days.Select(d => d.AveragePriceCents).Distinct().Count() > 1);
        foreach (var day in days)
        {
            Assert.InRange(day.AveragePriceCents, 411.5m * 0.97m, 411.5m * 1.03m);
            Assert.True(day.LowPriceCents <= day.AveragePriceCents && day.AveragePriceCents <= day.HighPriceCents);
            Assert.InRange(day.Orders, 1, day.Volume);
            Assert.Equal(MarketHistorySource.SimulatedReference, day.Source);
        }
        Assert.Empty(MarketReferenceHistoryRules.Generate(new(34, null, null), last));
        Assert.Equal(500m, MarketReferenceHistoryRules.Generate(new(34, null, 500), last)[^1].AveragePriceCents);
        Assert.Throws<ArgumentOutOfRangeException>(() => MarketReferenceHistoryRules.Generate(new(0, 1, 2), last));
    }

    [Fact]
    public void NativeDefaultTickerItemsHaveEnoughNonzeroReferenceHistory()
    {
        // Build 3662168 ticker.pyj and inventorycommon.const. These are its
        // default/always-present item IDs; their reference activity is explicitly simulated.
        int[] types = [44992, 40520, 40519, 45635, 52568, 36, 28668, 34, 39, 38, 40, 16275, 28758, 35];
        foreach (int type in types)
        {
            var days = MarketHistoryCatalog.Days(type);
            Assert.True(days.Count >= 7, $"Type {type} lacks seven history entries.");
            Assert.Contains(days, d => d.Volume > 0);
            Assert.All(days, d => Assert.True(d.AveragePriceCents > 0));
        }
    }

    [Fact]
    public void FreshWorldHasReferenceTickerHistoryWithoutCreatingExecutionsOrdersOrMoney()
    {
        using IndustryTests.Fixture f = new();
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        var days = market.History(f.Pilot.PilotId, [34]);
        Assert.True(days.Count >= 7);
        Assert.Contains(days, d => d.Volume > 0);
        Assert.All(days, d => Assert.True(d.AveragePriceCents > 0));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<MarketExecution>());
        Assert.Empty(saved.Set<NpcMarketOrder>());
        Assert.Empty(saved.Set<PilotWallet>());
        Assert.All(days, day => Assert.Equal(MarketHistorySource.SimulatedReference, day.Source));
        Assert.Equal(29, (days[^1].Date - days[0].Date) / TimeSpan.TicksPerDay);
        Assert.DoesNotContain(market.History(f.Pilot.PilotId, [int.MaxValue]), _ => true);
    }

    [Fact]
    public void LocalDayReplacesReferenceVolumeAndFutureRowsStayHiddenAcrossReopen()
    {
        using IndustryTests.Fixture f = new();
        var reference = MarketHistoryCatalog.Days(34).First();
        using (WorldDbContext world = new(f.Db.Path))
        {
            var order = world
                .Add(
                    new NpcMarketOrder
                    {
                        StationId = f.Pilot.StationId,
                        SystemId = f.Pilot.SystemId,
                        RegionId = f.Pilot.RegionId,
                        OwnerId = f.Pilot.StationOwnerId,
                        TypeId = 34,
                        Bid = true,
                        PriceCents = 999,
                        Entered = 1,
                        Remaining = 1,
                    }
                )
                .Entity;
            world.SaveChanges();
            world.Add(
                new MarketExecution
                {
                    OrderId = order.Id,
                    PilotId = f.Pilot.PilotId,
                    StationId = f.Pilot.StationId,
                    CounterpartyId = f.Pilot.StationOwnerId,
                    TypeId = 34,
                    Quantity = 1,
                    PriceCents = 999,
                    Time = reference.Date + TimeSpan.FromHours(1).Ticks,
                }
            );
            world.SaveChanges();
        }
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        var merged = market.History(f.Pilot.PilotId, [34]);
        var local = Assert.Single(merged, d => d.Date == reference.Date);
        Assert.Equal(1, local.Volume);
        Assert.Equal(999, local.AveragePriceCents);
        Assert.Equal(1, local.Orders);
        Assert.Equal(MarketHistorySource.LocalExecution, local.Source);
        Assert.Equal(merged.Count, merged.Select(d => (d.TypeId, d.Date)).Distinct().Count());
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(merged, market.History(f.Pilot.PilotId, [34]));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<MarketExecution>());
        long earlier = reference.Date - 1;
        Assert.Empty(MarketHistoryCatalog.Merge([], [34], 0, earlier));
    }
}
