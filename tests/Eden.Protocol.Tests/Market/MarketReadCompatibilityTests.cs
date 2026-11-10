using Eden.Data.Catalog;
using Eden.Data.World.Market;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class MarketReadCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyPlexHistoryAndMultibuyStructureDirectoryUseNativeCollections(bool inSpace)
    {
        using TestWorld world = new();
        var pilot = world.Store.FindPilot(1000001)!;
        if (inSpace)
        {
            world.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
            pilot = world.Store.FindPilot(1000001)!;
        }
        using var scope = world.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), world.Objects),
            1,
            SignedSettings.Empty
        );
        var calls = new[]
        {
            ("marketProxy", "GetPlexOldPriceHistory"),
            ("marketProxy", "GetPlexNewPriceHistory"),
            ("structureDirectory", "GetMyCharacterStructures"),
        };
        foreach (var (service, method) in calls)
        {
            Assert.Same(PilotCallResult.Unsupported, router.CallWire(context, service, method, Array.Empty<object?>(), _ => { }));
        }
        context.Select(pilot);
        foreach (var (service, method) in calls)
        {
            var reply = router.CallWire(context, service, method, Array.Empty<object?>(), _ => { });
            Assert.NotEmpty(BlueMarshal.Encode(reply));
            if (service == "structureDirectory")
            {
                Assert.Empty(Assert.IsType<Dictionary<string, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply))));
            }
            else
            {
                var rows = Assert.IsType<BlueConstructor>(reply);
                Assert.Equal("carbon.common.script.sys.crowset.CRowset", rows.Name);
                Assert.Empty(Assert.IsType<List<object?>>(rows.Arguments[1]));
            }
            Assert.IsType<PilotCallResult.Failure>(router.CallWire(context, service, method, new object?[] { 1L }, _ => { }));
            Assert.IsType<PilotCallResult.Failure>(
                router.CallWire(context, service, method, Array.Empty<object?>(), _ => { }, new() { ["unexpected"] = true })
            );
        }
        Assert.Empty(context.Notifications);
        using WorldDbContext saved = new(world.Path);
        Assert.Empty(saved.Set<MarketExecution>());
        Assert.Empty(saved.Set<NpcMarketOrder>());
    }

    [Fact]
    public void MultibuyPersistsAndPlexHistorySplitsExecutionsAtUtcMidnightAfterReopen()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 7, 23, 59, 59, TimeSpan.Zero));
        using TestWorld world = new(emptyWallets: false, clock: clock);
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (44992,'PLEX',54);
                INSERT INTO SourceRecord VALUES ('client/types','44992','{{"published":1,"marketGroupID":3,"volume":0.01}}');
                INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7]}}');
                """
            );
        }
        var pilot = world.Store.FindPilot(1000001)!;
        var market = world.Services.GetRequiredService<IPilotMarket>();
        market.Buy(pilot.PilotId, pilot.StationId, [new(MarketDefinitions.PlexTypeId, 100, 2)]);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        using var scope = world.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, clock, new(), world.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        Assert.Empty(
            Assert.IsType<Dictionary<string, object?>>(
                router.CallWire(context, "structureDirectory", "GetMyCharacterStructures", Array.Empty<object?>(), _ => { })
            )
        );
        var item = new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["typeID"] = (long)MarketDefinitions.PlexTypeId,
                ["quantity"] = 3L,
                ["price"] = 1d,
            }
        );
        object?[] purchase = [(long)pilot.StationId, new List<object?> { item }, 0L];
        Assert.Single(
            Assert.IsType<List<object?>>(
                router.CallWire(context, "marketProxy", "BuyMultipleItems", BlueMarshal.Decode(BlueMarshal.Encode(purchase)), _ => { })
            )
        );
        Assert.Single(context.Notifications, notification => notification.Name == "OnAccountChange");
        _ = WorldInitialization.Open(world.Path);
        Assert.Equal(TestingMarketRules.GrantCents - 500, market.Balance(pilot.PilotId));
        Assert.Equal(2, market.Transactions(pilot.PilotId).Count);
        context.Notifications.Clear();
        var batch = Assert.IsType<Dictionary<long, object?>>(
            router.CallWire(context, "marketProxy", "GetPlexHistory", Array.Empty<object?>(), _ => { })
        );
        var pair = Assert.IsType<object?[]>(batch[MarketDefinitions.PlexTypeId]);
        foreach (
            var (method, index, date, quantity) in new[]
            {
                ("GetPlexOldPriceHistory", 0, clock.UtcNow.UtcDateTime.Date.AddDays(-1), 2L),
                ("GetPlexNewPriceHistory", 1, clock.UtcNow.UtcDateTime.Date, 3L),
            }
        )
        {
            var reply = router.CallWire(context, "marketProxy", method, Array.Empty<object?>(), _ => { });
            Assert.Equal(BlueMarshal.Encode(pair[index]), BlueMarshal.Encode(reply));
            var rows = Assert.IsType<BlueConstructor>(reply);
            var row = Assert.IsType<BlueConstructor>(
                Assert.Single(
                    Assert.IsType<List<object?>>(rows.Arguments[1]),
                    value => Assert.IsType<BlueConstructor>(value).Arguments[1] is object?[] cells && Equals(cells[0], date.ToFileTimeUtc())
                )
            );
            Assert.Equal(new object?[] { date.ToFileTimeUtc(), 1d, 1d, 1d, quantity, 1 }, row.Arguments[1]);
        }
        Assert.Empty(context.Notifications);
    }
}
