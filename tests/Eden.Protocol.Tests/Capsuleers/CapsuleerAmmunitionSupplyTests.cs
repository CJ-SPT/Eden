using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Market;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Industry;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerAmmunitionSupplyTests
{
    private const int Home = 30000141;
    private const int Depot = 60015157;
    private const int FactoryStation = 60003760;
    private const long Customer = 90000001;
    private const int Ore = 1228;
    private const int Tritanium = 34;
    private const int Pyerite = 35;

    [Fact]
    public async Task MinedInputsBecomePublicOrdersAndPaidCourierStockSurvivesRestart()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture);
        using (WorldDbContext save = new(fixture.Database.Path))
        {
            save.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldIndustryJob DROP COLUMN OutputCorporationId;
                ALTER TABLE WorldPersonalOrder DROP COLUMN CorporationId;
                PRAGMA user_version=188;
                """
            );
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long producer;
        long courier;
        long combat;
        long jobId;
        int seller;
        int buyer;
        long listingId;
        int shipment = Rules.ShipmentQuantity;
        long purchaseCents;
        long sellerBefore;
        FreightManifest paidShipment;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                producer = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                var haulers = agents.Where(agent => agent.Career == CapsuleerCareer.Hauling).ToArray();
                courier = haulers[0].PilotId;
                combat = haulers[1].PilotId;
                var minerShip = setup.Pilots.Find(producer)!.ActiveShip;
                minerShip.StationId = FactoryStation;
                seller = Charter(setup, catalog, producer, "Factory Fleet", "FACT", fixture.Clock.Now);
                buyer = Charter(setup, catalog, courier, "Patrol Fleet", "PTFL", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, setup.Pilots.Find(combat)!, buyer, fixture.Clock.Now);
                var patrolShip = setup.Pilots.Find(combat)!.ActiveShip;
                foreach (var item in setup.Filter<OwnedItem>(item => item.OwnerId == combat).ToArray())
                {
                    setup.Remove(item);
                }
                TrafficFitting.Apply(
                    setup,
                    catalog,
                    combat,
                    patrolShip.Id,
                    TrafficSnapshotTests.Profile() with
                    {
                        HullTypeId = 603,
                        Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(Rules.ChargeType, (int)InventoryFlag.HighSlot0, 1)],
                    },
                    fixture.Clock.Now
                );
                setup.Set<SavedCapsuleerJob>().Find(combat)!.DueTicks = TimeSpan.FromHours(1).Ticks;
                foreach (
                    var agent in agents.Where(agent => agent.PilotId != producer && agent.PilotId != courier && agent.PilotId != combat)
                )
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                AddStock(setup, producer, FactoryStation, Ore, 300);
                AddStock(setup, Customer, FactoryStation, Rules.BlueprintType, 1);
                setup.Set<SavedCorporation>().Find(buyer)!.BalanceCents = 20_000 * MarketDefinitions.CentsPerIsk;
                setup.Ships.Find(setup.Pilots.Find(Customer)!.ActiveShipId)!.StationId = FactoryStation;
                setup.Add(new PilotWallet { PilotId = Customer, Cents = 1_000_000 });
                setup.Commit();
            }

            var privateBook = host.GetRequiredService<IPersonalMarket>();
            long blueprint = live.Economy.Read(world =>
                world
                    .Filter<OwnedItem>(item => item.OwnerId == Customer)
                    .Single(item => item.TypeId == Rules.BlueprintType && item.LocationId == FactoryStation)
                    .Id
            );
            privateBook.PlaceSell(Customer, [new(blueprint, FactoryStation, Rules.BlueprintType, MarketDefinitions.CentsPerIsk, 1)], 1);
            var ammunition = host.GetRequiredService<CapsuleerIndustry>();
            var ships = host.GetRequiredService<SqliteWorld>();
            using (var dock = live.Economy.Begin(TransactionParticipants.None))
            {
                var miner = ships.FindPilot(dock, -1, producer)!;
                ammunition.Produce(dock, catalog, miner, fixture.Clock.Now);
                Assert.Equal(0, Stock(dock, producer, FactoryStation, Ore));
                Assert.True(Stock(dock, producer, FactoryStation, Tritanium) >= 135);
                Assert.True(Stock(dock, producer, FactoryStation, Pyerite) >= 105);
                Assert.True(ammunition.Produce(dock, catalog, miner, fixture.Clock.Now));
                Assert.Equal(1, Stock(dock, producer, FactoryStation, Rules.BlueprintType));
                ammunition.Produce(dock, catalog, miner, fixture.Clock.Now);
                var job = Assert.Single(dock.Set<IndustryJob>());
                Assert.Equal(seller, job.OutputCorporationId);
                Assert.Equal(IndustryJobStatus.Installed, job.Status);
                jobId = job.Id;
                Assert.Empty(dock.Set<SavedPersonalOrder>().Where(order => order.CorporationId == seller));
                dock.Commit();
            }
            fixture.Clock.Seconds += 1500;
            using (var dock = live.Economy.Begin(TransactionParticipants.None))
            {
                var miner = ships.FindPilot(dock, -1, producer)!;
                Assert.True(ammunition.Produce(dock, catalog, miner, fixture.Clock.Now));
                Assert.False(ammunition.Produce(dock, catalog, miner, fixture.Clock.Now));
                Assert.Equal(IndustryJobStatus.Delivered, dock.Set<IndustryJob>().Find(jobId)!.Status);
                var listing = Assert.Single(dock.Filter<SavedPersonalOrder>(order => order.CorporationId == seller));
                Assert.Equal(500, listing.Remaining);
                listingId = listing.Id;
                purchaseCents = listing.PriceCents * shipment;
                sellerBefore = dock.Set<SavedCorporation>().Find(seller)!.BalanceCents;
                dock.Commit();
            }
            var market = host.GetRequiredService<IPilotMarket>();
            var publicAsk = Assert.Single(market.Orders(Customer, Rules.ChargeType), order => order.Id == listingId);
            Assert.Equal(seller, publicAsk.OwnerId);
            var corporationAsk = Assert.Single(privateBook.CorporationOrders(producer));
            Assert.Equal(listingId, corporationAsk.Id);
            var orderFields = Assert.IsType<object?[]>(MarketWire.Row(corporationAsk).Arguments[1]);
            Assert.Equal((long)seller, orderFields[2]);
            Assert.Equal(true, orderFields[16]);
            Assert.Empty(privateBook.CorporationOrders(Customer));
            Assert.Empty(privateBook.PersonalOrders(producer));
            // Use the native personal buyer entry point with the same public book.
            var playerPurchase = market.Buy(Customer, FactoryStation, [new(Rules.ChargeType, publicAsk.PriceCents, 1)]);
            Assert.Equal(seller, Assert.Single(playerPurchase.Trades).CounterpartyId);

            using var human = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            using (var review = live.Economy.Begin(TransactionParticipants.None))
            {
                var pilot = ships.FindPilot(review, -1, courier)!;
                var plan = CapsuleerIndustry.PlanCollection(review, catalog, live, pilot, new HashSet<int>(), fixture.Clock.Now);
                Assert.NotNull(plan);
                Assert.Equal(listingId, plan.Run.OrderId);
            }
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (
                int turn = 0;
                turn < 1600 && live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.Freight?.Sequence != listingId;
                turn++
            )
            {
                fixture.Clock.Seconds++;
                worker.Tick();
            }
            var loaded = scheduler.Hauler(courier)!;
            Assert.True(
                loaded.Work.CorporationSupply?.OrderId == listingId,
                $"Courier {loaded.Pilot.ShipTypeId} at {loaded.Pilot.SystemId}/{loaded.Pilot.StationId}, "
                    + $"{loaded.Work.Activity}, tick {loaded.Work.ActiveTicks}, target {loaded.Work.TargetId}, "
                    + $"route {loaded.Work.CorporationSupply?.OrderId}, failed {scheduler.Read().LastFailure}, {loaded.Work.WaitingReason}"
            );
            paidShipment = loaded.Work.Freight!;
            var cargo = Assert.Single(paidShipment.Stacks);
            Assert.Equal(shipment, cargo.Quantity);
            Assert.Equal(Depot, paidShipment.Destination);
            live.Economy.Read(world =>
            {
                Assert.Equal(499 - shipment, world.Set<SavedPersonalOrder>().Find(listingId)!.Remaining);
                Assert.Empty(CapsuleerIndustry.DepotStock(world, buyer, Depot));
                Assert.Equal(courier, world.Set<OwnedItem>().Find(cargo.ItemId)!.OwnerId);
                var sales = world
                    .Filter<SavedWalletLedger>(row => row.OwnerId == seller)
                    .Where(row => row.EntryType == WalletEntryType.MarketTransaction && row.ReferenceId == listingId)
                    .ToArray();
                var publicSale = Assert.Single(sales, row => row.CounterpartyId == Customer);
                var corporationSale = Assert.Single(sales, row => row.CounterpartyId == buyer);
                var fill = MarketRules.Fill(shipment, shipment, publicAsk.PriceCents, MarketRules.SalesTax(0));
                Assert.Equal(purchaseCents - fill.TaxCents, corporationSale.AmountCents);
                Assert.Equal(
                    sellerBefore + publicSale.AmountCents + corporationSale.AmountCents,
                    world.Set<SavedCorporation>().Find(seller)!.BalanceCents
                );
                Assert.Contains(
                    world.Filter<SavedWalletLedger>(row => row.OwnerId == buyer),
                    row => row.EntryType == WalletEntryType.MarketTransaction && row.AmountCents == -purchaseCents
                );
                return true;
            });
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using var occupied = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        var resumed = reopened.GetRequiredService<CapsuleerScheduler>();
        resumed.PopulationPrepared(Home);
        var maintenance = reopened.GetRequiredService<CapsuleerMaintenance>();
        for (int turn = 0; turn < 1600 && restored.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.CorporationSupply is not null; turn++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        Assert.Null(resumed.Hauler(courier)!.Work.CorporationSupply);
        using var received = restored.Economy.Begin(TransactionParticipants.None);
        Assert.Equal(shipment, CapsuleerIndustry.DepotStock(received, buyer, Depot).Sum(item => item.Quantity));
        using CatalogDbContext reopenedCatalog = new(fixture.Database.CatalogPath);
        var deliveryPilot = reopened.GetRequiredService<SqliteWorld>().FindPilot(received, -1, courier)!;
        CapsuleerIndustry.Deliver(received, reopenedCatalog, deliveryPilot, new(buyer, listingId), paidShipment);
        Assert.Equal(shipment, CapsuleerIndustry.DepotStock(received, buyer, Depot).Sum(item => item.Quantity));
        long combatShip = received.Pilots.Find(combat)!.ActiveShipId;
        var gun = Assert.Single(
            received
                .Filter<OwnedItem>(item => item.OwnerId == combat)
                .Where(item => item.LocationId == combatShip && item.FlagId == (int)InventoryFlag.HighSlot0)
        );
        received.Remove(Assert.Single(received.Filter<OwnedItem>(item => item.LocationId == gun.Id)));
        var resupply = reopened.GetRequiredService<CapsuleerResupply>();
        var supplied = resupply.Dock(fixture.Database, received, reopenedCatalog, combat, null, fixture.Clock.Now);
        Assert.Equal(0, supplied.SpentCents);
        var magazine = Assert.Single(
            received
                .Filter<OwnedItem>(item => item.OwnerId == combat)
                .Where(item => item.TypeId == Rules.ChargeType && item.LocationId == gun.Id)
        );
        Assert.True(magazine.Quantity > 0);
        Assert.Equal(
            shipment,
            received
                .Filter<OwnedItem>(item => item.OwnerId == combat)
                .Where(item => item.TypeId == Rules.ChargeType)
                .Sum(item => item.Quantity) + CapsuleerIndustry.DepotStock(received, buyer, Depot).Sum(item => item.Quantity)
        );
        Assert.Single(
            received
                .Filter<SavedWalletLedger>(row => row.OwnerId == buyer)
                .Where(row => row.EntryType == WalletEntryType.MarketTransaction && row.AmountCents == -purchaseCents)
        );
        received.Commit();
    }

    [Theory]
    [InlineData(785, false)] // 150mm Railgun I, with a blueprint bought from the local market.
    [InlineData(950, true)] // Merlin, with one licensed copy run.
    public async Task LocalDemandFundsCatalogManufacturingAndDeliveryAfterRestart(int blueprintType, bool copy)
    {
        const int requested = 2;
        const long unitBid = 100_000_000;
        const long corporateCapital = 50_000_000;
        const long inputPrice = 100;
        const long pilotBalance = 1_000_000;
        var recipe = StarterProductionTests.ReadAudit().Recipes.Single(recipe => recipe.BlueprintId == blueprintType);
        int product = Assert.Single(recipe.Products).TypeID;
        int runs = copy ? 1 : requested;

        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long producer;
        int corporation;
        long jobId;
        long bidId;
        long inputCost = (recipe.Materials.Sum(material => material.Quantity * runs) + (copy ? 0 : 1)) * inputPrice;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingGrants = false;
                producer = setup.Set<SavedCapsuleerAgent>().Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                setup.Pilots.Find(producer)!.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(Customer)!.ActiveShip.StationId = FactoryStation;
                corporation = Charter(setup, catalog, producer, "Demand Works", "DMND", fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = corporateCapital;
                setup.Set<PilotWallet>().Find(producer)!.Cents = pilotBalance;
                setup.Add(new PilotWallet { PilotId = Customer, Cents = 3 * unitBid });
                foreach (var material in recipe.Materials)
                {
                    AddStock(setup, Customer, FactoryStation, material.TypeID, material.Quantity * runs);
                }
                AddStock(setup, copy ? producer : Customer, FactoryStation, blueprintType, 1);
                if (copy)
                {
                    var license = setup.Filter<OwnedItem>(item => item.OwnerId == producer).Single(item => item.TypeId == blueprintType);
                    license.BlueprintRuns = 1;
                    license.Singleton = true;
                }
                setup.Commit();
            }

            var book = host.GetRequiredService<IPersonalMarket>();
            book.PlaceBuy(Customer, new(FactoryStation, product, unitBid, requested, 7));
            bidId = Assert.Single(book.PersonalOrders(Customer)).Id;
            var industry = host.GetRequiredService<CapsuleerIndustry>();
            var ships = host.GetRequiredService<SqliteWorld>();
            using (var unavailable = live.Economy.Begin(TransactionParticipants.None))
            {
                Assert.False(industry.Produce(unavailable, catalog, ships.FindPilot(unavailable, -1, producer)!, fixture.Clock.Now));
                Assert.Empty(unavailable.Set<IndustryJob>());
                Assert.Equal(corporateCapital, unavailable.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                unavailable.Commit();
            }
            MarketSale[] inputs = live.Economy.Read(world =>
                world
                    .Filter<OwnedItem>(item => item.OwnerId == Customer)
                    .Where(item => item.LocationId == FactoryStation)
                    .AsEnumerable()
                    .Where(item => item.TypeId == blueprintType || recipe.Materials.Any(m => m.TypeID == item.TypeId))
                    .Select(item => new MarketSale(item.Id, FactoryStation, item.TypeId, inputPrice, item.Quantity))
                    .ToArray()
            );
            book.PlaceSell(Customer, inputs, 7);
            using (var unfunded = live.Economy.Begin(TransactionParticipants.None))
            {
                var treasury = unfunded.Set<SavedCorporation>().Find(corporation)!;
                var wallet = unfunded.Set<PilotWallet>().Find(producer)!;
                treasury.BalanceCents = 0;
                wallet.Cents = 0;
                Assert.False(industry.Produce(unfunded, catalog, ships.FindPilot(unfunded, -1, producer)!, fixture.Clock.Now));
                Assert.Empty(unfunded.Set<IndustryJob>());
                var asks = unfunded.Filter<SavedPersonalOrder>(order => order.PilotId == Customer).Where(order => !order.Bid).ToArray();
                Assert.All(
                    inputs,
                    input => Assert.Equal(input.Quantity, Assert.Single(asks, order => order.TypeId == input.TypeId).Remaining)
                );
            }
            using (var dock = live.Economy.Begin(TransactionParticipants.None))
            {
                var pilot = ships.FindPilot(dock, -1, producer)!;
                Assert.True(industry.Produce(dock, catalog, pilot, fixture.Clock.Now));
                Assert.False(industry.Produce(dock, catalog, pilot, fixture.Clock.Now));
                var job = Assert.Single(dock.Set<IndustryJob>());
                jobId = job.Id;
                Assert.Equal(blueprintType, job.BlueprintTypeId);
                Assert.Equal(copy, job.BlueprintCopy);
                Assert.Equal(product, job.ProductTypeId);
                Assert.Equal(runs, job.Runs);
                Assert.Equal(runs, job.OutputQuantity);
                Assert.Equal(corporation, job.OutputCorporationId);
                Assert.Equal(IndustryJobStatus.Installed, job.Status);
                Assert.Equal(corporateCapital - inputCost - job.CostCents, dock.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                Assert.Equal(pilotBalance, dock.Set<PilotWallet>().Find(producer)!.Cents);
                Assert.All(recipe.Materials, material => Assert.Equal(0, Stock(dock, producer, FactoryStation, material.TypeID)));
                Assert.Equal(0, Stock(dock, Customer, FactoryStation, product));
                dock.Commit();
            }
            await live.FlushAsync();
        }

        fixture.Clock.Seconds += recipe.Time * runs;
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using CatalogDbContext reopenedCatalog = new(fixture.Database.CatalogPath);
        using var delivery = restored.Economy.Begin(TransactionParticipants.None);
        var operatorPilot = reopened.GetRequiredService<SqliteWorld>().FindPilot(delivery, -1, producer)!;
        Assert.True(reopened.GetRequiredService<CapsuleerIndustry>().Produce(delivery, reopenedCatalog, operatorPilot, fixture.Clock.Now));
        Assert.Equal(IndustryJobStatus.Delivered, delivery.Set<IndustryJob>().Find(jobId)!.Status);
        Assert.Equal(runs, Stock(delivery, Customer, FactoryStation, product));
        Assert.Equal(requested - runs, delivery.Set<SavedPersonalOrder>().Find(bidId)!.Remaining);
        Assert.Equal(copy ? 0 : 1, Stock(delivery, producer, FactoryStation, blueprintType));
        var fill = MarketRules.Fill(runs, runs, unitBid, MarketRules.SalesTax(0));
        Assert.Contains(
            delivery.Filter<SavedWalletLedger>(row => row.OwnerId == corporation),
            row => row.EntryType == WalletEntryType.MarketTransaction && row.AmountCents == fill.GrossCents - fill.TaxCents
        );
        delivery.Commit();
    }

    [Fact]
    public async Task HaulsOnlyTheRemoteShortfallAndResumesDeliveryAfterRestart()
    {
        const int blueprint = 785;
        const int supplier = 90000002;
        const long materialsPrice = 100;
        const long factoryFunds = 50_000_000;
        const long unitBid = 100_000_000;
        const int localUnits = 19;
        var recipe = StarterProductionTests.ReadAudit().Recipes.Single(entry => entry.BlueprintId == blueprint);
        int product = Assert.Single(recipe.Products).TypeID;
        var remote = recipe.Materials.Single(material => material.TypeID != Tritanium && material.TypeID != Pyerite);
        int remoteUnits = remote.Quantity - localUnits;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long manufacturer;
        long courier;
        int corporation;
        long materialOrder;
        CorporationSupplyRun shipment;
        FreightManifest cargo;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                manufacturer = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                courier = agents.First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                setup.Pilots.Find(manufacturer)!.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(courier)!.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(Customer)!.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(supplier)!.ActiveShip.StationId = Depot;
                setup.Add(new PilotWallet { PilotId = Customer, Cents = 2 * unitBid });
                var supplierWallet = setup.Set<PilotWallet>().Find(supplier) ?? setup.Add(new PilotWallet { PilotId = supplier }).Entity;
                supplierWallet.Cents = PersonalMarketRules.MinimumBrokerFeeCents;
                corporation = Charter(setup, catalog, manufacturer, "Freight Works", "FRGT", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, setup.Pilots.Find(courier)!, corporation, fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = factoryFunds;
                setup.Set<SavedCapsuleerJob>().Find(manufacturer)!.DueTicks = TimeSpan.FromHours(2).Ticks;
                foreach (var agent in agents.Where(agent => agent.PilotId != manufacturer && agent.PilotId != courier))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                foreach (var material in recipe.Materials.Where(material => material.TypeID != remote.TypeID))
                {
                    AddStock(setup, manufacturer, FactoryStation, material.TypeID, material.Quantity);
                }
                AddStock(setup, manufacturer, FactoryStation, blueprint, 1);
                AddStock(setup, Customer, FactoryStation, remote.TypeID, localUnits);
                AddStock(setup, supplier, Depot, remote.TypeID, remote.Quantity);
                setup.Commit();
            }

            var market = host.GetRequiredService<IPersonalMarket>();
            market.PlaceBuy(Customer, new(FactoryStation, product, unitBid, 1, 7));
            foreach (var offer in new[] { (Owner: Customer, Station: FactoryStation), (Owner: (long)supplier, Station: Depot) })
            {
                var ask = live.Economy.Read(world =>
                    world.Filter<OwnedItem>(item => item.OwnerId == offer.Owner).Single(item => item.TypeId == remote.TypeID)
                );
                market.PlaceSell(offer.Owner, [new(ask.Id, offer.Station, ask.TypeId, materialsPrice, ask.Quantity)], 7);
            }
            materialOrder = Assert.Single(market.PersonalOrders(supplier)).Id;
            var industry = host.GetRequiredService<CapsuleerIndustry>();
            var ships = host.GetRequiredService<SqliteWorld>();
            using (var factory = live.Economy.Begin(TransactionParticipants.None))
            {
                var miner = ships.FindPilot(factory, -1, manufacturer)!;
                Assert.False(industry.Produce(factory, catalog, miner, fixture.Clock.Now, out var request));
                Assert.NotNull(request);
                Assert.Equal(remoteUnits, Assert.Single(request.Materials).HangarTarget);
                Assert.Empty(factory.Set<IndustryJob>());
                new MinerCheckpoint(
                    manufacturer,
                    new(MinerActivity.Docked, 0, 0, 0, 0, 0, 0, new NavigationState().Capture(), miner.ShipId)
                    {
                        FactorySupply = request,
                        DueTicks = TimeSpan.FromHours(2).Ticks,
                    }
                ).Apply(factory);
                factory.Commit();
            }
            using (var review = live.Economy.Begin(TransactionParticipants.None))
            {
                var hauler = ships.FindPilot(review, -1, courier)!;
                var plan = industry.PlanFactoryCollection(review, catalog, live, hauler, new HashSet<int>(), fixture.Clock.Now);
                Assert.NotNull(plan);
                Assert.Equal(Depot, plan.Route.DestinationStationId);
                Assert.Equal(2, plan.Route.Systems.Length);
                Assert.Null(industry.PlanFactoryCollection(review, catalog, live, hauler, new HashSet<int> { Home }, fixture.Clock.Now));
            }
            long localRestock;
            using (var restock = live.Economy.Begin(TransactionParticipants.None))
            {
                AddStock(restock, Customer, FactoryStation, remote.TypeID, remoteUnits);
                localRestock = restock.Filter<OwnedItem>(item => item.OwnerId == Customer).Single(item => item.TypeId == remote.TypeID).Id;
                restock.Commit();
            }
            market.PlaceSell(Customer, [new(localRestock, FactoryStation, remote.TypeID, materialsPrice, remoteUnits)], 7);
            var localReplacement = Assert.Single(
                market.PersonalOrders(Customer),
                order => order.TypeId == remote.TypeID && order.Remaining == remoteUnits
            );
            using (var nowLocal = live.Economy.Begin(TransactionParticipants.None))
            {
                var hauler = ships.FindPilot(nowLocal, -1, courier)!;
                Assert.Null(industry.PlanFactoryCollection(nowLocal, catalog, live, hauler, new HashSet<int>(), fixture.Clock.Now));
            }
            market.Cancel(Customer, localReplacement.Id, localReplacement.RegionId);

            using var occupied = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            for (
                int turn = 0;
                turn < 1600
                    && live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work
                        is not { CorporationSupply.FactorySupply: not null, Freight: not null };
                turn++
            )
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
            }
            var progress = scheduler.Hauler(courier)!;
            Assert.True(
                progress.Work.CorporationSupply?.FactorySupply is not null,
                scheduler.Read().LastFailure ?? progress.Work.WaitingReason
            );
            shipment = progress.Work.CorporationSupply!;
            cargo = Assert.IsType<FreightManifest>(progress.Work.Freight);
            Assert.Equal(FactoryStation, cargo.Destination);
            Assert.Equal(materialOrder, cargo.Sequence);
            Assert.Equal(remoteUnits, Assert.Single(cargo.Stacks).Quantity);
            live.Economy.Read(world =>
            {
                Assert.Equal(0, Stock(world, manufacturer, FactoryStation, remote.TypeID));
                Assert.Equal(localUnits, world.Set<SavedPersonalOrder>().Find(materialOrder)!.Remaining);
                Assert.Equal(factoryFunds - remoteUnits * materialsPrice, world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                Assert.Equal(progress.Pilot.ShipId, world.Set<OwnedItem>().Find(cargo.Stacks[0].ItemId)!.LocationId);
                return true;
            });
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using var human = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        var resumed = reopened.GetRequiredService<CapsuleerScheduler>();
        resumed.PopulationPrepared(Home);
        var adapter = reopened.GetRequiredService<CapsuleerMaintenance>();
        for (int turn = 0; turn < 1600 && restored.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.CorporationSupply is not null; turn++)
        {
            fixture.Clock.Seconds++;
            adapter.Tick();
        }
        Assert.Null(resumed.Hauler(courier)!.Work.CorporationSupply);
        using CatalogDbContext reopenedCatalog = new(fixture.Database.CatalogPath);
        using var delivery = restored.Economy.Begin(TransactionParticipants.None);
        var shipStore = reopened.GetRequiredService<SqliteWorld>();
        Assert.Equal(remoteUnits, Stock(delivery, manufacturer, FactoryStation, remote.TypeID));
        CapsuleerIndustry.Deliver(delivery, reopenedCatalog, shipStore.FindPilot(delivery, -1, courier)!, shipment, cargo);
        Assert.Equal(remoteUnits, Stock(delivery, manufacturer, FactoryStation, remote.TypeID));
        Assert.Null(delivery.Set<OwnedItem>().Find(cargo.Stacks[0].ItemId));
        Assert.True(
            reopened
                .GetRequiredService<CapsuleerIndustry>()
                .Produce(delivery, reopenedCatalog, shipStore.FindPilot(delivery, -1, manufacturer)!, fixture.Clock.Now, out var shortage)
        );
        Assert.Null(shortage);
        Assert.Equal(product, Assert.Single(delivery.Set<IndustryJob>()).ProductTypeId);
        Assert.Equal(0, Stock(delivery, manufacturer, FactoryStation, remote.TypeID));
        delivery.Commit();
    }

    private static void UseLocalOrders(CapsuleerHaulerTests.Fixture fixture)
    {
        var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
        var startup = configuration.Read();
        var draft = startup.Active.Draft;
        configuration.Save(
            startup.Generation,
            draft with
            {
                Settings = draft.Settings with
                {
                    Economy = draft.Settings.Economy with { ProgressiveMarkets = false, StandardMarket = false },
                },
            },
            "Corporate industry fixture"
        );
    }

    private static long Stock(WorldAction world, long pilot, int station, int type) =>
        world
            .Filter<OwnedItem>(item => item.OwnerId == pilot)
            .Where(item => item.LocationId == station && item.TypeId == type)
            .Sum(item => item.Quantity);

    private static void AddStock(EconomyTransaction world, long owner, int station, int type, int amount) =>
        world.Add(
            new OwnedItem
            {
                Id = world.NextItemId(),
                OwnerId = owner,
                LocationId = station,
                TypeId = type,
                Quantity = amount,
                FlagId = (int)InventoryFlag.Hangar,
            }
        );

    private static int Charter(EconomyTransaction world, CatalogDbContext catalog, long ceo, string name, string ticker, long now)
    {
        world.Add(
            new PilotSkill
            {
                PilotId = ceo,
                TypeId = CorporationRules.ManagementSkill,
                Points = 250,
            }
        );
        world.Set<PilotWallet>().Find(ceo)!.Cents += CorporationRules.CreationFeeCents;
        int id = PlayerCorporationStore.Create(world, catalog, ceo, PlayerCorporationTests.Details(name, ticker), now).Corporation.Id;
        var saved = world.Set<SavedCorporation>().Find(id)!;
        saved.CapsuleerFoundingSystemId = saved.SystemId;
        return id;
    }

    private static void PrepareCatalog(CapsuleerHaulerTests.Fixture fixture)
    {
        PassiveCatalogFixture.LoadFrigateHulls(fixture.Database.CatalogPath);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory VALUES (9,'Blueprint'),(4,'Material');
            INSERT OR IGNORE INTO ItemGroup VALUES (167,'Hybrid Charge Blueprint',9),(18,'Mineral',4);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (1130,'Iron Charge S Blueprint',167),
                (34,'Tritanium',18),(35,'Pyerite',18),(3380,'Industry',257);
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (440,'manufacturingTimeBonus');
            INSERT OR IGNORE INTO TypeAttribute VALUES (3380,275,1),(3380,440,-4);
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[5,7,14,17]}}'),
                ('client/types','34','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/types','35','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/types','215','{{"published":1,"marketGroupID":1,"volume":0.0025}}'),
                ('client/types','1130','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/types','1228','{{"published":1,"marketGroupID":1,"volume":0.15,"portionSize":100}}'),
                ('client/typematerials','1228','{{"materials":[' ||
                    '{{"materialTypeID":34,"quantity":150}},{{"materialTypeID":35,"quantity":110}}]}}'),
                ('sde/npcStations','60003760','{{"operationID":14,"reprocessingEfficiency":0.5,"reprocessingStationsTake":0.05}}'),
                ('client/blueprints','"1130"','{{"activities":{{"manufacturing":{{"skills":[{{"typeID":3380,"level":1}}],' ||
                    '"materials":[{{"typeID":34,"quantity":27}},{{"typeID":35,"quantity":21}}],' ||
                    '"products":[{{"typeID":215,"quantity":100}}],"time":300}}}},"blueprintTypeID":1130,' ||
                    '"maxProductionLimit":600}}');
            """
        );
    }
}
