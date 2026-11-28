using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Market;
using Eden.Gameplay.Structures;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Fact]
    public void BotBuysMoonFeedForAnExistingReactorWithoutAMiningField()
    {
        const int feedUnits = 100;
        const long offerPrice = MarketDefinitions.CentsPerIsk;
        const long treasury = 20_000_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        PassiveCatalogFixture.LoadStructures(fixture.Database.CatalogPath);
        PrepareRefineryCatalog(fixture);
        PrepareMoonChainCatalog(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var live = host.GetRequiredService<LiveWorld>();
        var industry = host.GetRequiredService<CapsuleerIndustry>();
        var ships = host.GetRequiredService<SqliteWorld>();
        long miner;
        int corporation;
        MarketSale offer;
        using (var setup = live.Economy.Begin(TransactionParticipants.None))
        {
            setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
            miner = setup.Set<SavedCapsuleerAgent>().Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
            foreach (long pilot in new[] { miner, Customer })
            {
                var ship = setup.Pilots.Find(pilot)!.ActiveShip;
                ship.StationId = Depot;
                if (setup.Positions.Find(ship.Id) is { } position)
                {
                    setup.Remove(position);
                }
            }
            corporation = Charter(setup, catalog, miner, "Reactor Supply", "REACT", fixture.Clock.Now);
            setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = treasury;
            var wallet = setup.Set<PilotWallet>().Find(Customer) ?? setup.Add(new PilotWallet { PilotId = Customer }).Entity;
            wallet.Cents = treasury;
            setup.Add(
                new SavedStructure
                {
                    Id = ChainRefinery,
                    TypeId = CapsuleerIndustry.AthanorType,
                    CorporationId = corporation,
                    SystemId = Home,
                    Name = "Reactor Supply Athanor",
                    HasCore = true,
                    State = StructureState.ShieldVulnerable,
                    MoonId = ChainMoon,
                }
            );
            long module = setup.NextItemId();
            setup.Add(
                new OwnedItem
                {
                    Id = module,
                    OwnerId = corporation,
                    LocationId = ChainRefinery,
                    TypeId = ChainReactor,
                    Quantity = 1,
                    FlagId = StructureRules.FirstServiceSlot,
                    Singleton = true,
                    Online = true,
                }
            );
            setup.Add(
                new SavedStructureService
                {
                    ItemId = module,
                    StructureId = ChainRefinery,
                    Slot = StructureRules.FirstServiceSlot,
                    Online = true,
                    NextFuelAt = fixture.Clock.Now + StructureRules.FuelInterval,
                }
            );
            AddStock(setup, miner, Depot, ChainCrystalliteFormula, 1);
            AddStock(setup, miner, Depot, ChainCadmium, feedUnits);
            AddStock(setup, miner, Depot, ChainFuelBlock, 1);
            AddStock(setup, Customer, Depot, ChainCobalt, feedUnits);
            var feed = setup.Filter<OwnedItem>(item => item.OwnerId == Customer).Single(item => item.TypeId == ChainCobalt);
            offer = new(feed.Id, Depot, ChainCobalt, offerPrice, feedUnits);
            Assert.Empty(setup.Set<SavedMoonExtraction>());
            setup.Commit();
        }

        var supply = new FactoryMoonSupply(ChainRefinery, miner, Depot, [new(ChainCrystallite, 1)]) { UseExistingServices = true };
        using (var shortage = live.Economy.Begin(TransactionParticipants.None))
        {
            var pilot = ships.FindPilot(shortage, -1, miner)!;
            Assert.False(industry.PrepareMoonSupply(shortage, catalog, pilot, supply, fixture.Clock.Now, out var purchases, out _));
            var need = Assert.Single(Assert.IsType<FactorySupplyRequest>(purchases).Materials);
            Assert.Equal((ChainCobalt, feedUnits), (need.TypeId, need.PlannedUnits));
            Assert.Equal(treasury, shortage.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
            shortage.Commit();
        }

        host.GetRequiredService<IPersonalMarket>().PlaceSell(Customer, [offer], 7);
        using (var purchase = live.Economy.Begin(TransactionParticipants.None))
        {
            var pilot = ships.FindPilot(purchase, -1, miner)!;
            Assert.True(industry.PrepareMoonSupply(purchase, catalog, pilot, supply, fixture.Clock.Now, out var pending, out _));
            Assert.Null(pending);
            Assert.Equal(feedUnits, Stock(purchase, miner, Depot, ChainCobalt));
            Assert.Equal(treasury - offerPrice * feedUnits, purchase.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
            purchase.Commit();
        }
    }
}
