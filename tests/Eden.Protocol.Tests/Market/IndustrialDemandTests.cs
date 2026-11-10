using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Capsuleers;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class IndustrialDemandTests
{
    [Fact]
    public void ResearchedProductionEarnsProfitAfterMaterialOpportunityCostIndustryFeesAndSalesTax()
    {
        using Eden.Protocol.Tests.Industry.IndustryTests.Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0.5}}')"
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            world.Set<SavedWorldEconomy>().Single().TestingGrants = false;
            world.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
            foreach (int type in new[] { 34, 35 })
            {
                world.Add(new SavedAdjustedPrice { TypeId = type, PriceIsk = MarketSnapshotCatalog.Quotes[type].SellCents!.Value / 100m });
            }
            world.Set<OwnedItem>().Find(Eden.Protocol.Tests.Industry.IndustryTests.Fixture.BlueprintId)!.MaterialEfficiency = 10;
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = (int)MarketSkill.Accounting,
                    Points = 768000,
                }
            );
            var wallet = world.Set<PilotWallet>().Find(f.Pilot.PilotId) ?? world.Add(new PilotWallet { PilotId = f.Pilot.PilotId }).Entity;
            wallet.Cents = 1_000_000;
            world.SaveChanges();
        }
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        long before = market.Balance(f.Pilot.PilotId);
        var request = f.Request() with
        {
            QuotedMaterials = new Dictionary<int, int> { [34] = 25, [35] = 19 },
        };
        long materialCost = request.QuotedMaterials.Sum(m => m.Value * MarketSnapshotCatalog.Quotes[m.Key].SellCents!.Value);
        var installed = f.Store.Install(f.Pilot.PilotId, request);
        Assert.True(installed.Job.CostCents > 0);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(installed.Job.DurationSeconds));
        var goods = Assert.Single(f.Store.Deliver(f.Pilot.PilotId, installed.Job.Id, f.Pilot.SystemId).Changes).Item;
        var bid = market.Orders(f.Pilot.PilotId, 215).Single(o => o.Bid && o.StationId == f.Pilot.StationId);
        var result = market.Sell(f.Pilot.PilotId, [new(goods.Id, f.Pilot.StationId, 215, bid.PriceCents, goods.Quantity)]);
        Assert.True(Assert.Single(result.Trades).TaxCents > 0);
        Assert.True(result.BalanceCents - before > materialCost);
    }

    [Fact]
    public void TechTwoBidsAreRetiredUntilProductionIsClaimedThenReactivate()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','215',
                    '{{"published":1,"marketGroupID":1,"volume":0.0025,"techLevel":2,"metaGroupID":2}}');
                """
            );
        }
        long old;
        using (WorldDbContext world = new(f.Database.Path))
        {
            var bid = world
                .Add(
                    new NpcMarketOrder
                    {
                        StationId = 60003760,
                        SystemId = 30000142,
                        RegionId = 10000002,
                        OwnerId = 1000035,
                        TypeId = 215,
                        Bid = true,
                        Kind = NpcMarketKind.Regional,
                        PriceCents = 475,
                        Entered = 100,
                        Remaining = 100,
                        Issued = f.Clock.Now,
                    }
                )
                .Entity;
            world.SaveChanges();
            old = bid.Id;
            world.Add(
                new SavedRegionalMarketStock
                {
                    OrderId = old,
                    AnchorCents = 475,
                    DemandVersion = 1,
                }
            );
            world.SaveChanges();
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var market = host.GetRequiredService<IPilotMarket>();
        Assert.Empty(market.Orders(pilot.PilotId, 215));
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Equal(0, world.Set<NpcMarketOrder>().Find(old)!.Remaining);
            world.Add(
                new SavedMarketUnlock
                {
                    TypeId = 215,
                    AccountId = 1000001,
                    PilotId = pilot.PilotId,
                    JobId = 1,
                    ClaimedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        Assert.Contains(market.Orders(pilot.PilotId, 215), o => o.Id == old && o.Bid && o.Remaining > 0);
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(4, 0, false)]
    [InlineData(1, 500001, true)]
    [InlineData(52, 0, false)]
    [InlineData(2, 0, false)]
    public void TechOneDemandDoesNotRequireAManufacturingRecipeAndExcludesFaction(int meta, int faction, bool expected)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        using (CatalogDbContext editable = new(f.Database.CatalogPath, writable: true))
        {
            editable.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/blueprints'");
            string definition = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    published = 1,
                    marketGroupID = 1,
                    volume = 0.0025,
                    metaGroupID = meta,
                    factionID = faction,
                }
            );
            editable.Database.ExecuteSqlInterpolated(
                $"UPDATE SourceRecord SET Data={definition} WHERE Dataset='client/types' AND Key='215'"
            );
            editable.Database.ExecuteSqlRaw("DELETE FROM TypeAttribute WHERE ItemTypeId=215 AND AttributeId=422");
        }
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        Assert.Equal(expected, RegionalMarketCatalog.Read(catalog).Demand.ContainsKey(215));
    }

    [Fact]
    public void BestAsksDoNotPrepareBuyDemandAndWarmReadsHaveBoundedDatabaseCost()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var market = host.GetRequiredService<IPilotMarket>();
        _ = market.BestAsks(pilot.PilotId, MarketScope.Region);
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        Assert.False(RegionalMarketCatalog.Read(catalog).DemandLoaded);
        using var metrics = SessionPerformance.Begin("warm best asks", _ => { }, force: true)!;
        _ = market.BestAsks(pilot.PilotId, MarketScope.Region);
        Assert.InRange(metrics.Commands, 1, 40);
        Assert.InRange(metrics.Connections, 1, 10);
        Assert.Equal(0, metrics.FittingCalculations);
        using WorldDbContext world = new(f.Database.Path);
        Assert.Empty(world.Set<NpcMarketOrder>().Where(o => o.Kind == NpcMarketKind.Regional && o.Bid));
    }

    [Theory]
    [InlineData(0, 1, 0.01)]
    [InlineData(1, 0, 0.01)]
    [InlineData(1, 1, 0)]
    public void UnpublishedUnlistedAndNonphysicalMaterialsHaveNoGeneratedDemand(int published, int marketGroup, double volume)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        string definition = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                published,
                marketGroupID = marketGroup,
                volume,
            }
        );
        using (CatalogDbContext editable = new(f.Database.CatalogPath, writable: true))
        {
            editable.Database.ExecuteSqlInterpolated(
                $"UPDATE SourceRecord SET Data={definition} WHERE Dataset='client/types' AND Key='34'"
            );
        }
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        Assert.DoesNotContain(34, RegionalMarketCatalog.Read(catalog).Demand.Keys);
        Assert.DoesNotContain(1130, RegionalMarketCatalog.Read(catalog).Demand.Keys);
        Assert.DoesNotContain(3380, RegionalMarketCatalog.Read(catalog).Demand.Keys);
    }

    [Fact]
    public void FreshWorldHasMaterialAndProductBuyersWithoutUnlockingSupply()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var market = host.GetRequiredService<IPilotMarket>();
        foreach (int type in new[] { 34, 1230, 215 })
        {
            var book = market.Orders(pilot.PilotId, type);
            var bid = Assert.Single(book, o => o.Bid && o.StationId == 60003760);
            long reference = MarketSnapshotCatalog.Quotes[type].BuyCents!.Value;
            Assert.Equal(RegionalMarketRules.DemandCapacity(reference, RegionalMarketRole.Hub, 0), bid.Entered);
            Assert.DoesNotContain(book, o => !o.Bid);
        }
        using WorldDbContext world = new(f.Database.Path);
        Assert.Empty(world.Set<SavedMarketUnlock>());
        Assert.Empty(world.Set<MarketExecution>());
        Assert.All(world.Set<NpcMarketOrder>().Where(o => o.Kind == NpcMarketKind.Regional), o => Assert.True(o.Bid));
        // Type-filtered preparation does not materialize the rest of the catalog.
        Assert.All(
            world.Set<NpcMarketOrder>().Where(o => o.Kind == NpcMarketKind.Regional),
            o => Assert.Contains(o.TypeId, new[] { 34, 1230, 215 })
        );
    }

    [Fact]
    public async Task DemandUpgradePreservesDepletionAndRecoveryAcrossRestart()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        long id;
        int capacity;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
            var market = host.GetRequiredService<IPilotMarket>();
            var bid = market.Orders(pilot.PilotId, 34).Single(o => o.Bid && o.StationId == 60003760);
            id = bid.Id;
            capacity = bid.Entered;
            using (WorldDbContext world = new(f.Database.Path))
            {
                var order = world.Set<NpcMarketOrder>().Find(id)!;
                order.Entered = 50;
                order.Remaining = 25;
                world.Set<SavedRegionalMarketStock>().Find(id)!.DemandVersion = 0;
                world.SaveChanges();
            }
            Assert.Equal(capacity / 2, market.Orders(pilot.PilotId, 34).Single(o => o.Id == id).Remaining);
            using var presence = host.GetRequiredService<IHumanPresence>().Select(pilot.PilotId, pilot.SystemId);
            f.Clock.Seconds += 300;
            Assert.Equal(capacity / 2, market.Orders(pilot.PilotId, 34).Single(o => o.Id == id).Remaining);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        f.Clock.Seconds += 86400;
        using (WorldDbContext legacy = new(f.Database.Path))
        {
            legacy.Database.ExecuteSqlRaw("ALTER TABLE WorldRegionalMarketStock DROP COLUMN DemandVersion; PRAGMA user_version=132");
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var person = reopened.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var book = reopened.GetRequiredService<IPilotMarket>();
        Assert.Equal(capacity / 2, book.Orders(person.PilotId, 34).Single(o => o.Id == id).Remaining);
        using var selected = reopened.GetRequiredService<IHumanPresence>().Select(person.PilotId, person.SystemId);
        f.Clock.Seconds += 300;
        Assert.True(book.Orders(person.PilotId, 34).Single(o => o.Id == id).Remaining > capacity / 2);
    }

    [Fact]
    public void DemandQuantitiesGrowthRecoveryAndSpecialistsUseTypedRules()
    {
        Assert.Equal(150, RegionalMarketRules.DemandCapacity(100_000_000, RegionalMarketRole.Hub, 0));
        Assert.Equal(600, RegionalMarketRules.DemandCapacity(100_000_000, RegionalMarketRole.Hub, TimeSpan.FromHours(24).Ticks));
        Assert.Equal(30, RegionalMarketRules.DemandCapacity(100_000_000, RegionalMarketRole.Industry, 0));
        Assert.Equal(1, RegionalMarketRules.DemandCapacity(long.MaxValue, RegionalMarketRole.Hub, 0));
        Assert.Equal(int.MaxValue, RegionalMarketRules.DemandCapacity(1, RegionalMarketRole.Hub, TimeSpan.FromDays(10).Ticks));
        Assert.True(RegionalMarketRules.Buys(1230, InventoryCategory.Asteroid, true, false, RegionalMarketRole.Mining));
        Assert.False(RegionalMarketRules.Buys(34, InventoryCategory.Material, false, true, RegionalMarketRole.Mining));
        Assert.True(RegionalMarketRules.Buys(34, InventoryCategory.Material, false, true, RegionalMarketRole.Industry));
        Assert.False(RegionalMarketRules.Buys(603, InventoryCategory.Ship, false, false, RegionalMarketRole.Industry));
        Assert.False(RegionalMarketRules.Buys(216, InventoryCategory.Charge, false, false, RegionalMarketRole.Industry));
        var recovered = RegionalMarketRules.Recover(new(0, 101, 0), 101, TimeSpan.FromHours(1).Ticks, demand: true);
        Assert.Equal(101, recovered.Remaining);
        Assert.Equal(
            int.MaxValue,
            RegionalMarketRules
                .Recover(new(int.MaxValue, int.MaxValue, 0), int.MaxValue, TimeSpan.FromDays(1000).Ticks, demand: true)
                .Remaining
        );
    }
}
