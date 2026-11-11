using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class StarterMarketTests
{
    private const long Pilot = 90000001;
    private const int Hub = 60003760;
    private const int SchoolStation = 60015157;
    private const int OrdinaryStation = 60003999;
    private const int SchoolSystem = 30000141;
    private const int ShieldOperation = 3416;
    private const int RepairSystems = 3393;
    private const long StartingCents = 1_000_000L * MarketDefinitions.CentsPerIsk;

    [Theory]
    [InlineData(InventoryCategory.Module, true, true, 1, false, true)]
    [InlineData(InventoryCategory.Skill, true, true, 1, false, true)]
    [InlineData(InventoryCategory.Module, false, true, 1, false, false)]
    [InlineData(InventoryCategory.Module, true, false, 1, false, false)]
    [InlineData(InventoryCategory.Module, true, true, 2, false, false)]
    [InlineData(InventoryCategory.Module, true, true, 1, true, false)]
    [InlineData(InventoryCategory.Blueprint, true, true, 1, false, false)]
    public void BootstrapEligibilityRejectsUnpublishedAdvancedFactionAndBlueprintTypes(
        InventoryCategory category,
        bool published,
        bool onMarket,
        int tech,
        bool faction,
        bool expected
    ) => Assert.Equal(expected, StarterMarketRules.CanSupply(category, published, onMarket, tech, faction));

    [Fact]
    public void UnsupportedCatalogVariantsDoNotProduceStarterSellOrders()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/types','483','{{"published":0,"marketGroupID":1}}'),
                    ('client/types','399','{{"published":1}}'),
                    ('client/types','561','{{"published":1,"marketGroupID":1,"techLevel":2}}');
                """
            );
        }
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        foreach (
            int type in new[]
            {
                (int)StarterMarketType.Miner,
                (int)StarterMarketType.SmallShieldBooster,
                (int)StarterMarketType.GatlingRailgun,
            }
        )
        {
            Assert.DoesNotContain(market.Orders(Pilot, type), order => !order.Bid);
        }
    }

    [Theory]
    [InlineData(Hub)]
    [InlineData(SchoolStation)]
    public void StarterStationsAndHubsSupplyTheCompleteCohortAndPrerequisiteBooksWithoutUnlocks(int station)
    {
        using Fixture f = new(station);
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        var book = market.Orders(Pilot);
        foreach (int type in StarterMarketRules.Types.Append(ShieldOperation).Append(RepairSystems))
        {
            var ask = Assert.Single(book, order => order.StationId == station && order.TypeId == type && !order.Bid);
            Assert.True(ask.Remaining > 0);
            Assert.Equal(ask.Entered, ask.Remaining);
            var category = f.Catalog.FittingType(type)!.CategoryId;
            Assert.Equal(RegionalMarketRules.InitialStock((InventoryCategory)category), ask.Entered);
            long expected = RegionalMarketRules.SnapshotAnchor(
                MarketSnapshotCatalog.Quotes[type].SellCents!.Value,
                station == Hub ? RegionalMarketRole.Hub : RegionalMarketRole.Industry,
                bid: false
            );
            Assert.Equal(expected, ask.PriceCents);
        }
        Assert.DoesNotContain(book, order => !order.Bid && order.TypeId == (int)StarterProductionType.Badger);
        Assert.DoesNotContain(book, order => !order.Bid && order.TypeId == (int)IndustryBlueprintType.Venture);
        Assert.Equal(book, market.Orders(Pilot));
        Assert.Equal(StartingCents, market.Balance(Pilot));
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Empty(saved.Set<SavedMarketUnlock>());
        Assert.Empty(saved.Set<MarketExecution>());
    }

    [Fact]
    public void OrdinaryStationsRetainProductionLocksAndUnsafeOrMarketlessSchoolsGetNoStarterSupply()
    {
        using Fixture f = new(OrdinaryStation);
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        Assert.DoesNotContain(market.Orders(Pilot), order => !order.Bid && order.StationId == OrdinaryStation);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000141','{{\"securityStatus\":0.1}}')"
            );
        }
        Assert.DoesNotContain(RegionalMarketCatalog.Read(f.Catalog).Stations, station => station.Station.Id == SchoolStation);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000141','{{"securityStatus":1.0}}');
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','999','{{"services":[14]}}');
                UPDATE Station SET OperationId=999 WHERE Id=60015157;
                """
            );
        }
        Assert.DoesNotContain(RegionalMarketCatalog.Read(f.Catalog).Stations, station => station.Station.Id == SchoolStation);
    }

    [Fact]
    public void SchoolMappingIgnoresAlternativeStationsAndSupportsSingleStationStarterSchools()
    {
        using Fixture f = new();
        var profiles = RegionalMarketCatalog.Read(f.Catalog);
        Assert.True(profiles.Stations.Single(station => station.Station.Id == SchoolStation).StartingStation);
        Assert.False(profiles.Stations.Single(station => station.Station.Id == Hub).StartingStation);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/schools','31','{{"is_starter_space_school":1,"startingStations":[60003999]}}');
                """
            );
        }
        Assert.True(
            RegionalMarketCatalog.Read(f.Catalog).Stations.Single(station => station.Station.Id == OrdinaryStation).StartingStation
        );
    }

    [Fact]
    public void StarterStockAndPaymentsSurviveReopenAndRecoverOnlyAtExistingActiveTimeBoundaries()
    {
        using Fixture f = new();
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        int type = (int)StarterMarketType.Venture;
        var ask = market.Orders(Pilot, type).Single(order => order.StationId == SchoolStation && !order.Bid);
        var bought = market.Buy(Pilot, SchoolStation, [new(type, ask.PriceCents, 1)]);
        Assert.Equal(StartingCents - ask.PriceCents, bought.BalanceCents);
        Assert.Single(bought.Changes);
        Assert.Single(bought.Trades);
        using (WorldDbContext saved = new(f.Database.Path))
        {
            Assert.Equal(ask.Remaining - 1, saved.Set<NpcMarketOrder>().Find(ask.Id)!.Remaining);
            Assert.Single(saved.Set<OwnedItem>(), item => item.OwnerId == Pilot && item.TypeId == type);
            Assert.Empty(saved.Set<SavedMarketUnlock>());
        }
        var reopened = new PilotMarketStore(f.Database, f.Clock, new MarketChangeSignal());
        Assert.Equal(bought.BalanceCents, reopened.Balance(Pilot));
        Assert.Single(reopened.Transactions(Pilot));
        Assert.Equal(ask.Remaining - 1, reopened.Orders(Pilot, type).Single(order => order.Id == ask.Id).Remaining);
        using (WorldDbContext saved = new(f.Database.Path))
        {
            var clock = saved.Set<Eden.Data.World.Capsuleers.SavedCapsuleerClock>().Single();
            clock.ElapsedTicks += RegionalMarketRules.RecoveryIntervalTicks / 2;
            saved.SaveChanges();
        }
        Assert.Equal(ask.Remaining - 1, reopened.Orders(Pilot, type).Single(order => order.Id == ask.Id).Remaining);
        using (WorldDbContext saved = new(f.Database.Path))
        {
            saved.Set<Eden.Data.World.Capsuleers.SavedCapsuleerClock>().Single().ElapsedTicks +=
                RegionalMarketRules.RecoveryIntervalTicks / 2;
            saved.SaveChanges();
        }
        Assert.Equal(ask.Remaining, reopened.Orders(Pilot, type).Single(order => order.Id == ask.Id).Remaining);
    }

    [Theory]
    [InlineData(1, StarterMarketType.Merlin, StarterMarketType.GatlingRailgun, StarterMarketType.SmallShieldBooster, 3)]
    [InlineData(2, StarterMarketType.Rifter, StarterMarketType.GatlingAutocannon, StarterMarketType.SmallArmorRepairer, 3)]
    [InlineData(4, StarterMarketType.Punisher, StarterMarketType.GatlingPulseLaser, StarterMarketType.SmallArmorRepairer, 4)]
    [InlineData(8, StarterMarketType.Incursus, StarterMarketType.GatlingRailgun, StarterMarketType.SmallArmorRepairer, 3)]
    [InlineData(1, StarterMarketType.Venture, StarterMarketType.Miner, StarterMarketType.SmallShieldBooster, 2)]
    public void StartingBudgetBuysAndFitsTheFirstShipWithOnlyItsRequiredAdditionalTraining(
        int race,
        StarterMarketType hull,
        StarterMarketType tool,
        StarterMarketType repair,
        int count
    )
    {
        using Fixture f = new();
        var market = f.Db.Services.GetRequiredService<IPilotMarket>();
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        var fitting = f.Db.Services.GetRequiredService<IPilotFitting>();
        Dictionary<int, int> shopping = new()
        {
            [(int)hull] = 1,
            [(int)tool] = count,
            [(int)repair] = 1,
            [(int)StarterMarketType.Afterburner] = 1,
            [(int)StarterMarketType.DamageControl] = 1,
        };
        int charge = hull switch
        {
            StarterMarketType.Rifter => (int)StarterMarketType.EmpSmall,
            StarterMarketType.Punisher => (int)StarterMarketType.StandardSmall,
            _ => (int)StarterMarketType.IronSmall,
        };
        if (hull != StarterMarketType.Venture)
        {
            shopping[charge] = hull == StarterMarketType.Punisher ? count : 1000;
        }
        using (WorldDbContext saved = new(f.Database.Path))
        {
            saved.Set<PilotSkill>().Where(skill => skill.PilotId == Pilot).ExecuteDelete();
            StartingSkills.Grant(saved, f.Catalog, Pilot, race);
            Queue<int> pending = new(shopping.Keys);
            HashSet<int> visited = [];
            while (pending.TryDequeue(out int type))
            {
                if (!visited.Add(type))
                {
                    continue;
                }
                foreach (var (skill, level) in f.Catalog.Definitions.RequiredSkills(type))
                {
                    pending.Enqueue(skill);
                    double rank = f.Catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant];
                    var trained = saved.Set<PilotSkill>().Find(Pilot, skill);
                    long points = TrainedSkill.PointsForLevel(rank, level);
                    if (trained is not null && trained.Points >= points)
                    {
                        continue;
                    }
                    shopping[skill] = 1;
                    trained ??= saved.Add(new PilotSkill { PilotId = Pilot, TypeId = skill }).Entity;
                    trained.Points = Math.Max(trained.Points, points);
                }
            }
            saved.SaveChanges();
        }
        var book = market.Orders(Pilot);
        var purchases = shopping
            .Select(pair => new MarketPurchase(
                pair.Key,
                book.Single(order => order.StationId == SchoolStation && !order.Bid && order.TypeId == pair.Key).PriceCents,
                pair.Value
            ))
            .ToArray();
        long cost = purchases.Sum(purchase => purchase.LimitPriceCents * purchase.Quantity);
        Assert.InRange(cost, 1, StartingCents * 3 / 4);
        var bought = market.Buy(Pilot, SchoolStation, purchases);
        Assert.Equal(StartingCents - cost, bought.BalanceCents);
        var packaged = inventory.List(Pilot, SchoolStation).Single(item => item.TypeId == (int)hull);
        long ship = Assert.Single(fleet.Assemble(Pilot, [packaged.Id], null).ShipIds);
        long previous = f.Db.Store.FindPilot(1000001)!.ShipId;
        fleet.Board(Pilot, ship, previous);
        for (int index = 0; index < count; index++)
        {
            var item = inventory.List(Pilot, SchoolStation).Single(item => item.TypeId == (int)tool);
            fitting.Fit(Pilot, [item.Id], SchoolStation, (int)InventoryFlag.AutoFit);
        }
        foreach (int module in new[] { (int)repair, (int)StarterMarketType.Afterburner, (int)StarterMarketType.DamageControl })
        {
            var item = inventory.List(Pilot, SchoolStation).Single(item => item.TypeId == module);
            fitting.Fit(Pilot, [item.Id], SchoolStation, (int)InventoryFlag.AutoFit);
        }
        foreach (var module in inventory.List(Pilot, ship).Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId)))
        {
            fitting.SetOnline(Pilot, ship, module.Id, true);
        }
        Assert.Equal(count + 3, inventory.List(Pilot, ship).Count(item => InventoryDefinitions.IsFittedSlot(item.FlagId)));
        Assert.All(
            inventory.List(Pilot, ship).Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId)),
            item => Assert.True(item.Online)
        );
    }

    private sealed class Fixture : IDisposable
    {
        internal TestClock Clock { get; } = new();
        internal TestWorld Db { get; }
        internal WorldDatabase Database { get; }
        internal CatalogDbContext Catalog { get; }

        internal Fixture(int station = SchoolStation)
        {
            Db = new(clock: Clock);
            Database = new(Db.Path);
            TestCatalog.AddSchool(Database.CatalogPath, 17, 1, 1, SchoolSystem, SchoolStation, 601);
            PassiveCatalogFixture.LoadStarterMarket(Database.CatalogPath);
            using (CatalogDbContext catalog = new(Database.CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[5,7,14,17]}}'),
                        ('client/systems','30000141','{{"securityStatus":1.0}}'),
                        ('client/systems','30000142','{{"securityStatus":1.0}}');
                    INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                        SELECT 60003999,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                    """
                );
            }
            using WorldDbContext world = new(Db.Path);
            world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            world.Ships.Single(ship => ship.Id == world.Pilots.Single(pilot => pilot.Id == Pilot).ActiveShipId).StationId = station;
            world.Add(new PilotWallet { PilotId = Pilot, Cents = StartingCents });
            world.SaveChanges();
            Catalog = new(Database.CatalogPath);
        }

        public void Dispose()
        {
            Catalog.Dispose();
            Db.Dispose();
        }
    }
}
