using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Industry;
using Eden.Protocol.Market;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class MarketTests
{
    private const int Scordite = 1228;
    private const int Tritanium = 34;
    private const long OreId = InventoryDefinitions.FirstOwnedItemId + 100;

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public IPilotMarket Store => Db.Services.GetRequiredService<IPilotMarket>();
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();

        public Fixture()
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemCategory VALUES (25,'Asteroid'),(4,'Material');
                INSERT INTO ItemGroup VALUES (460,'Scordite',25),(18,'Mineral',4),(462,'Veldspar',25);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (1228,'Scordite',460),(1230,'Veldspar',462),(34,'Tritanium',18),
                    (35,'Pyerite',18),(16622,'Accounting',257);
                INSERT INTO SourceRecord VALUES ('client/types','1228','{{"volume":0.15}}'),('client/types','1230','{{"volume":0.1}}'),
                    ('client/types','34','{{"volume":0.01}}'),('client/types','35','{{"volume":0.01}}'),
                    ('client/stationoperations','14','{{"services":[5,7,17]}}');
                INSERT INTO SourceRecord VALUES
                    ('client/accountingentrytypes','2','{{"entryTypeNameID":59794,"name":"Market Transaction"}}'),
                    ('client/accountingentrytypes','54','{{"entryTypeNameID":59846,"name":"Transaction Tax"}}');
                INSERT INTO TypeAttribute VALUES (16622,275,3);
                """
            );
            using WorldDbContext world = new(Db.Path);
            world.Add(
                new OwnedItem
                {
                    Id = OreId,
                    OwnerId = Pilot.PilotId,
                    TypeId = Scordite,
                    LocationId = Pilot.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 133,
                }
            );
            world.SaveChanges();
        }

        public MarketSale Sale(long id = OreId, int quantity = 133, long limit = 500) =>
            new(id, Pilot.StationId, Scordite, limit, quantity);

        public MarketResult Sell(params MarketSale[] sales) => Store.Sell(Pilot.PilotId, sales.Length == 0 ? [Sale()] : sales);

        public MarketResult Buy(int type = InventoryDefinitions.CivilianMinerType, int quantity = 1, long limit = 10000) =>
            Store.Buy(Pilot.PilotId, Pilot.StationId, [new(type, limit, quantity)]);

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void MiningProceedsCanBuyEquipmentAndBothSidesPersistWithoutRepeatingSeed()
    {
        using Fixture f = new();
        Assert.Equal(0, f.Store.Balance(f.Pilot.PilotId));
        Assert.Equal(7, f.Store.Orders(f.Pilot.PilotId).Count);
        var sale = f.Sell();
        Assert.Equal(61512, sale.BalanceCents);
        Assert.Equal(4988, Assert.Single(sale.Trades).TaxCents);
        Assert.Equal(0, Assert.Single(sale.Changes).Item.Quantity);
        Assert.Throws<InvalidDataException>(() => f.Sell());
        var purchase = f.Buy(quantity: 2);
        Assert.Equal(41512, purchase.BalanceCents);
        Assert.Equal(2, Assert.Single(purchase.Trades).Quantity);
        Assert.Equal(3, Assert.Single(purchase.Changes).Item.Quantity);
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(41512, f.Store.Balance(f.Pilot.PilotId));
        Assert.Equal(
            3,
            Assert
                .Single(f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType)
                .Quantity
        );
        var book = f.Store.Orders(f.Pilot.PilotId);
        Assert.Equal(98, Assert.Single(book, o => o.TypeId == InventoryDefinitions.CivilianMinerType).Remaining);
        Assert.Equal(999867, Assert.Single(book, o => o.TypeId == Scordite).Remaining);
        Assert.Equal(2, f.Store.Transactions(f.Pilot.PilotId).Count);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Single(world.Set<MarketStation>());
        Assert.Equal(7, world.Set<NpcMarketOrder>().Count());
    }

    [Fact]
    public void DockedMiningHoldOreCanBeSoldWithoutMovingOrdinaryCargo()
    {
        using Fixture f = new();
        PilotFleetTests.AddVenture(f.Db);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.Pilot.ShipId)!.TypeId = (int)StarterShipType.Venture;
            world.SaveChanges();
        }
        var pilot = f.Pilot;
        f.Inventory.Move(pilot.PilotId, [OreId], pilot.StationId, pilot.ShipId, (int)InventoryFlag.GeneralMiningHold, null);
        var result = f.Sell(f.Sale(quantity: 33));
        Assert.Equal(15262, result.BalanceCents);
        var saved = Assert.Single(f.Inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, saved.FlagId);
        Assert.Equal(100, saved.Quantity);
        Assert.Single(f.Inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
    }

    [Fact]
    public void NewLaserOffersReachVisitedStationsWithoutRestockingOldOrders()
    {
        using Fixture f = new();
        f.Sell(f.Sale(quantity: 10));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<NpcMarketOrder>().Single(o => o.TypeId == InventoryDefinitions.CivilianMinerType).Remaining = 0;
            world.Set<NpcMarketOrder>().Single(o => o.TypeId == Tritanium && !o.Bid).Issued = DateTime.UtcNow.AddDays(-366).ToFileTimeUtc();
            world.SaveChanges();
        }
        var original = f.Store.Orders(f.Pilot.PilotId);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT INTO ItemType(Id,Name,GroupId) VALUES (483,'Miner I',54),(482,'Miner II',54)");
        }
        var updated = f.Store.Orders(f.Pilot.PilotId);
        Assert.Equal(original, updated.Where(o => o.TypeId != (int)MiningLaserType.MinerI && o.TypeId != (int)MiningLaserType.MinerII));
        Assert.Equal(100000, Assert.Single(updated, o => o.TypeId == (int)MiningLaserType.MinerI).PriceCents);
        Assert.Equal(500000, Assert.Single(updated, o => o.TypeId == (int)MiningLaserType.MinerII).PriceCents);
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(updated, f.Store.Orders(f.Pilot.PilotId));
        Assert.Equal(4625, f.Store.Balance(f.Pilot.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<MarketStation>());
        Assert.Equal(9, saved.Set<NpcMarketOrder>().Count());
        Assert.Equal(0, saved.Set<NpcMarketOrder>().Single(o => o.TypeId == InventoryDefinitions.CivilianMinerType).Remaining);
        Assert.Equal(999990, saved.Set<NpcMarketOrder>().Single(o => o.TypeId == Scordite).Remaining);
    }

    [Fact]
    public void ModuleBlueprintsAndMexallonCanBePurchasedWithoutRestockingEarlierOffers()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(OreId)!.Quantity = 1000;
            world.SaveChanges();
        }
        f.Sell(f.Sale(quantity: 1000));
        var original = f.Store.Orders(f.Pilot.PilotId);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemCategory VALUES (9,'Blueprint');
                INSERT INTO ItemGroup VALUES (167,'Blueprint',9);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (785,'Miner I Blueprint',167),(1032,'Small Shield Booster I Blueprint',167),
                    (1072,'1MN Afterburner I Blueprint',167),(1112,'75mm Gatling Rail I Blueprint',167),(36,'Mexallon',18);
                INSERT INTO SourceRecord VALUES ('client/types','1112','{{"volume":0.01}}'),('client/types','36','{{"volume":0.01}}');
                """
            );
        }
        var book = f.Store.Orders(f.Pilot.PilotId);
        Assert.Equal(original, book.Where(o => original.Any(old => old.Id == o.Id)));
        foreach (
            var blueprint in new[]
            {
                IndustryBlueprintType.MinerI,
                IndustryBlueprintType.SmallShieldBoosterI,
                IndustryBlueprintType.OneMnAfterburnerI,
                IndustryBlueprintType.GatlingRail75MmI,
            }
        )
        {
            var offer = Assert.Single(book, o => o.TypeId == (int)blueprint);
            Assert.False(offer.Bid);
            Assert.Equal(100000, offer.PriceCents);
            Assert.Equal(100, offer.Remaining);
        }
        var bought = f.Buy((int)IndustryBlueprintType.GatlingRail75MmI, limit: 100000);
        Assert.Equal(362500, bought.BalanceCents);
        var blueprintItem = Assert.Single(bought.Changes).Item;
        Assert.Equal((int)InventoryCategory.Blueprint, blueprintItem.CategoryId);
        Assert.False(blueprintItem.Singleton);
        Assert.Equal(350500, f.Buy(36, 2, 6000).BalanceCents);
        _ = new SqliteWorld(f.Db.Path);
        var after = f.Store.Orders(f.Pilot.PilotId);
        Assert.Equal(99, Assert.Single(after, o => o.TypeId == (int)IndustryBlueprintType.GatlingRail75MmI).Remaining);
        Assert.Equal(9998, Assert.Single(after, o => o.TypeId == 36).Remaining);
        Assert.Equal(999000, Assert.Single(after, o => o.TypeId == Scordite).Remaining);
        Assert.Equal(blueprintItem, Assert.Single(f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId), i => i.Id == blueprintItem.Id));
    }

    [Fact]
    public void RegionalHistoryUsesActualDailyExecutionsAndWeightedPrices()
    {
        using Fixture f = new();
        f.Sell();
        var bought = Assert.Single(f.Buy(Tritanium, 2, 240).Changes).Item;
        f.Sell(new MarketSale(bought.Id, f.Pilot.StationId, Tritanium, 200, 2));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = OreId + 10,
                    OwnerId = 90000002,
                    TypeId = Tritanium,
                    LocationId = f.Pilot.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 3,
                }
            );
            world.Set<MarketExecution>().Single(t => t.TypeId == Scordite).Time = DateTime
                .UtcNow.Date.AddDays(-1)
                .AddHours(12)
                .ToFileTimeUtc();
            world.SaveChanges();
        }
        f.Store.Sell(90000002, [new(OreId + 10, f.Pilot.StationId, Tritanium, 200, 3)]);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var foreign = world
                .Add(
                    new NpcMarketOrder
                    {
                        StationId = f.Pilot.StationId + 1,
                        SystemId = f.Pilot.SystemId,
                        RegionId = f.Pilot.RegionId + 1,
                        OwnerId = f.Pilot.StationOwnerId,
                        TypeId = Tritanium,
                        PriceCents = 999,
                        Entered = 100,
                        Remaining = 100,
                    }
                )
                .Entity;
            world.SaveChanges();
            long own = world.Set<NpcMarketOrder>().Single(o => o.StationId == f.Pilot.StationId && o.TypeId == Tritanium && !o.Bid).Id;
            foreach (
                var (order, time) in new[]
                {
                    (foreign.Id, DateTime.UtcNow),
                    (own, DateTime.UtcNow.AddDays(-366)),
                    (own, DateTime.UtcNow.AddDays(1)),
                }
            )
            {
                world.Add(
                    new MarketExecution
                    {
                        PilotId = f.Pilot.PilotId,
                        OrderId = order,
                        StationId = f.Pilot.StationId,
                        TypeId = Tritanium,
                        Quantity = 1000,
                        PriceCents = 999,
                        Time = time.ToFileTimeUtc(),
                    }
                );
            }
            world.SaveChanges();
        }
        var days = f.Store.History(f.Pilot.PilotId, [Tritanium, Scordite]);
        var tritanium = Assert.Single(days, d => d.TypeId == Tritanium);
        Assert.Equal(DateTime.UtcNow.Date.ToFileTimeUtc(), tritanium.Date);
        Assert.Equal(200, tritanium.LowPriceCents);
        Assert.Equal(240, tritanium.HighPriceCents);
        Assert.Equal(1480m / 7, tritanium.AveragePriceCents);
        Assert.Equal(7, tritanium.Volume);
        Assert.Equal(3, tritanium.Orders);
        Assert.Equal(133, Assert.Single(days, d => d.TypeId == Scordite).Volume);
        Assert.Empty(f.Store.History(f.Pilot.PilotId, [(int)MiningLaserType.MinerII]));
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(days, f.Store.History(f.Pilot.PilotId, [Tritanium, Scordite]));
    }

    [Fact]
    public void PurchasesNeverReuseConsumedItemIdsEvenAfterRestart()
    {
        using Fixture f = new();
        f.Sell();
        var first = Assert.Single(f.Buy(Tritanium, 1, 240).Changes).Item;
        Assert.True(first.Id > OreId);
        MarketSale sale = new(first.Id, f.Pilot.StationId, Tritanium, 200, 1);
        f.Sell(sale);
        _ = new SqliteWorld(f.Db.Path);
        var second = Assert.Single(f.Buy(Tritanium, 1, 240).Changes).Item;
        Assert.True(second.Id > first.Id);
        Assert.Throws<InvalidDataException>(() => f.Sell(sale));
        Assert.Equal(1, f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId).Single(i => i.Id == second.Id).Quantity);
    }

    [Fact]
    public void ExecutionUsesSavedPricesAndAccountingRatherThanUntrustedClientTotals()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = (int)MarketSkill.Accounting,
                    Points = TrainedSkill.PointsForLevel(3, 5),
                }
            );
            world.SaveChanges();
        }
        var sale = f.Sell(f.Sale(limit: 1));
        Assert.Equal(500, Assert.Single(sale.Trades).PriceCents);
        Assert.Equal(2244, sale.Trades[0].TaxCents);
        Assert.Equal(64256, sale.BalanceCents);
        Assert.Equal(10000, Assert.Single(f.Buy(limit: 1000000).Trades).PriceCents);
    }

    [Fact]
    public void MissingFundsBadLimitsAndInvalidBatchRollBackWalletStockAndInventory()
    {
        using Fixture f = new();
        var before = f.Store.Orders(f.Pilot.PilotId);
        Assert.Throws<InvalidDataException>(() => f.Buy());
        Assert.Throws<InvalidDataException>(() => f.Sell(f.Sale(limit: 501)));
        Assert.Throws<InvalidDataException>(() => f.Sell(f.Sale(quantity: 134)));
        Assert.Throws<InvalidDataException>(() => f.Sell(f.Sale(quantity: 0)));
        Assert.Throws<InvalidDataException>(() => f.Sell(f.Sale(), f.Sale()));
        var foreign = Assert.Single(f.Inventory.List(90000002, f.Pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        Assert.Throws<InvalidDataException>(() => f.Sell(f.Sale(quantity: 10), f.Sale(foreign.Id, 1)));
        Assert.Equal(0, f.Store.Balance(f.Pilot.PilotId));
        Assert.Empty(f.Store.Transactions(f.Pilot.PilotId));
        Assert.Equal(before, f.Store.Orders(f.Pilot.PilotId));
        Assert.Equal(133, f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId).Single(i => i.Id == OreId).Quantity);
        f.Sell();
        var balance = f.Store.Balance(f.Pilot.PilotId);
        Assert.Throws<InvalidDataException>(() =>
            f.Store.Buy(
                f.Pilot.PilotId,
                f.Pilot.StationId,
                [new(InventoryDefinitions.CivilianMinerType, 10000, 1), new(Tritanium, 240, 100000)]
            )
        );
        Assert.Equal(balance, f.Store.Balance(f.Pilot.PilotId));
        Assert.Equal(100, f.Store.Orders(f.Pilot.PilotId).Single(o => o.TypeId == InventoryDefinitions.CivilianMinerType).Remaining);
    }

    [Fact]
    public void OverflowRollsBackMoneyStockAndItems()
    {
        using Fixture f = new();
        f.Sell();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world
                .Set<OwnedItem>()
                .Single(i => i.OwnerId == f.Pilot.PilotId && i.TypeId == InventoryDefinitions.CivilianMinerType && !i.Singleton)
                .Quantity = int.MaxValue;
            world.SaveChanges();
        }
        Assert.Throws<OverflowException>(() => f.Buy());
        Assert.Equal(61512, f.Store.Balance(f.Pilot.PilotId));
        Assert.Equal(100, f.Store.Orders(f.Pilot.PilotId).Single(o => o.TypeId == InventoryDefinitions.CivilianMinerType).Remaining);
        Assert.Single(f.Store.Transactions(f.Pilot.PilotId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(f.Pilot.PilotId)!.Cents = long.MaxValue;
            world.Add(
                new OwnedItem
                {
                    Id = OreId,
                    OwnerId = f.Pilot.PilotId,
                    TypeId = Scordite,
                    LocationId = f.Pilot.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<OverflowException>(() => f.Sell(f.Sale(quantity: 1)));
        Assert.Equal(long.MaxValue, f.Store.Balance(f.Pilot.PilotId));
        Assert.Equal(999867, f.Store.Orders(f.Pilot.PilotId).Single(o => o.TypeId == Scordite).Remaining);
        Assert.Equal(1, f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId).Single(i => i.Id == OreId).Quantity);
        Assert.Single(f.Store.Transactions(f.Pilot.PilotId));
    }

    [Fact]
    public void StationAndSourceValidationPreventsRemoteFittedAssembledAndSpaceSales()
    {
        using Fixture f = new();
        Assert.Throws<InvalidDataException>(() => f.Sell(f.Sale() with { StationId = f.Pilot.StationId + 1 }));
        Assert.Throws<InvalidDataException>(() => f.Store.Buy(f.Pilot.PilotId, f.Pilot.StationId + 1, [new(Tritanium, 240, 1)]));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(OreId)!.Singleton = true;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Sell());
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(OreId)!;
            item.Singleton = false;
            item.LocationId = f.Pilot.ShipId;
            item.FlagId = (int)InventoryFlag.HighSlot0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Sell());
        f.Db.Store.SetPosition(f.Pilot.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => f.Sell());
        Assert.Throws<InvalidDataException>(() => f.Buy());
        Assert.Equal(0, f.Store.Balance(f.Pilot.PilotId));
    }

    [Fact]
    public void DepletedOrExpiredOrdersDoNotRegenerateAndImmediateFillsCanBePartial()
    {
        using Fixture f = new();
        f.Sell();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<NpcMarketOrder>().Single(o => o.TypeId == InventoryDefinitions.CivilianMinerType).Remaining = 1;
            world.SaveChanges();
        }
        var bought = f.Buy(quantity: 5);
        Assert.Equal(1, Assert.Single(bought.Trades).Quantity);
        Assert.Equal(51512, bought.BalanceCents);
        _ = new SqliteWorld(f.Db.Path);
        Assert.DoesNotContain(f.Store.Orders(f.Pilot.PilotId), o => o.TypeId == InventoryDefinitions.CivilianMinerType);
        Assert.Throws<InvalidDataException>(() => f.Buy());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<NpcMarketOrder>().Single(o => o.TypeId == Tritanium && !o.Bid).Issued = DateTime.UtcNow.AddDays(-366).ToFileTimeUtc();
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Buy(Tritanium, 1, 240));
    }

    [Fact]
    public async Task CompetingPilotsCannotOversellTheSameFiniteNpcDemand()
    {
        using Fixture f = new();
        f.Store.Orders(f.Pilot.PilotId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(OreId)!.Quantity = 80;
            world.Add(
                new OwnedItem
                {
                    Id = OreId + 1,
                    OwnerId = 90000002,
                    TypeId = Scordite,
                    LocationId = f.Pilot.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 80,
                }
            );
            world.Set<NpcMarketOrder>().Single(o => o.TypeId == Scordite).Remaining = 100;
            world.SaveChanges();
        }
        var results = await Task.WhenAll(
            Task.Run(() => f.Sell(f.Sale(quantity: 80))),
            Task.Run(() => f.Store.Sell(90000002, [new(OreId + 1, f.Pilot.StationId, Scordite, 500, 80)]))
        );
        Assert.Equal(100, results.Sum(r => Assert.Single(r.Trades).Quantity));
        Assert.Equal(46250, results.Sum(r => r.BalanceCents));
        Assert.Equal(
            60,
            f.Inventory.List(f.Pilot.PilotId, f.Pilot.StationId)
                .Concat(f.Inventory.List(90000002, f.Pilot.StationId))
                .Where(i => i.TypeId == Scordite)
                .Sum(i => i.Quantity)
        );
    }

    [Fact]
    public void UpgradeFromVersionSixteenPreservesOreAndDoesNotGrantMoney()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldMarketTrade; DROP TABLE WorldMarketOrder; DROP TABLE WorldMarketStation; DROP TABLE "
                    + "WorldWallet; DROP TABLE WorldShipHealth; DROP TABLE WorldWreck; DROP TABLE WorldRepairPayment; DROP "
                    + "TABLE WorldNpcAggression; DROP TABLE WorldShipLoss; DROP TABLE WorldNpc; DROP TABLE WorldNpcBounty; "
                    + "DROP TABLE WorldWeaponCooldown; DELETE FROM WorldItem WHERE TypeId=3638; PRAGMA user_version=16"
            );
        }
        _ = new SqliteWorld(f.Db.Path);
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(pilot, f.Pilot);
        Assert.Equal(0, f.Store.Balance(pilot.PilotId));
        Assert.Equal(133, f.Inventory.List(pilot.PilotId, pilot.StationId).Single(i => i.Id == OreId).Quantity);
        Assert.Equal(7, f.Store.Orders(pilot.PilotId).Count);
    }

    [Fact]
    public void StationMarketButtonChecksTheCurrentServiceWithoutCreatingOrdersOrMovingMoney()
    {
        using Fixture f = new();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var pilot = f.Pilot;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, TimeProvider.System, new(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        object?[] bind =
        [
            (long)pilot.StationId,
            new object?[]
            {
                "DoStandingCheckForStationService",
                new object?[] { (long)StationService.Market },
                new Dictionary<string, object?>(),
            },
        ];
        Assert.Same(PilotCallResult.Unsupported, router.CallWire(context, "corpStationMgr", "MachoBindObject", bind, _ => { }));
        context.Select(pilot);
        var result = Assert.IsType<object?[]>(router.CallWire(context, "corpStationMgr", "MachoBindObject", bind, _ => { }));
        Assert.IsType<BlueObjectReference>(result[0]);
        Assert.Null(result[1]);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Empty(saved.Set<NpcMarketOrder>());
            Assert.Empty(saved.Set<MarketExecution>());
            Assert.Empty(context.Notifications);
        }
        Assert.Throws<InvalidDataException>(() => f.Store.CheckStation(pilot.PilotId, pilot.StationId + 1));
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data='{{\"services\":[]}}' WHERE Dataset='client/stationoperations'");
        }
        Assert.IsType<PilotCallResult.Failure>(router.CallWire(context, "corpStationMgr", "MachoBindObject", bind, _ => { }));
        f.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => f.Store.CheckStation(pilot.PilotId, pilot.StationId));
    }

    [Fact]
    public void NativeCallsReturnVersionedBooksAndNotifyWalletAndInventoryTogether()
    {
        using Fixture f = new();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var pilot = f.Pilot;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, TimeProvider.System, new SharedSpace(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        object? Call(string service, string method, params object?[] args) => router.CallWire(context, service, method, args, _ => { });
        Assert.Same(PilotCallResult.Unsupported, Call("marketProxy", "GetOrders", (long)Scordite));
        context.Select(pilot);
        Assert.Null(Call("charMgr", "GetPrivateInfoOnCorpChange", pilot.PilotId));
        Assert.Empty(Assert.IsType<List<object?>>(Call("LPSvc", "GetAllMyCharacterWalletLPBalances")));
        Assert.Equal(2, Assert.IsType<List<object?>>(Call("account", "GetEntryTypes")).Count);
        var cached = Assert.IsType<BlueInstance>(Call("marketProxy", "GetOrders", (long)Scordite));
        var state = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(cached)) is BlueInstance decoded ? decoded.State : null);
        Assert.Equal("always", BlueMarshal.Text(Assert.IsType<Dictionary<string, object?>>(state[0])["versionCheck"]));
        Assert.NotEmpty(Assert.IsType<byte[]>(state[1]));
        var item = new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = OreId,
                ["stationID"] = (long)pilot.StationId,
                ["typeID"] = (long)Scordite,
                ["quantity"] = 133L,
                ["price"] = 5d,
            }
        );
        Assert.Equal(true, Call("marketProxy", "PlaceMultiSellOrder", new List<object?> { item }, false, 0L, null));
        Assert.Equal(615.12, Call("account", "GetCashBalance", 0L));
        Assert.Equal(
            new object?[] { "cash", pilot.PilotId, 615.12 },
            Assert.Single(context.Notifications, n => n.Name == "OnAccountChange").Arguments
        );
        var removal = Assert.Single(context.Notifications, n => n.Name == "OnItemChange");
        var removedRow = Assert.IsType<BlueConstructor>(removal.Arguments[0]);
        var removedFields = Assert.IsType<object?[]>(removedRow.Arguments[1]);
        Assert.Equal((long)InventoryLocation.Graveyard, removedFields[(int)InventoryColumn.Location]);
        Assert.Equal(133, removedFields[(int)InventoryColumn.Quantity]);
        Assert.Equal((long)pilot.StationId, Assert.IsType<Dictionary<long, object?>>(removal.Arguments[1])[(int)InventoryColumn.Location]);
        Assert.Single(context.Notifications, n => n.Name == "OnOwnOrdersChanged");
        var now = DateTime.UtcNow;
        Assert.Equal(
            2,
            Assert
                .IsType<List<object?>>(
                    Call("account", "GetTransactions", (long)WalletAccountKey.Cash, (long)now.Year, (long)now.Month, false)
                )
                .Count
        );
        Assert.Equal(
            2,
            Assert.IsType<List<object?>>(Call("account", "GetTransactions", (long)WalletAccountKey.Cash, null, null, false)).Count
        );
        Assert.Single(Assert.IsType<List<object?>>(Call("marketProxy", "CharGetTransactions", (object?)null)));
        var history = Assert.IsType<Dictionary<long, object?>>(
            Call("marketProxy", "GetHistoryForManyTypeIDs", new HashSet<long> { Scordite, Tritanium })
        );
        var pair = Assert.IsType<object?[]>(history[Scordite]);
        Assert.Empty(Assert.IsType<List<object?>>(Assert.IsType<BlueConstructor>(pair[0]).Arguments[1]));
        var today = Assert.Single(Assert.IsType<List<object?>>(Assert.IsType<BlueConstructor>(pair[1]).Arguments[1]));
        Assert.Equal(new object?[] { now.Date.ToFileTimeUtc(), 5d, 5d, 5d, 133L, 1 }, Assert.IsType<BlueConstructor>(today).Arguments[1]);
        Assert.NotEmpty(BlueMarshal.Encode(history));
        var empty = Assert.IsType<object?[]>(history[Tritanium]);
        Assert.All(empty, rows => Assert.Empty(Assert.IsType<List<object?>>(Assert.IsType<BlueConstructor>(rows).Arguments[1])));
        Assert.IsType<Dictionary<long, object?>>(Call("marketProxy", "GetPlexHistory"));
        Assert.IsType<PilotCallResult.Failure>(
            Call("marketProxy", "PlaceBuyOrder", (long)pilot.StationId, 3651L, 100d, 1L, -1L, 1L, 1L, false, null)
        );
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(0.001d)]
    public void RejectsInvalidMarketMoney(double value) => Assert.Throws<InvalidDataException>(() => MarketDefinitions.PriceCents(value));
}
