using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Agents;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Data.World.Planetary;
using Eden.Data.World.Skills;
using Eden.Data.World.Structures;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Planetary;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Market;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Industry;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Planetary;
using Eden.World.Reprocessing;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Replacement = Eden.Gameplay.Capsuleers.CapsuleerReplacementRules;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HaulsOnlyTheRemoteShortfallAndResumesDeliveryAfterRestart(bool multipleMaterials, bool limitedHold)
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
        var remoteNeeds = new Dictionary<int, int> { [remote.TypeID] = remoteUnits };
        if (multipleMaterials)
        {
            remoteNeeds.Add(Tritanium, recipe.Materials.Single(material => material.TypeID == Tritanium).Quantity);
        }
        var expected = new Dictionary<int, int>(remoteNeeds);
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long manufacturer;
        long courier;
        int corporation;
        IReadOnlyList<MarketOrder> supplierOrders;
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
                var haulerShip = setup.Pilots.Find(courier)!.ActiveShip;
                haulerShip.StationId = FactoryStation;
                setup.Pilots.Find(Customer)!.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(supplier)!.ActiveShip.StationId = Depot;
                setup.Add(new PilotWallet { PilotId = Customer, Cents = 2 * unitBid });
                var supplierWallet = setup.Set<PilotWallet>().Find(supplier) ?? setup.Add(new PilotWallet { PilotId = supplier }).Entity;
                supplierWallet.Cents = PersonalMarketRules.MinimumBrokerFeeCents * (remoteNeeds.Count + 1);
                corporation = Charter(setup, catalog, manufacturer, "Freight Works", "FRGT", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, setup.Pilots.Find(courier)!, corporation, fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = factoryFunds;
                setup.Set<SavedCapsuleerJob>().Find(manufacturer)!.DueTicks = TimeSpan.FromHours(2).Ticks;
                foreach (var agent in agents.Where(agent => agent.PilotId != manufacturer && agent.PilotId != courier))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                foreach (var material in recipe.Materials.Where(material => !remoteNeeds.ContainsKey(material.TypeID)))
                {
                    AddStock(setup, manufacturer, FactoryStation, material.TypeID, material.Quantity);
                }
                AddStock(setup, manufacturer, FactoryStation, blueprint, 1);
                AddStock(setup, Customer, FactoryStation, remote.TypeID, localUnits);
                // Two fills for the same material still produce one cargo stack.
                AddStock(setup, supplier, Depot, remote.TypeID, localUnits);
                AddStock(setup, supplier, Depot, remote.TypeID, remote.Quantity - localUnits);
                if (multipleMaterials)
                {
                    AddStock(setup, supplier, Depot, Tritanium, remoteNeeds[Tritanium]);
                }
                if (limitedHold)
                {
                    int flag = (int)InventoryFlag.Cargo;
                    double hold = PilotInventoryStore.HoldCapacity(setup, catalog, courier, haulerShip.Id, flag);
                    double tritaniumVolume = PilotInventoryStore.Volume(catalog, Tritanium);
                    double remoteVolume = remoteUnits * PilotInventoryStore.Volume(catalog, remote.TypeID);
                    double fullTritanium = remoteNeeds[Tritanium] * tritaniumVolume;
                    double ballastVolume = PilotInventoryStore.Volume(catalog, Pyerite);
                    double occupiedVolume = InventoryPersistence.UsedVolume(setup, catalog, courier, haulerShip.Id, flag);
                    int ballast = (int)Math.Floor((hold - occupiedVolume - fullTritanium - remoteVolume / 2) / ballastVolume);
                    Assert.True(ballast > 0);
                    setup.Add(
                        new OwnedItem
                        {
                            Id = setup.NextItemId(),
                            OwnerId = courier,
                            LocationId = haulerShip.Id,
                            FlagId = flag,
                            TypeId = Pyerite,
                            Quantity = ballast,
                        }
                    );
                    setup.SaveChanges();
                    double free = hold - InventoryPersistence.UsedVolume(setup, catalog, courier, haulerShip.Id, flag);
                    Assert.True(
                        free >= Math.Max(fullTritanium, remoteVolume) && free < fullTritanium + remoteVolume,
                        $"Free {free}, capacity {hold}, occupied {occupiedVolume}, loads {remoteVolume} and {fullTritanium}."
                    );
                    expected[Tritanium] = (int)Math.Floor((free - remoteVolume + InventoryDefinitions.ResourceTolerance) / tritaniumVolume);
                    Assert.InRange(expected[Tritanium], 1, remoteNeeds[Tritanium] - 1);
                }
                setup.Commit();
            }

            var market = host.GetRequiredService<IPersonalMarket>();
            market.PlaceBuy(Customer, new(FactoryStation, product, unitBid, 1, 7));
            foreach (var offer in new[] { (Owner: Customer, Station: FactoryStation), (Owner: (long)supplier, Station: Depot) })
            {
                var asks = live.Economy.Read(world =>
                    world
                        .Filter<OwnedItem>(item => item.OwnerId == offer.Owner)
                        .AsEnumerable()
                        .Where(item => item.LocationId == offer.Station && remoteNeeds.ContainsKey(item.TypeId))
                        .Select(item => new MarketSale(item.Id, offer.Station, item.TypeId, materialsPrice, item.Quantity))
                        .ToArray()
                );
                market.PlaceSell(offer.Owner, asks, 7);
            }
            supplierOrders = market.PersonalOrders(supplier);
            var industry = host.GetRequiredService<CapsuleerIndustry>();
            var ships = host.GetRequiredService<SqliteWorld>();
            using (var factory = live.Economy.Begin(TransactionParticipants.None))
            {
                var miner = ships.FindPilot(factory, -1, manufacturer)!;
                Assert.False(industry.Produce(factory, catalog, miner, fixture.Clock.Now, out var request));
                Assert.NotNull(request);
                Assert.Equal(remoteNeeds.Count, request.Materials.Length);
                Assert.All(request.Materials, need => Assert.Equal(remoteNeeds[need.TypeId], need.HangarTarget));
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
            MarketSale[] localRestock;
            using (var restock = live.Economy.Begin(TransactionParticipants.None))
            {
                foreach (var (type, quantity) in remoteNeeds)
                {
                    AddStock(restock, Customer, FactoryStation, type, quantity);
                }
                localRestock = restock
                    .Filter<OwnedItem>(item => item.OwnerId == Customer)
                    .AsEnumerable()
                    .Where(item => remoteNeeds.ContainsKey(item.TypeId))
                    .Select(item => new MarketSale(item.Id, FactoryStation, item.TypeId, materialsPrice, item.Quantity))
                    .ToArray();
                restock.Commit();
            }
            market.PlaceSell(Customer, localRestock, 7);
            var localReplacements = market
                .PersonalOrders(Customer)
                .Where(order => remoteNeeds.TryGetValue(order.TypeId, out int quantity) && order.Remaining == quantity)
                .ToArray();
            Assert.Equal(remoteNeeds.Count, localReplacements.Length);
            using (var nowLocal = live.Economy.Begin(TransactionParticipants.None))
            {
                var hauler = ships.FindPilot(nowLocal, -1, courier)!;
                Assert.Null(industry.PlanFactoryCollection(nowLocal, catalog, live, hauler, new HashSet<int>(), fixture.Clock.Now));
            }
            foreach (var replacement in localReplacements)
            {
                market.Cancel(Customer, replacement.Id, replacement.RegionId);
            }

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
            Assert.Contains(supplierOrders, order => order.Id == cargo.Sequence);
            Assert.Equal(expected.Count, cargo.Stacks.Length);
            Assert.Equal(
                expected.OrderBy(line => line.Key),
                cargo.Stacks.ToDictionary(stack => stack.TypeId, stack => stack.Quantity).OrderBy(line => line.Key)
            );
            live.Economy.Read(world =>
            {
                foreach (var stack in cargo.Stacks)
                {
                    Assert.Equal(0, Stock(world, manufacturer, FactoryStation, stack.TypeId));
                    var orders = supplierOrders.Where(order => order.TypeId == stack.TypeId).ToArray();
                    Assert.Equal(
                        orders.Sum(order => order.Entered) - stack.Quantity,
                        orders.Sum(order => world.Set<SavedPersonalOrder>().Find(order.Id)!.Remaining)
                    );
                    Assert.Equal(progress.Pilot.ShipId, world.Set<OwnedItem>().Find(stack.ItemId)!.LocationId);
                }
                long spent = expected.Values.Sum() * materialsPrice;
                Assert.Equal(factoryFunds - spent, world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                int flag = (int)InventoryFlag.Cargo;
                double used = InventoryPersistence.UsedVolume(world, catalog, courier, progress.Pilot.ShipId, flag);
                double capacity = PilotInventoryStore.HoldCapacity(world, catalog, courier, progress.Pilot.ShipId, flag);
                Assert.True(used <= capacity + InventoryDefinitions.ResourceTolerance);
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
        CapsuleerIndustry.Deliver(delivery, reopenedCatalog, shipStore.FindPilot(delivery, -1, courier)!, shipment, cargo);
        foreach (var stack in cargo.Stacks)
        {
            Assert.Equal(stack.Quantity, Stock(delivery, manufacturer, FactoryStation, stack.TypeId));
            Assert.Null(delivery.Set<OwnedItem>().Find(stack.ItemId));
        }
        bool installed = reopened
            .GetRequiredService<CapsuleerIndustry>()
            .Produce(delivery, reopenedCatalog, shipStore.FindPilot(delivery, -1, manufacturer)!, fixture.Clock.Now, out var shortage);
        if (limitedHold)
        {
            Assert.False(installed);
            Assert.Empty(delivery.Set<IndustryJob>());
            var remaining = Assert.Single(shortage!.Materials);
            Assert.Equal(Tritanium, remaining.TypeId);
            Assert.Equal(remoteNeeds[Tritanium] - expected[Tritanium], remaining.PlannedUnits);
        }
        else
        {
            Assert.True(installed);
            Assert.Null(shortage);
            Assert.Equal(product, Assert.Single(delivery.Set<IndustryJob>()).ProductTypeId);
            Assert.All(cargo.Stacks, stack => Assert.Equal(0, Stock(delivery, manufacturer, FactoryStation, stack.TypeId)));
        }
        delivery.Commit();
    }

    [Fact]
    public async Task LostMerlinBuysPaidReplacementsAndReturnsToDutyAcrossRestarts()
    {
        const int merlinBlueprint = 950;
        const long buyerFunds = 100_000_000L * MarketDefinitions.CentsPerIsk;
        const int factorySystem = 30000142;
        const int railgun = (int)CombatWeaponType.GatlingRailgun75mm;
        const int hull = (int)TechOneHullType.Merlin;
        const int rounds = 400;
        const int secondTurretSlot = (int)InventoryFlag.HighSlot0 + 2;
        var recipe = StarterProductionTests.ReadAudit().Recipes.Single(recipe => recipe.BlueprintId == merlinBlueprint);
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture, fundRefits: false);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long producer;
        long patrol;
        long lostShip;
        long capsule;
        int buyer;
        int seller;
        long[] orderIds;
        long[] paymentIds;
        long balanceAfterPurchase;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin())
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingGrants = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                producer = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                patrol = agents.First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                var miner = setup.Pilots.Find(producer)!;
                miner.ActiveShip.StationId = FactoryStation;
                var pilot = setup.Pilots.Find(patrol)!;
                pilot.ActiveShip.StationId = FactoryStation;
                var player = setup.Pilots.Find(Customer)!;
                player.ActiveShip.StationId = FactoryStation;
                seller = Charter(setup, catalog, producer, "Merlin Works", "MRLN", fixture.Clock.Now);
                buyer = Charter(setup, catalog, patrol, "Patrol Fleet", "PTFL", fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(seller)!.BalanceCents = buyerFunds;
                // Corporation stock covers one turret. The second turret and the charges need the market.
                AddStock(setup, buyer, FactoryStation, railgun, 1);
                setup.Filter<OwnedItem>(item => item.OwnerId == buyer).Single().FlagId = CorporationStorageRules.MarketDeliveriesFlag;
                AddStock(setup, Customer, FactoryStation, railgun, 1);
                AddStock(setup, Customer, FactoryStation, Rules.ChargeType, rounds);
                AddStock(setup, producer, FactoryStation, merlinBlueprint, 1);
                var original = setup.Filter<OwnedItem>(item => item.OwnerId == producer).Single(item => item.TypeId == merlinBlueprint);
                original.Singleton = true;
                original.BlueprintRuns = -1;
                foreach (var material in recipe.Materials)
                {
                    AddStock(setup, producer, FactoryStation, material.TypeID, material.Quantity);
                }
                foreach (var agent in agents.Where(agent => agent.PilotId != patrol && agent.PilotId != producer))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                // Drive factory decisions explicitly so the integration test can control the market fill.
                setup.Set<SavedCapsuleerJob>().Find(producer)!.DueTicks = TimeSpan.FromHours(6).Ticks;
                foreach (var item in setup.Filter<OwnedItem>(item => item.OwnerId == patrol).ToArray())
                {
                    setup.Remove(item);
                }
                lostShip = pilot.ActiveShipId;
                TrafficFitting.Apply(
                    setup,
                    catalog,
                    patrol,
                    lostShip,
                    TrafficSnapshotTests.Profile() with
                    {
                        HullTypeId = hull,
                        Equipment =
                        [
                            new(railgun, (int)InventoryFlag.HighSlot0, 1),
                            new(Rules.ChargeType, (int)InventoryFlag.HighSlot0, 20),
                            new(railgun, secondTurretSlot, 1),
                            new(Rules.ChargeType, secondTurretSlot, 20),
                        ],
                    },
                    fixture.Clock.Now
                );
                var station = SystemObjects.Geometry(catalog, factorySystem).Single(body => body.Id == FactoryStation);
                SqliteWorld.SetPosition(setup, catalog, lostShip, station.Position, factorySystem, fixture.Clock.Now);
                ShipLossPersistence.Apply(
                    setup,
                    catalog,
                    pilot,
                    null,
                    lostShip,
                    factorySystem,
                    pilot.ActiveShip.Position!,
                    fixture.Clock.Now,
                    host.GetRequiredService<IShipLootRoll>(),
                    PilotInteractionHistoryStore.Player(setup, player, player.ActiveShipId)
                );
                capsule = pilot.ActiveShipId;
                setup.Commit();
            }

            using var human = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem);
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            scheduler.PopulationPrepared(factorySystem);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var waiting = Advance(maintenance, scheduler, progress => progress.Work.WaitingReason == "Waiting for a replacement ship.");
            Assert.Equal(capsule, waiting.Pilot.ShipId);
            Assert.Equal(FactoryStation, waiting.Pilot.StationId);
            Assert.Empty(live.Economy.Read(BuyerOrders));
            Assert.Empty(live.Economy.Read(BuyerPayments));

            using (var fund = live.Economy.Begin())
            {
                fund.Set<SavedCorporation>().Find(buyer)!.BalanceCents = buyerFunds;
                fund.Commit();
            }
            for (int i = 0; i < 95; i++)
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
            }
            var open = live.Economy.Read(BuyerOrders);
            Assert.Equal(3, open.Length);
            Assert.All(open, order => Assert.Equal(PersonalOrderStatus.Open, order.Status));
            Assert.Equal(1, Assert.Single(open, order => order.TypeId == railgun).Entered);
            orderIds = open.Select(order => order.Id).Order().ToArray();

            MarketSale[] supplies = live.Economy.Read(world =>
                world
                    .Filter<OwnedItem>(item => item.OwnerId == Customer)
                    .Where(item => item.TypeId == railgun || item.TypeId == Rules.ChargeType)
                    .AsEnumerable()
                    .Select(item => new MarketSale(
                        item.Id,
                        FactoryStation,
                        item.TypeId,
                        open.Single(order => order.TypeId == item.TypeId).PriceCents,
                        item.Quantity
                    ))
                    .ToArray()
            );
            host.GetRequiredService<IPersonalMarket>().PlaceSell(Customer, supplies, 0);

            var industry = host.GetRequiredService<CapsuleerIndustry>();
            var ships = host.GetRequiredService<SqliteWorld>();
            using (var install = live.Economy.Begin())
            {
                industry.Produce(install, catalog, ships.FindPilot(install, -1, producer)!, fixture.Clock.Now);
                var job = Assert.Single(install.Set<IndustryJob>());
                Assert.Equal(hull, job.ProductTypeId);
                Assert.Equal(seller, job.OutputCorporationId);
                install.Commit();
            }
            fixture.Clock.Seconds += recipe.Time;
            using (var finish = live.Economy.Begin())
            {
                Assert.True(industry.Produce(finish, catalog, ships.FindPilot(finish, -1, producer)!, fixture.Clock.Now));
                Assert.Equal(capsule, finish.Pilots.Find(patrol)!.ActiveShipId);
                Assert.Equal(1, Stock(finish, patrol, FactoryStation, hull));
                Assert.Equal(2, Stock(finish, patrol, FactoryStation, railgun));
                Assert.Equal(rounds, Stock(finish, patrol, FactoryStation, Rules.ChargeType));
                var orders = BuyerOrders(finish);
                Assert.All(orders, order => Assert.Equal(PersonalOrderStatus.Filled, order.Status));
                Assert.All(orders, order => Assert.Equal(0, order.EscrowCents));
                balanceAfterPurchase = finish.Set<SavedCorporation>().Find(buyer)!.BalanceCents;
                var payments = BuyerPayments(finish);
                Assert.Equal(3, payments.Count(payment => payment.EntryType == WalletEntryType.MarketEscrow));
                Assert.Equal(3, payments.Count(payment => payment.EntryType == WalletEntryType.BrokersFee));
                Assert.Equal(
                    buyerFunds - orders.Sum(order => order.PriceCents * order.Entered),
                    balanceAfterPurchase
                        - payments.Where(payment => payment.EntryType == WalletEntryType.BrokersFee).Sum(payment => payment.AmountCents)
                );
                paymentIds = payments.Select(payment => payment.Id).Order().ToArray();
                finish.Commit();
            }
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(capsule, restored.Economy.Read(world => world.Pilots.Find(patrol)!.ActiveShipId));
        using var occupied = reopened.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem);
        var resumed = reopened.GetRequiredService<CapsuleerScheduler>();
        resumed.PopulationPrepared(Home);
        resumed.PopulationPrepared(factorySystem);
        var backOnDuty = Advance(
            reopened.GetRequiredService<CapsuleerMaintenance>(),
            resumed,
            progress => progress.Pilot.ShipTypeId == hull && progress.Pilot.Position is not null
        );
        Assert.NotEqual(lostShip, backOnDuty.Pilot.ShipId);
        Assert.Equal(backOnDuty.Pilot.ShipId, backOnDuty.Work.ShipId);
        long firstReplacement = backOnDuty.Pilot.ShipId;
        restored.Economy.Inspect(world =>
        {
            Assert.Null(world.Ships.Find(lostShip));
            Assert.Null(world.Ships.Find(capsule));
            AssertFit(world, firstReplacement);
            Assert.Equal(orderIds, BuyerOrders(world).Select(order => order.Id).Order());
            Assert.Equal(paymentIds, BuyerPayments(world).Select(payment => payment.Id).Order());
            Assert.Equal(balanceAfterPurchase, world.Set<SavedCorporation>().Find(buyer)!.BalanceCents);
            return true;
        });

        long secondCapsule;
        using (var loss = restored.Economy.Begin())
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            var station = SystemObjects.Geometry(catalog, factorySystem).Single(body => body.Id == FactoryStation);
            SqliteWorld.SetPosition(loss, catalog, firstReplacement, station.Position, factorySystem, fixture.Clock.Now);
            secondCapsule = DestroyBotShip(loss, catalog, patrol, fixture.Clock.Now);
            loss.Commit();
        }
        var secondRecovery = Advance(
            reopened.GetRequiredService<CapsuleerMaintenance>(),
            resumed,
            progress => progress.Pilot.ShipId == secondCapsule && progress.Work.ReplacementOrderIds?.Length == 3
        );
        long[] secondBids = secondRecovery.Work.ReplacementOrderIds!;
        Assert.Empty(secondBids.Intersect(orderIds));
        await restored.FlushAsync();
        occupied.Dispose();
        reopened.Dispose();

        using var repeat = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var repeated = repeat.GetRequiredService<LiveWorld>();
        MarketSale[] newSupply;
        using (var sell = repeated.Economy.Begin())
        {
            Assert.Equal(secondCapsule, sell.Pilots.Find(patrol)!.ActiveShipId);
            var saved = HaulerCheckpoint.Read(sell.Filter<SavedCapsuleerHauler>(row => row.PilotId == patrol).Single().Progress);
            Assert.Equal(secondBids, saved?.ReplacementOrderIds);
            var current = BuyerOrders(sell).Where(order => secondBids.Contains(order.Id)).ToArray();
            Assert.All(current, order => Assert.Equal(PersonalOrderStatus.Open, order.Status));
            Assert.Equal(2, Assert.Single(current, order => order.TypeId == railgun).Remaining);
            foreach (var order in current)
            {
                AddStock(sell, Customer, FactoryStation, order.TypeId, order.Remaining);
            }
            newSupply = sell.Filter<OwnedItem>(item => item.OwnerId == Customer)
                .AsEnumerable()
                .Where(item => current.Any(order => order.TypeId == item.TypeId))
                .Select(item => new MarketSale(
                    item.Id,
                    FactoryStation,
                    item.TypeId,
                    current.Single(order => order.TypeId == item.TypeId).PriceCents,
                    item.Quantity
                ))
                .ToArray();
            sell.Commit();
        }
        repeat.GetRequiredService<IPersonalMarket>().PlaceSell(Customer, newSupply, 0);
        using var secondObserver = repeat.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem);
        var repeatScheduler = repeat.GetRequiredService<CapsuleerScheduler>();
        repeatScheduler.PopulationPrepared(Home);
        repeatScheduler.PopulationPrepared(factorySystem);
        var secondDuty = Advance(
            repeat.GetRequiredService<CapsuleerMaintenance>(),
            repeatScheduler,
            progress => progress.Pilot.ShipTypeId == hull && progress.Pilot.Position is not null
        );
        Assert.DoesNotContain(secondDuty.Pilot.ShipId, new[] { lostShip, firstReplacement });
        Assert.Null(secondDuty.Work.ReplacementOrderIds);
        Assert.Null(secondDuty.Work.ReplacementOrderDeadline);
        await repeated.FlushAsync();
        secondObserver.Dispose();
        repeat.Dispose();

        using var verified = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        verified
            .GetRequiredService<LiveWorld>()
            .Economy.Inspect(world =>
            {
                Assert.Equal(secondDuty.Pilot.ShipId, world.Pilots.Find(patrol)!.ActiveShipId);
                Assert.Null(world.Ships.Find(firstReplacement));
                Assert.Null(world.Ships.Find(secondCapsule));
                Assert.Equal(
                    new[] { lostShip, firstReplacement }.Order(),
                    world.Filter<ShipLoss>(loss => loss.PilotId == patrol).AsEnumerable().Select(loss => loss.ShipId).Order()
                );
                AssertFit(world, secondDuty.Pilot.ShipId);
                var orders = BuyerOrders(world);
                Assert.Equal(orderIds.Concat(secondBids).Order(), orders.Select(order => order.Id).Order());
                Assert.All(
                    orders,
                    order =>
                    {
                        Assert.Equal(PersonalOrderStatus.Filled, order.Status);
                        Assert.Equal(0, order.EscrowCents);
                    }
                );
                var payments = BuyerPayments(world);
                Assert.Equal(6, payments.Count(payment => payment.EntryType == WalletEntryType.MarketEscrow));
                Assert.Equal(6, payments.Count(payment => payment.EntryType == WalletEntryType.BrokersFee));
                Assert.Equal(
                    buyerFunds
                        - orders.Sum(order => order.PriceCents * order.Entered)
                        + payments.Where(payment => payment.EntryType == WalletEntryType.BrokersFee).Sum(payment => payment.AmountCents),
                    world.Set<SavedCorporation>().Find(buyer)!.BalanceCents
                );
                return true;
            });

        void AssertFit(WorldAction world, long ship)
        {
            Assert.Equal(ship, Assert.Single(world.Ships.Where(item => item.OwnerId == patrol && item.TypeId == hull)).Id);
            var fitted = world
                .Filter<OwnedItem>(item => item.LocationId == ship)
                .Where(item => item.TypeId == railgun && item.Online)
                .ToArray();
            Assert.Equal([(int)InventoryFlag.HighSlot0, secondTurretSlot], fitted.Select(module => module.FlagId).Order());
            foreach (var module in fitted)
            {
                var magazine = Assert.Single(world.Filter<OwnedItem>(item => item.LocationId == module.Id));
                Assert.Equal(Rules.ChargeType, magazine.TypeId);
                Assert.Equal(rounds / fitted.Length, magazine.Quantity);
            }
        }

        SavedPersonalOrder[] BuyerOrders(WorldAction world) =>
            world.Filter<SavedPersonalOrder>(order => order.PilotId == patrol).Where(order => order.CorporationId == buyer).ToArray();

        SavedWalletLedger[] BuyerPayments(WorldAction world) =>
            world
                .Filter<SavedWalletLedger>(payment => payment.OwnerId == buyer)
                .Where(payment => payment.EntryType == WalletEntryType.BrokersFee || payment.EntryType == WalletEntryType.MarketEscrow)
                .ToArray();

        HaulerObservation Advance(CapsuleerMaintenance worker, CapsuleerScheduler scheduler, Func<HaulerObservation, bool> ready)
        {
            HaulerObservation? last = null;
            for (int turn = 0; turn < 500; turn++)
            {
                fixture.Clock.Seconds++;
                worker.Tick();
                last = scheduler.Hauler(patrol);
                if (last is not null && ready(last))
                {
                    return last;
                }
            }
            throw new InvalidOperationException($"Patrol did not reach the expected state. Last progress was {last?.Work}.");
        }
    }

    [Fact]
    public async Task StalledVentureBidsRefundAfterRestartAndMinerUndocksWithAnInStockFit()
    {
        const int hull = (int)CapsuleerMiningHull.Venture;
        const int laser = (int)MiningLaserType.MinerI;
        const long funds = 100_000_000L * MarketDefinitions.CentsPerIsk;
        const long ask = 10_000;
        const long hubPrice = 123_456;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture, fundRefits: false);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        PassiveCatalogFixture.LoadSovereignty(fixture.Database.CatalogPath);
        long miner;
        long capsule;
        long lostHull;
        long deadline;
        int employer;
        long[] bids;
        long totalFees;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin())
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingGrants = false;
                miner = setup.Set<SavedCapsuleerAgent>().Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                var pilot = setup.Pilots.Find(miner)!;
                var player = setup.Pilots.Find(Customer)!;
                pilot.ActiveShip.StationId = Depot;
                player.ActiveShip.StationId = Depot;
                setup.Add(new PilotWallet { PilotId = Customer, Cents = 10 * PersonalMarketRules.MinimumBrokerFeeCents });
                employer = Charter(setup, catalog, miner, "Venture Recovery", "VREC", fixture.Clock.Now);
                var corporation = setup.Set<SavedCorporation>().Find(employer)!;
                corporation.BalanceCents = funds;
                host.GetRequiredService<PilotMarketStore>()
                    .BuyCorporationStock(
                        setup,
                        catalog,
                        miner,
                        Depot,
                        corporation,
                        [new(EntosisRules.HubTypeId, hubPrice, 1)],
                        CapsuleerAllianceRules.FirstHubOrderDays
                    );
                foreach (var agent in setup.Set<SavedCapsuleerAgent>().Where(agent => agent.PilotId != miner).ToArray())
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                setup.RemoveRange(setup.Filter<OwnedItem>(item => item.OwnerId == miner).ToArray());
                lostHull = pilot.ActiveShipId;
                TrafficFitting.Apply(
                    setup,
                    catalog,
                    miner,
                    lostHull,
                    TrafficSnapshotTests.Profile() with
                    {
                        HullTypeId = hull,
                        Equipment = [new(laser, (int)InventoryFlag.HighSlot0, 1), new(laser, (int)InventoryFlag.HighSlot1, 1)],
                    },
                    fixture.Clock.Now
                );
                var station = SystemObjects.Geometry(catalog, Home).Single(body => body.Id == Depot);
                SqliteWorld.SetPosition(setup, catalog, lostHull, station.Position, Home, fixture.Clock.Now);
                ShipLossPersistence.Apply(
                    setup,
                    catalog,
                    pilot,
                    null,
                    lostHull,
                    Home,
                    pilot.ActiveShip.Position!,
                    fixture.Clock.Now,
                    host.GetRequiredService<IShipLootRoll>(),
                    PilotInteractionHistoryStore.Player(setup, player, player.ActiveShipId)
                );
                capsule = pilot.ActiveShipId;
                setup.Commit();
            }
            using var occupied = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            var state = Advance(host, observation => observation.Work.ReplacementOrderDeadline.HasValue);
            deadline = state.Work.ReplacementOrderDeadline!.Value;
            var placed = live.Economy.Read(Orders);
            Assert.Equal(capsule, state.Pilot.ShipId);
            Assert.Equal(2, placed.Length);
            Assert.Equal(2, Assert.Single(placed, order => order.TypeId == laser).Remaining);
            Assert.All(placed, order => Assert.True(order.EscrowCents > 0));
            bids = placed.Select(order => order.Id).Order().ToArray();
            Assert.Equal(bids, Assert.IsType<long[]>(state.Work.ReplacementOrderIds).Order());
            totalFees = live.Economy.Read(world =>
                -world
                    .Filter<SavedWalletLedger>(payment => payment.OwnerId == employer)
                    .Where(payment => payment.EntryType == WalletEntryType.BrokersFee)
                    .Sum(payment => payment.AmountCents)
            );
            await live.FlushAsync();
        }

        fixture.Clock.AdvanceWallTime(Replacement.BidWindow + TimeSpan.FromSeconds(1));
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var checkpoint = Assert.IsType<MinerProgress>(
            restored.Economy.Read(world =>
                MinerCheckpoint.Read(world.Set<SavedCapsuleerCommunity>().Single(row => row.PilotId == miner).Progress)
            )
        );
        Assert.Equal(deadline, checkpoint.ReplacementOrderDeadline);
        Assert.Equal(bids, Assert.IsType<long[]>(checkpoint.ReplacementOrderIds).Order());
        using var human = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        var refunded = Advance(
            reopened,
            observation => observation.Work.WaitingReason == "Waiting for an affordable mining replacement in stock."
        );
        Assert.Equal(capsule, refunded.Pilot.ShipId);
        var stockRetry = Advance(
            reopened,
            observation =>
                observation.Work.WaitingReason == refunded.Work.WaitingReason && observation.Work.DueTicks > refunded.Work.DueTicks
        );
        Assert.Equal(capsule, stockRetry.Pilot.ShipId);
        restored.Economy.Inspect(world =>
        {
            Assert.All(
                Orders(world),
                order =>
                {
                    Assert.Equal(PersonalOrderStatus.Cancelled, order.Status);
                    Assert.Equal(0, order.EscrowCents);
                }
            );
            AssertHubBid(world);
            Assert.Equal(funds - totalFees - hubPrice, world.Set<SavedCorporation>().Find(employer)!.BalanceCents);
            return true;
        });
        MarketSale[] supplies;
        using (var stock = restored.Economy.Begin())
        {
            AddStock(stock, Customer, Depot, hull, 1);
            AddStock(stock, Customer, Depot, laser, 1);
            supplies = stock
                .Filter<OwnedItem>(item => item.OwnerId == Customer)
                .Where(item => item.TypeId == hull || item.TypeId == laser)
                .AsEnumerable()
                .Select(item => new MarketSale(item.Id, Depot, item.TypeId, ask, 1))
                .ToArray();
            stock.Commit();
        }
        reopened.GetRequiredService<IPersonalMarket>().PlaceSell(Customer, supplies, 7);
        var working = Advance(reopened, observation => observation.Pilot.ShipTypeId == hull && observation.Pilot.Position is not null);
        Assert.NotEqual(lostHull, working.Pilot.ShipId);
        Assert.Equal(laser, working.Laser?.TypeId);
        Assert.Null(working.Work.ReplacementOrderDeadline);
        Assert.Null(working.Work.ReplacementOrderIds);
        restored.Economy.Inspect(world =>
        {
            Assert.Null(world.Ships.Find(capsule));
            Assert.Single(
                world.Filter<OwnedItem>(item => item.LocationId == working.Pilot.ShipId),
                item => item.TypeId == laser && item.Online
            );
            Assert.Equal(bids, Orders(world).Select(order => order.Id).Order());
            AssertHubBid(world);
            Assert.Equal(funds - totalFees - hubPrice - 2 * ask, world.Set<SavedCorporation>().Find(employer)!.BalanceCents);
            return true;
        });

        SavedPersonalOrder[] Orders(WorldAction world) =>
            world
                .Filter<SavedPersonalOrder>(order => order.PilotId == miner)
                .Where(order => order.CorporationId == employer && order.TypeId != EntosisRules.HubTypeId)
                .ToArray();

        void AssertHubBid(WorldAction world)
        {
            var hub = Assert.Single(
                world.Filter<SavedPersonalOrder>(order => order.PilotId == miner),
                order => order.CorporationId == employer && order.TypeId == EntosisRules.HubTypeId
            );
            Assert.Equal(Depot, hub.StationId);
            Assert.Equal(PersonalOrderStatus.Open, hub.Status);
            Assert.Equal(1, hub.Remaining);
            Assert.Equal(hubPrice, hub.EscrowCents);
        }

        MinerObservation Advance(ServiceProvider host, Func<MinerObservation, bool> ready)
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            MinerObservation? last = null;
            for (int turn = 0; turn < 900; turn++)
            {
                fixture.Clock.Seconds++;
                worker.Tick();
                last = scheduler.Miner(miner);
                if (last is not null && ready(last))
                {
                    return last;
                }
            }
            throw new InvalidOperationException($"Miner did not reach the expected state. Last progress was {last?.Work}.");
        }
    }

    [Fact]
    public async Task MinerTwoFactoryBuysCopiesInventsManufacturesAndSellsAcrossRestarts()
    {
        const int factorySystem = 30000142;
        const int MinerTwo = CapsuleerMinerTwoRules.Module;
        const int MinerOneBlueprint = CapsuleerMinerTwoRules.Original;
        const int MinerTwoBlueprint = CapsuleerMinerTwoRules.InventedBlueprint;
        const int MechanicalDatacore = 20424;
        const int LaserDatacore = 20413;
        const long capital = 100_000_000L * MarketDefinitions.CentsPerIsk;
        const long inputAsk = 100 * MarketDefinitions.CentsPerIsk;
        const long moduleBid = 60_000_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        PrepareMinerTwoCatalog(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        var random = new MinerTwoRandom();
        long producer;
        int corporation;
        HashSet<int> inputTypes = [MinerOneBlueprint, MechanicalDatacore, LaserDatacore];
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin())
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingGrants = false;
                setup.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
                producer = setup.Set<SavedCapsuleerAgent>().Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                foreach (var agent in setup.Set<SavedCapsuleerAgent>().Where(agent => agent.PilotId != producer).ToArray())
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                setup.Pilots.Find(producer)!.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(Customer)!.ActiveShip.StationId = FactoryStation;
                corporation = Charter(setup, catalog, producer, "Miner Two Works", "MINR", fixture.Clock.Now);
                setup.Add(new PilotWallet { PilotId = Customer, Cents = capital });
                var trade =
                    setup.Set<PilotSkill>().Find(Customer, (int)MarketSkill.Trade)
                    ?? setup.Add(new PilotSkill { PilotId = Customer, TypeId = (int)MarketSkill.Trade }).Entity;
                trade.Points = TrainedSkill.PointsForLevel(1, 2);
                var recipe = ManufacturingCatalog.Recipes(catalog)[MinerTwoBlueprint];
                SetMinerTwoAdjustedPrices(setup, catalog);
                var materials = IndustryMaterials.Calculate(
                    recipe.Materials,
                    CapsuleerMinerTwoRules.BatchSize,
                    IndustryDefinitions.InventionMaterialEfficiency
                );
                foreach (var (type, quantity) in materials)
                {
                    AddStock(setup, Customer, FactoryStation, type, quantity);
                    inputTypes.Add(type);
                }
                AddStock(setup, Customer, FactoryStation, MinerOneBlueprint, 1);
                AddStock(setup, Customer, FactoryStation, MechanicalDatacore, 4);
                AddStock(setup, Customer, FactoryStation, LaserDatacore, 4);
                setup.Commit();
            }
            MarketSale[] listings = live.Economy.Read(world =>
                world
                    .Filter<OwnedItem>(item => item.OwnerId == Customer)
                    .Where(item => item.LocationId == FactoryStation)
                    .AsEnumerable()
                    .Where(item => inputTypes.Contains(item.TypeId))
                    .Select(item => new MarketSale(item.Id, FactoryStation, item.TypeId, inputAsk, item.Quantity))
                    .ToArray()
            );
            Assert.All(
                listings,
                listing =>
                    Assert.True(
                        catalog.Read(CatalogData.Client.Types, (long)listing.TypeId) is { Published: 1, MarketGroupID: > 0 }
                            && catalog.FittingType(listing.TypeId) is not null,
                        $"Missing market metadata for input {listing.TypeId}."
                    )
            );
            host.GetRequiredService<IPersonalMarket>().PlaceSell(Customer, listings, 7);
            Assert.False(Produce(host));
            live.Economy.Inspect(world =>
            {
                Assert.Empty(Jobs(world));
                Assert.Equal(0, Stock(world, producer, FactoryStation, MinerOneBlueprint));
                return true;
            });
            using (var funded = live.Economy.Begin())
            {
                funded.Set<SavedCorporation>().Find(corporation)!.BalanceCents = capital;
                funded.Commit();
            }
            await live.FlushAsync();
        }

        IndustryActivity[] stages =
        [
            IndustryActivity.Copying,
            IndustryActivity.Invention,
            IndustryActivity.Copying,
            IndustryActivity.Invention,
            IndustryActivity.Manufacturing,
        ];
        IndustryJob? previous = null;
        for (int index = 0; index < stages.Length; index++)
        {
            using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
            var live = host.GetRequiredService<LiveWorld>();
            if (previous is { } saved)
            {
                live.Economy.Inspect(world =>
                {
                    var reopened = world.Set<IndustryJob>().Find(saved.Id)!;
                    Assert.Equal(IndustryJobStatus.Installed, reopened.Status);
                    Assert.Equal(saved.EndTime, reopened.EndTime);
                    Assert.Equal(saved.CapsuleerEndTicks, reopened.CapsuleerEndTicks);
                    Assert.Equal(saved.SuccessfulRuns, reopened.SuccessfulRuns);
                    Assert.Equal(corporation, reopened.OutputCorporationId);
                    return true;
                });
                if (saved.CapsuleerEndTicks is long due)
                {
                    using var human = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem);
                    long remaining = due - live.CapsuleerActivity.Read().ElapsedTicks;
                    fixture.Clock.Seconds += (remaining + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
                }
            }
            if (index == 2)
            {
                // The first invention roll failed. Delivery after reopening must honor the saved roll.
                Assert.False(Produce(host));
                Assert.False(Produce(host));
                live.Economy.Inspect(world =>
                {
                    Assert.Equal(2, Jobs(world).Length);
                    Assert.Equal(0, Jobs(world)[1].SuccessfulRuns);
                    Assert.Equal(IndustryJobStatus.Delivered, Jobs(world)[1].Status);
                    Assert.Equal(0, Stock(world, corporation, FactoryStation, MinerTwoBlueprint));
                    Assert.Equal(0, Stock(world, producer, FactoryStation, MechanicalDatacore));
                    Assert.Equal(0, Stock(world, producer, FactoryStation, LaserDatacore));
                    return true;
                });
                fixture.Clock.AdvanceWallTime(CapsuleerMinerTwoRules.FailureRetry);
            }
            if (index == 0)
            {
                using var human = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem);
                host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(factorySystem);
                MinerObservation? observed = null;
                for (int step = 0; step < 12 && live.Economy.Read(world => Jobs(world).Length) == 0; step++)
                {
                    observed = CapsuleerTestDriver.Miner(host, fixture.Clock);
                }
                Assert.True(
                    live.Economy.Read(world => Jobs(world).Length) > 0,
                    $"Factory miner did not install copying. {observed?.Work.Activity}, {observed?.WaitingReason}"
                );
            }
            else
            {
                Produce(host);
            }
            previous = live.Economy.Read(world =>
            {
                var jobs = Jobs(world);
                Assert.Equal(index + 1, jobs.Length);
                var running = Assert.Single(jobs, job => job.Status == IndustryJobStatus.Installed);
                Assert.Equal(stages[index], running.Activity);
                Assert.Equal(corporation, running.OutputCorporationId);
                return running;
            });
            Assert.False(Produce(host));
            Assert.Equal(
                previous.Id,
                live.Economy.Read(world => Assert.Single(Jobs(world), job => job.Status == IndustryJobStatus.Installed).Id)
            );
            await live.FlushAsync();
            if (previous.CapsuleerEndTicks is null)
            {
                fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(previous.EndTime - fixture.Clock.Now));
            }
        }

        using var finalHost = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var final = finalHost.GetRequiredService<LiveWorld>();
        Assert.Equal(IndustryJobStatus.Installed, final.Economy.Read(world => world.Set<IndustryJob>().Find(previous!.Id)!.Status));
        var book = finalHost.GetRequiredService<IPersonalMarket>();
        book.PlaceBuy(Customer, new(FactoryStation, MinerTwo, moduleBid, CapsuleerMinerTwoRules.BatchSize, 7));
        Assert.True(Produce(finalHost));
        Assert.False(Produce(finalHost));
        final.Economy.Inspect(world =>
        {
            var jobs = Jobs(world);
            Assert.Equal(stages, jobs.Select(job => job.Activity));
            Assert.All(jobs, job => Assert.Equal(IndustryJobStatus.Delivered, job.Status));
            Assert.Equal(0, jobs[1].SuccessfulRuns);
            Assert.Equal(1, jobs[3].SuccessfulRuns);
            Assert.Equal(2, random.Calls);
            Assert.Equal(CapsuleerMinerTwoRules.BatchSize, Stock(world, Customer, FactoryStation, MinerTwo));
            Assert.Equal(0, Stock(world, corporation, FactoryStation, MinerTwo));
            var original = Assert.Single(
                world.Filter<OwnedItem>(item => item.OwnerId == producer),
                item => item.TypeId == MinerOneBlueprint
            );
            Assert.Equal(-1, original.BlueprintRuns);
            var copy = Assert.Single(world.Filter<OwnedItem>(item => item.OwnerId == producer), item => item.TypeId == MinerTwoBlueprint);
            Assert.Equal(10 - CapsuleerMinerTwoRules.BatchSize, copy.BlueprintRuns);
            var ledger = world.Filter<SavedWalletLedger>(entry => entry.OwnerId == corporation).ToArray();
            Assert.Single(ledger, entry => entry.EntryType == WalletEntryType.MarketTransaction && entry.AmountCents > 0);
            Assert.Equal(capital + ledger.Sum(entry => entry.AmountCents), world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
            foreach (var job in jobs)
            {
                Assert.True(job.CostCents > 0);
                var kind = job.Activity switch
                {
                    IndustryActivity.Copying => WalletEntryType.Copying,
                    IndustryActivity.Invention => WalletEntryType.Invention,
                    _ => WalletEntryType.Manufacturing,
                };
                Assert.Single(
                    ledger,
                    entry => entry.EntryType == kind && entry.AmountCents == -job.CostCents && entry.ReferenceId == job.Id
                );
            }
            return true;
        });

        bool Produce(ServiceProvider host)
        {
            var factory = new CapsuleerIndustry(
                new(fixture.Database, fixture.Clock, random),
                host.GetRequiredService<PilotReprocessingStore>(),
                host.GetRequiredService<PilotMarketStore>(),
                host.GetRequiredService<PilotFittingStore>(),
                host.GetRequiredService<PilotPlanetaryIndustryStore>(),
                host.GetRequiredService<CapsuleerCareerTraining>()
            );
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using var world = host.GetRequiredService<LiveWorld>().Economy.Begin();
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(world, -1, producer)!;
            bool changed = factory.Produce(world, catalog, pilot, fixture.Clock.Now);
            world.Commit();
            return changed;
        }

        IndustryJob[] Jobs(WorldAction world) =>
            world.Filter<IndustryJob>(job => job.OutputCorporationId == corporation).OrderBy(job => job.Id).ToArray();
    }

    [Fact]
    public async Task MissingMinerTwoDatacoresComeFromPaidResearchAndSurviveCargoRestart()
    {
        const int factorySystem = 30000142;
        const int mechanical = 20424;
        const int laser = 20413;
        const long corporationFunds = 100_000_000;
        const int perType = 2;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        PrepareMinerTwoCatalog(fixture);
        PrepareResearchSuppliers(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long manufacturer;
        long courier;
        int corporation;
        long[] projects;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin())
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingGrants = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                manufacturer = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                courier = agents.First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                setup.Pilots.Find(manufacturer)!.ActiveShip.StationId = FactoryStation;
                var hauler = setup.Pilots.Find(courier)!;
                hauler.ActiveShip.StationId = Depot;
                hauler.ActiveShip.TypeId = HaulingRules.BadgerType;
                corporation = Charter(setup, catalog, manufacturer, "Research Freight", "RFRT", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, hauler, corporation, fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = corporationFunds;
                SetMinerTwoAdjustedPrices(setup, catalog);
                foreach (var agent in agents.Where(agent => agent.PilotId != manufacturer && agent.PilotId != courier))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                // Let the factory publish a genuine shortage before the first courier action.
                setup.Set<SavedCapsuleerJob>().Find(courier)!.DueTicks = TimeSpan.FromSeconds(20).Ticks;
                setup.Commit();
            }
            using (var factory = live.Economy.Begin())
            {
                var miner = host.GetRequiredService<SqliteWorld>().FindPilot(factory, -1, manufacturer)!;
                Assert.False(
                    host.GetRequiredService<CapsuleerIndustry>().Produce(factory, catalog, miner, fixture.Clock.Now, out var request)
                );
                Assert.NotNull(request);
                Assert.True(request.ResearchDatacores);
                Assert.Equal(new[] { laser, mechanical }, request.Materials.Select(material => material.TypeId).Order());
                new MinerCheckpoint(
                    manufacturer,
                    new(MinerActivity.Docked, 0, 0, 0, 0, 0, 0, new NavigationState().Capture(), miner.ShipId) { FactorySupply = request }
                ).Apply(factory);
                factory.Commit();
            }
            using var human = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            Advance(host, () => live.Economy.Read(world => Projects(world).Length == 2), 400);
            live.Economy.Inspect(world =>
            {
                Assert.Equal(new[] { 11447, 11452 }, Projects(world).Select(project => project.SkillId).Order());
                Assert.All(Projects(world), project => Assert.Equal(0, project.Points));
                Assert.Empty(Payments(world));
                Assert.Empty(world.Set<IndustryJob>());
                Assert.Equal(0, Stock(world, manufacturer, FactoryStation, mechanical));
                Assert.Equal(0, Stock(world, manufacturer, FactoryStation, laser));
                return true;
            });
            projects = live.Economy.Read(world => Projects(world).Select(project => project.Id).Order().ToArray());
            human.Dispose();
            await live.FlushAsync();
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(7));
        long firstCargo;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(projects, live.Economy.Read(world => Projects(world).Select(project => project.Id).Order()));
            using var human = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            Advance(
                host,
                () =>
                    live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work
                        is { CorporationSupply.FactorySupply.Research: not null, Freight.Stacks.Length: > 0 },
                2600
            );
            var work = live.HaulerCheckpoints[courier].Work;
            var cargo = Assert.Single(work.Freight!.Stacks);
            Assert.Equal(perType, cargo.Quantity);
            firstCargo = cargo.ItemId;
            Assert.Equal(FactoryStation, work.Freight.Destination);
            live.Economy.Inspect(world =>
            {
                Assert.Equal(-perType * ResearchRules.CentsPerDatacore, Assert.Single(Payments(world)).AmountCents);
                Assert.Equal((int)InventoryFlag.Cargo, world.Set<OwnedItem>().Find(firstCargo)!.FlagId);
                Assert.Empty(world.Set<SavedPersonalOrder>().Where(order => order.TypeId == laser || order.TypeId == mechanical));
                Assert.Empty(
                    world.Set<NpcMarketOrder>().Where(order => (order.TypeId == laser || order.TypeId == mechanical) && !order.Bid)
                );
                return true;
            });
            human.Dispose();
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        Assert.NotNull(restored.Economy.Read(world => world.Set<OwnedItem>().Find(firstCargo)));
        using var observer = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        Advance(
            reopened,
            () =>
                restored.Economy.Read(world =>
                    Stock(world, manufacturer, FactoryStation, mechanical) == perType
                    && Stock(world, manufacturer, FactoryStation, laser) == perType
                ),
            4800
        );
        restored.Economy.Inspect(world =>
        {
            Assert.Equal(projects, Projects(world).Select(project => project.Id).Order());
            Assert.Equal(projects, Payments(world).Select(payment => payment.ReferenceId).Order());
            Assert.All(Payments(world), payment => Assert.Equal(-perType * ResearchRules.CentsPerDatacore, payment.AmountCents));
            Assert.Equal(
                corporationFunds - 2 * perType * ResearchRules.CentsPerDatacore,
                world.Set<SavedCorporation>().Find(corporation)!.BalanceCents
            );
            Assert.Null(world.Set<OwnedItem>().Find(firstCargo));
            return true;
        });
        observer.Dispose();
        await restored.FlushAsync();

        SavedResearchProject[] Projects(WorldAction world) =>
            world.Filter<SavedResearchProject>(project => project.PilotId == courier).ToArray();

        SavedWalletLedger[] Payments(WorldAction world) =>
            world
                .Filter<SavedWalletLedger>(payment => payment.OwnerId == corporation)
                .Where(payment => payment.EntryType == WalletEntryType.DatacorePurchase)
                .ToArray();

        void Advance(ServiceProvider host, Func<bool> ready, int maximumSteps)
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            scheduler.PopulationPrepared(factorySystem);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (int step = 0; step < maximumSteps; step++)
            {
                if (ready())
                {
                    return;
                }
                fixture.Clock.Seconds++;
                worker.Tick();
            }
            var state = scheduler.Hauler(courier);
            var miner = scheduler.Miner(manufacturer);
            var researchRequested = miner?.Work.FactorySupply?.ResearchDatacores;
            Assert.True(
                ready(),
                $"Research supply did not advance. Courier {state?.Work.Activity}, "
                    + $"factory {miner?.Work.Activity}, request {researchRequested}, {scheduler.Read().LastFailure}"
            );
        }
    }

    [Fact]
    public async Task MinerTwoFactoryRefinesOreAndMakesItsOwnMinerOneInputsAcrossRestart()
    {
        const int minerOne = 483;
        const int mercoxit = 11396;
        const int morphite = 11399;
        const int portion = 100;
        const long corporationFunds = 100_000_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        PrepareMinerTwoCatalog(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long manufacturer;
        int corporation;
        long componentJob;
        long componentEnd;
        int quantity;
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var recipes = ManufacturingCatalog.Recipes(catalog);
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (var setup = live.Economy.Begin())
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingGrants = false;
                manufacturer = setup.Set<SavedCapsuleerAgent>().Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                setup.Pilots.Find(manufacturer)!.ActiveShip.StationId = FactoryStation;
                corporation = Charter(setup, catalog, manufacturer, "Component Works", "COMP", fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = corporationFunds;
                SetMinerTwoAdjustedPrices(setup, catalog);

                OwnedItem original = new()
                {
                    Id = setup.NextItemId(),
                    OwnerId = manufacturer,
                    LocationId = FactoryStation,
                    TypeId = CapsuleerMinerTwoRules.Original,
                    Quantity = 1,
                    Singleton = true,
                    BlueprintRuns = -1,
                    FlagId = (int)InventoryFlag.Hangar,
                };
                setup.Add(original);
                setup.Add(
                    new OwnedItem
                    {
                        Id = setup.NextItemId(),
                        OwnerId = manufacturer,
                        LocationId = FactoryStation,
                        TypeId = CapsuleerMinerTwoRules.InventedBlueprint,
                        Quantity = 1,
                        Singleton = true,
                        BlueprintRuns = CapsuleerMinerTwoRules.BatchSize,
                        FlagId = (int)InventoryFlag.Hangar,
                    }
                );
                var final = IndustryMaterials.Calculate(
                    recipes[CapsuleerMinerTwoRules.InventedBlueprint].Materials,
                    CapsuleerMinerTwoRules.BatchSize
                );
                quantity = final[minerOne];
                foreach (var (type, amount) in final.Where(part => part.Key is not (minerOne or morphite)))
                {
                    AddStock(setup, manufacturer, FactoryStation, type, amount);
                }
                foreach (var (type, amount) in IndustryMaterials.Calculate(recipes[original.TypeId].Materials, quantity))
                {
                    AddStock(setup, manufacturer, FactoryStation, type, amount);
                }
                AddStock(setup, manufacturer, FactoryStation, mercoxit, portion);
                setup.Set<PilotWallet>().Find(manufacturer)!.Cents = corporationFunds;
                setup.Commit();
            }

            Produce(host);
            live.Economy.Inspect(world =>
            {
                Assert.Equal(0, Stock(world, manufacturer, FactoryStation, mercoxit));
                Assert.True(Stock(world, manufacturer, FactoryStation, morphite) >= CapsuleerMinerTwoRules.BatchSize);
                Assert.Empty(world.Set<IndustryJob>());
                Assert.Empty(world.Set<NpcMarketOrder>().Where(order => order.TypeId == morphite && !order.Bid));
                return true;
            });
            Produce(host);
            Produce(host);
            (componentJob, componentEnd) = live.Economy.Read(world =>
            {
                var job = Assert.Single(world.Set<IndustryJob>());
                Assert.Equal(minerOne, job.ProductTypeId);
                Assert.Equal(IndustryJobStatus.Installed, job.Status);
                Assert.Equal(quantity, job.OutputQuantity);
                Assert.Equal((int)InventoryFlag.Hangar, job.OutputFlagId);
                Assert.Equal(0, Stock(world, manufacturer, FactoryStation, minerOne));
                Assert.Empty(world.Set<NpcMarketOrder>().Where(order => order.TypeId == minerOne && !order.Bid));
                Assert.Empty(world.Set<SavedPersonalOrder>().Where(order => order.TypeId == minerOne));
                return (job.Id, job.EndTime);
            });
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(componentEnd, restored.Economy.Read(world => world.Set<IndustryJob>().Find(componentJob)!.EndTime));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(componentEnd - fixture.Clock.Now + TimeSpan.TicksPerSecond));
        Produce(reopened);
        restored.Economy.Inspect(world =>
        {
            var materialJob = world.Set<IndustryJob>().Find(componentJob)!;
            Assert.Equal(IndustryJobStatus.Delivered, materialJob.Status);
            var assembly = Assert.Single(world.Set<IndustryJob>(), job => job.ProductTypeId == CapsuleerMinerTwoRules.Module);
            Assert.Equal(IndustryJobStatus.Installed, assembly.Status);
            Assert.Equal(0, Stock(world, manufacturer, FactoryStation, minerOne));
            Assert.Equal(0, Stock(world, corporation, FactoryStation, minerOne));
            Assert.Empty(world.Set<SavedPersonalOrder>().Where(order => order.TypeId == minerOne));
            var payments = world.Filter<SavedWalletLedger>(entry => entry.OwnerId == corporation).ToArray();
            Assert.Single(payments, entry => entry.EntryType == WalletEntryType.Manufacturing && entry.ReferenceId == componentJob);
            Assert.Equal(
                corporationFunds - materialJob.CostCents - assembly.CostCents,
                world.Set<SavedCorporation>().Find(corporation)!.BalanceCents
            );
            return true;
        });
        await restored.FlushAsync();

        void Produce(ServiceProvider host)
        {
            using var world = host.GetRequiredService<LiveWorld>().Economy.Begin();
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(world, -1, manufacturer)!;
            host.GetRequiredService<CapsuleerIndustry>().Produce(world, catalog, pilot, fixture.Clock.Now);
            world.Commit();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void VentureLaunchesForMercoxitOnlyWhenThereIsOreAndRoom(bool depleted, bool busy)
    {
        const int beltSystem = 30000144;
        const int busySystem = 30000142;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadMining(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        PrepareMinerTwoCatalog(fixture);
        PrepareMercoxitRoute(fixture);
        if (busy)
        {
            using CatalogDbContext edit = new(fixture.Database.CatalogPath, writable: true);
            edit.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/systems','30000142','{{"securityStatus":-0.8}}'),
                    ('client/solarsystemcontent','30000142','{{"security":-0.8}}'),
                    ('client/asteroidcountbytypeidbeltid','40009982','{{"11396":8}}');
                """
            );
        }
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var routes = CapsuleerTrafficRoutes.MercoxitRoutes(catalog.Definitions)[Home];
        Assert.Equal(busy ? [busySystem, beltSystem] : [beltSystem], routes.Select(route => route[^1]));
        long extractor;
        using (var setup = live.Economy.Begin())
        {
            var agents = setup.Set<SavedCapsuleerAgent>().ToArray();
            extractor = agents
                .Single(agent => agent.Career == CapsuleerCareer.Mining && agent.CommunitySystemId == Home && agent.Slot == 0)
                .PilotId;
            setup.Filter<SavedCapsuleerCommunity>(row => row.PilotId == extractor).Single().Progress = null;
            foreach (var agent in agents.Where(agent => agent.PilotId != extractor))
            {
                setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
            }
            AsteroidFieldStore.Populate(setup, catalog, beltSystem, fixture.Clock.Now);
            var deposits = setup.Filter<WorldAsteroid>(rock => rock.SystemId == beltSystem).ToArray();
            Assert.Contains(
                deposits,
                rock => rock.Quantity > 0 && catalog.FittingType(rock.TypeId)?.GroupId == MiningDefinitions.MercoxitGroup
            );
            if (depleted)
            {
                foreach (var rock in deposits)
                {
                    rock.Quantity = 0;
                }
            }
            setup.Commit();
        }
        if (busy)
        {
            using var owner = live.EnterShared("reserve the closer test deposit");
            Assert.True(live.MercoxitDepartureAvailable(extractor, busySystem));
            live.PublishMercoxitExpedition(long.MaxValue, new(Depot, [Home, busySystem]));
            live.PublishMercoxitExpedition(long.MaxValue - 1, new(Depot, [Home, busySystem], Returning: true));
            Assert.False(live.MercoxitDepartureAvailable(extractor, busySystem));
        }

        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var presence = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        scheduler.PopulationPrepared(Home);
        var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
        for (int step = 0; step < 90; step++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
            if (
                live.MinerCheckpoints.GetValueOrDefault(extractor)?.Work is { } saved
                && (saved.Trips > 0 || saved.Activity == MinerActivity.Waiting)
            )
            {
                break;
            }
        }
        var work = live.MinerCheckpoints[extractor].Work;
        Assert.True(work.Trips > 0 || work.Activity == MinerActivity.Waiting);
        Assert.True(
            (work.Mercoxit is not null) == !depleted,
            $"Unexpected Mercoxit route from {scheduler.Miner(extractor)?.Pilot.SystemId} in activity {work.Activity}: {work.WaitingReason}"
        );
        Assert.Equal(!depleted, work.Activity == MinerActivity.FlyingToGate);
        Assert.Equal(depleted ? null : (int?)beltSystem, work.Mercoxit?.Systems[^1]);
        if (busy)
        {
            using var owner = live.EnterShared("release a returned test miner");
            Assert.False(live.MercoxitDepartureAvailable(extractor, busySystem));
            live.PublishMercoxitExpedition(long.MaxValue - 1, null);
            Assert.True(live.MercoxitDepartureAvailable(extractor, busySystem));
        }
        Assert.Equal(0, scheduler.Read().Failures);
    }

    [Fact]
    public async Task BotsMineAndHaulMercoxitToTheMinerTwoFactoryAcrossRestarts()
    {
        const int factorySystem = 30000142;
        const int beltSystem = 30000144;
        const int mercoxit = 11396;
        const int morphite = CapsuleerIndustryRules.Morphite;
        const int portion = 100;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadMining(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        PrepareMinerTwoCatalog(fixture);
        PrepareMercoxitRoute(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock))
        {
            provisioner.EnsureSystem(factorySystem);
        }
        using (CatalogDbContext writable = new(fixture.Database.CatalogPath, writable: true))
        {
            // Keep the factory operator docked to process shipments as soon as they arrive.
            writable.Database.ExecuteSql(
                $"DELETE FROM Celestial WHERE SolarSystemId={factorySystem} AND GroupId={AsteroidDefinitions.BeltGroup}"
            );
        }
        long extractor;
        long manufacturer;
        long courier;
        int corporation;
        long cargoId;
        long orePrice;
        long asteroidId;
        long startingOre;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin())
            {
                var economy = setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!;
                economy.TestingMarket = false;
                economy.TestingGrants = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                var worker = agents.Single(agent =>
                    agent.Career == CapsuleerCareer.Mining && agent.CommunitySystemId == Home && agent.Slot == 0
                );
                extractor = worker.PilotId;
                var factory = agents.First(agent => agent.Career == CapsuleerCareer.Mining && agent.CommunitySystemId == factorySystem);
                factory.Slot = 10;
                manufacturer = factory.PilotId;
                courier = agents.First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                foreach (
                    var agent in agents.Where(agent =>
                        agent.PilotId != extractor && agent.PilotId != manufacturer && agent.PilotId != courier
                    )
                )
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                var hauler = setup.Pilots.Find(courier)!;
                hauler.ActiveShip.StationId = Depot;
                hauler.ActiveShip.TypeId = HaulingRules.BadgerType;
                var operatorPilot = setup.Pilots.Find(manufacturer)!;
                operatorPilot.ActiveShip.StationId = FactoryStation;
                if (setup.Positions.Find(operatorPilot.ActiveShipId) is { } inSpace)
                {
                    setup.Remove(inSpace);
                }
                setup.Filter<SavedCapsuleerCommunity>(row => row.PilotId == manufacturer).Single().Progress = null;
                corporation = Charter(setup, catalog, manufacturer, "Mercoxit Manufacturing", "MERX", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, hauler, corporation, fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = 100_000_000;
                setup.Set<PilotWallet>().Find(manufacturer)!.Cents = 100_000_000;
                SetMinerTwoAdjustedPrices(setup, catalog);
                setup.Commit();
            }
            Assert.Equal(new[] { Home, beltSystem }, Assert.Single(CapsuleerTrafficRoutes.MercoxitRoutes(catalog.Definitions)[Home]));
            using (var presence = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
            {
                Advance(
                    host,
                    () =>
                        live.MinerCheckpoints.GetValueOrDefault(extractor)?.Work is { Mercoxit: not null, Activity: MinerActivity.Mining },
                    600
                );
                asteroidId = live.MinerCheckpoints[extractor].Work.TargetId;
                startingOre = live.Economy.Read(world => world.Set<WorldAsteroid>().Find(asteroidId)!.Quantity);
                live.Economy.Inspect(world =>
                {
                    Assert.Equal(beltSystem, world.Positions.Find(world.Pilots.Find(extractor)!.ActiveShipId)!.SystemId);
                    Assert.Empty(world.Set<NpcMarketOrder>().Where(order => order.TypeId == mercoxit || order.TypeId == morphite));
                    Assert.Empty(world.Set<SavedPersonalOrder>().Where(order => order.TypeId == mercoxit || order.TypeId == morphite));
                    return true;
                });
            }
            await live.FlushAsync();
        }

        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            live.Economy.Warm();
            live.WarmMercoxitExpeditions();
            using (live.EnterShared("verify saved Mercoxit reservation"))
            {
                Assert.True(live.MercoxitDepartureAvailable(manufacturer, beltSystem));
                live.PublishMercoxitExpedition(manufacturer, new(FactoryStation, [factorySystem, beltSystem]));
                Assert.False(live.MercoxitDepartureAvailable(courier, beltSystem));
                live.PublishMercoxitExpedition(manufacturer, null);
            }
            using (var presence = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
            {
                Advance(
                    host,
                    () =>
                        live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work
                            is { CorporationSupply.FactorySupply.Mercoxit: true, Freight.Stacks.Length: > 0 },
                    6500
                );
                var freight = live.HaulerCheckpoints[courier].Work.Freight!;
                var cargo = Assert.Single(freight.Stacks);
                Assert.Equal(mercoxit, cargo.TypeId);
                Assert.Equal(portion / 2, cargo.Quantity);
                Assert.Equal(FactoryStation, freight.Destination);
                cargoId = cargo.ItemId;
                orePrice = live.Economy.Read(world =>
                {
                    var ownSale = Assert.Single(world.Set<SavedPersonalOrder>().Where(order => order.TypeId == mercoxit));
                    Assert.Equal(extractor, ownSale.PilotId);
                    Assert.Equal(portion - cargo.Quantity, ownSale.Remaining);
                    Assert.True((world.Set<WorldAsteroid>().Find(asteroidId)?.Quantity ?? 0) < startingOre);
                    Assert.Single(
                        world
                            .Filter<OwnedItem>(item => item.OwnerId == extractor)
                            .Where(item => item.TypeId == (int)MiningLaserType.DeepCoreMiningLaserI)
                    );
                    Assert.Equal((int)InventoryFlag.Cargo, world.Set<OwnedItem>().Find(cargoId)!.FlagId);
                    return ownSale.PriceCents;
                });
            }
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using (var presence = reopened.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
        {
            Assert.NotNull(restored.Economy.Read(world => world.Set<OwnedItem>().Find(cargoId)));
            Advance(reopened, () => restored.Economy.Read(world => Stock(world, manufacturer, FactoryStation, morphite) > 0), 2000);
            restored.Economy.Inspect(world =>
            {
                Assert.Null(world.Set<OwnedItem>().Find(cargoId));
                Assert.Equal(0, Stock(world, manufacturer, FactoryStation, mercoxit));
                var payments = world
                    .Filter<SavedWalletLedger>(entry => entry.OwnerId == corporation)
                    .Where(entry => entry.EntryType == WalletEntryType.MarketTransaction && entry.AmountCents < 0)
                    .ToArray();
                Assert.Equal(2, payments.Length);
                Assert.Equal(-portion * orePrice, payments.Sum(payment => payment.AmountCents));
                return true;
            });
        }
        await restored.FlushAsync();

        void Advance(ServiceProvider host, Func<bool> ready, int seconds)
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(factorySystem);
            scheduler.PopulationPrepared(Home);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (int step = 0; step < seconds && !ready(); step++)
            {
                fixture.Clock.Seconds++;
                worker.Tick();
            }
            if (!ready())
            {
                var mining = scheduler.Miner(extractor);
                var hauling = scheduler.Hauler(courier);
                var factory = scheduler.Miner(manufacturer);
                var status = host.GetRequiredService<LiveWorld>()
                    .Economy.Read(world =>
                    {
                        long raw = Stock(world, manufacturer, FactoryStation, mercoxit);
                        long refined = Stock(world, manufacturer, FactoryStation, morphite);
                        string orders = string.Join(
                            ',',
                            world
                                .Set<SavedPersonalOrder>()
                                .Where(order => order.TypeId == mercoxit)
                                .Select(order => order.Remaining)
                                .ToArray()
                        );
                        string items = string.Join(
                            ',',
                            world
                                .Set<OwnedItem>()
                                .Where(item => item.TypeId == mercoxit)
                                .AsEnumerable()
                                .Select(item => $"{item.Id}/{item.OwnerId}/{item.LocationId}/{item.FlagId}/{item.Quantity}")
                        );
                        string payments = string.Join(
                            ',',
                            world
                                .Filter<SavedWalletLedger>(entry => entry.OwnerId == corporation)
                                .Where(entry => entry.EntryType == WalletEntryType.MarketTransaction)
                                .Select(entry => entry.AmountCents)
                                .ToArray()
                        );
                        return $"Raw {raw}, refined {refined}, listed {orders}, items {items}, payments {payments}";
                    });
                Assert.Fail(
                    $"Miner {mining?.Work.Activity} in {mining?.Pilot.SystemId}: {mining?.WaitingReason}. "
                        + $"Hauler {hauling?.Work.Activity} in {hauling?.Pilot.SystemId}. Factory {factory?.Work.Activity} "
                        + $"request {factory?.Work.FactorySupply?.Mercoxit}: {factory?.WaitingReason}. "
                        + $"{status}. {scheduler.Read().LastFailure}"
                );
            }
        }
    }

    private static void PrepareMercoxitRoute(CapsuleerHaulerTests.Fixture fixture)
    {
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR REPLACE INTO SourceRecord VALUES
                ('client/stationoperations','99','{{"services":[7]}}'),
                ('client/systems','30000144','{{"securityStatus":-0.8}}'),
                ('client/solarsystemcontent','30000144','{{"security":-0.8}}'),
                ('client/types','11396','{{"published":1,"marketGroupID":530,"volume":40,"portionSize":100,"basePrice":17367040}}'),
                ('client/asteroidcountbytypeidbeltid','40009981','{{"11396":8}}'),
                ('client/asteroidcountbytypeidbeltid','40009982','{{"1230":12}}');
            UPDATE Station SET OperationId=99 WHERE Id=60015157;
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                (50009993,30000141,16,10,3532,800000000000,-120000000000,-900000000000),
                (50009994,30000144,16,10,3532,800000000000,-120000000000,-900000000000),
                (40009981,30000144,15,9,0,620000000000,-40000000000,-800000000000),
                (40009982,30000142,15,9,0,620000000000,-40000000000,-800000000000);
            INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                (50009993,30000141,50009994),(50009994,30000144,50009993);
            """
        );
    }

    [Theory]
    [InlineData(3689, true)] // Mechanical Parts from raw extraction.
    [InlineData(2867, false)] // Broadcast Node from supplied P3 intermediates.
    public async Task BotsProduceAndHaulPlanetaryInputsAcrossRestarts(int parts, bool extract)
    {
        const int factorySystem = 30000142;
        const int planet = 45000997;
        const long funds = 10_000_000_000;
        int batchParts = CapsuleerMinerTwoRules.BatchSize * (extract ? 8 : 1);
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadPlanetary(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        PrepareMinerTwoCatalog(fixture);
        using (CatalogDbContext edit = new(fixture.Database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemGroup VALUES(7,'Planet',2);
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z)
                    VALUES(45000997,30000141,2016,7,6000000,620000000000,-40000000000,-800000000000);
                INSERT INTO SourceRecord VALUES('sde/mapPlanets','45000997','{{"celestialIndex":1,"radius":6000000,"typeID":2016}}');
                """
            );
            if (!extract)
            {
                // Exercise the same manufacturing shortfall with a P4 colony product as its input.
                edit.Database.ExecuteSql(
                    $"""
                    UPDATE SourceRecord SET Data=replace(Data, {"\"typeID\":3689,\"quantity\":8"}, {$"\"typeID\":{parts},\"quantity\":1"})
                    WHERE Dataset='client/blueprints' AND Key='"784"'
                    """
                );
            }
        }
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long manufacturer;
        long courier;
        int corporation;
        long firstPin;
        long firstDeadline;
        long firstCycle;
        long cargoId;
        long paidBeforeDelivery;
        int schematicId;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            var schematic = PlanetaryCatalogReader.Read(catalog).Schematics.Values.Single(recipe => recipe.Output.TypeId == parts);
            schematicId = schematic.Id;
            using (var setup = live.Economy.Begin())
            {
                var economy = setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!;
                economy.TestingMarket = false;
                economy.TestingGrants = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                manufacturer = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                courier = agents.First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                foreach (var agent in agents.Where(agent => agent.PilotId != manufacturer && agent.PilotId != courier))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                var hauler = setup.Pilots.Find(courier)!;
                hauler.ActiveShip.StationId = FactoryStation;
                hauler.ActiveShip.TypeId = HaulingRules.BadgerType;
                setup.Pilots.Find(manufacturer)!.ActiveShip.StationId = FactoryStation;
                corporation = Charter(setup, catalog, manufacturer, "Colony Manufacturing", "COLY", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, hauler, corporation, fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = funds;
                SetMinerTwoAdjustedPrices(setup, catalog);
                setup.Add(
                    new OwnedItem
                    {
                        Id = setup.NextItemId(),
                        OwnerId = manufacturer,
                        LocationId = FactoryStation,
                        TypeId = CapsuleerMinerTwoRules.InventedBlueprint,
                        Quantity = 1,
                        Singleton = true,
                        BlueprintRuns = CapsuleerMinerTwoRules.BatchSize,
                        FlagId = (int)InventoryFlag.Hangar,
                    }
                );
                var recipe = ManufacturingCatalog.Recipes(catalog)[CapsuleerMinerTwoRules.InventedBlueprint];
                foreach (
                    var (type, amount) in IndustryMaterials
                        .Calculate(recipe.Materials, CapsuleerMinerTwoRules.BatchSize)
                        .Where(part => part.Key != parts)
                )
                {
                    AddStock(setup, manufacturer, FactoryStation, type, amount);
                }
                if (!extract)
                {
                    foreach (var input in schematic.Inputs)
                    {
                        AddStock(setup, courier, FactoryStation, input.TypeId, input.Quantity * batchParts);
                    }
                }
                setup.Commit();
            }
            using (var presence = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
            {
                Advance(host, () => live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.PlanetaryProduction is not null, false, 1800);
                var job = live.HaulerCheckpoints[courier].Work.PlanetaryProduction!;
                if (extract)
                {
                    Assert.Contains(job.Step.TypeId, new[] { PlanetaryResources.BaseMetals, PlanetaryResources.NobleMetals });
                }
                else
                {
                    Assert.Equal(parts, job.Step.TypeId);
                    Assert.Equal(schematicId, job.Step.SchematicId);
                }
                Assert.Equal(planet, job.PlanetId);
                firstDeadline = job.ReadyAt;
                (firstPin, firstCycle) = live.Economy.Read(world =>
                {
                    var machine = Colony(world).Pins.Single(pin => pin.TypeId == job.MachineType);
                    return (machine.Id, machine.NextRunAt!.Value);
                });
                live.Economy.Inspect(world =>
                {
                    Assert.Single(Payments(world), entry => entry.EntryType == WalletEntryType.PlanetaryConstruction);
                    Assert.Empty(world.Set<NpcMarketOrder>().Where(order => order.TypeId == parts));
                    Assert.Empty(world.Set<SavedPersonalOrder>().Where(order => order.TypeId == parts));
                    return true;
                });
            }
            await live.FlushAsync();
        }

        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (var presence = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
            {
                Assert.Equal(firstCycle, live.Economy.Read(world => Colony(world).Pins.Single(pin => pin.Id == firstPin).NextRunAt));
                var saved = live.Economy.Read(world =>
                    world.Filter<SavedCapsuleerHauler>(hauler => hauler.PilotId == courier).Single().Progress
                );
                Assert.Equal(firstDeadline, HaulerCheckpoint.Read(saved)?.PlanetaryProduction?.ReadyAt);
                Advance(
                    host,
                    () =>
                        live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.Freight?.Stacks.Any(stack => stack.TypeId == parts) == true,
                    true,
                    12000
                );
                var freight = live.HaulerCheckpoints[courier].Work.Freight!;
                var cargo = Assert.Single(freight.Stacks);
                Assert.True(cargo.Quantity >= batchParts);
                cargoId = cargo.ItemId;
                paidBeforeDelivery = live.Economy.Read(world =>
                    Payments(world).Where(entry => entry.EntryType == WalletEntryType.PlanetaryExportTax).Sum(entry => entry.AmountCents)
                );
                Assert.True(paidBeforeDelivery < 0);
            }
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        using (var presence = reopened.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
        {
            Assert.NotNull(restored.Economy.Read(world => world.Set<OwnedItem>().Find(cargoId)));
            Advance(
                reopened,
                () =>
                    restored.Economy.Read(world =>
                        world
                            .Set<IndustryJob>()
                            .Any(job => job.ProductTypeId == CapsuleerMinerTwoRules.Module && job.OutputCorporationId == corporation)
                    ),
                false,
                2000
            );
            restored.Economy.Inspect(world =>
            {
                Assert.Null(world.Set<OwnedItem>().Find(cargoId));
                Assert.Equal(
                    paidBeforeDelivery,
                    Payments(world).Where(entry => entry.EntryType == WalletEntryType.PlanetaryExportTax).Sum(entry => entry.AmountCents)
                );
                Assert.Single(Payments(world), entry => entry.EntryType == WalletEntryType.MarketTransaction);
                Assert.Contains(Payments(world), entry => entry.EntryType == WalletEntryType.PlanetaryImportTax);
                var colony = Colony(world);
                Assert.Contains(colony.Pins, pin => pin.SchematicId == schematicId);
                Assert.Contains(colony.Pins, pin => pin.Id == firstPin);
                return true;
            });
        }
        await restored.FlushAsync();

        PlanetaryColony Colony(WorldAction world) =>
            PilotPlanetaryIndustryStore.ReadState(world.Set<SavedPlanetaryColony>().Find(courier, planet)!);

        SavedWalletLedger[] Payments(WorldAction world) => world.Filter<SavedWalletLedger>(entry => entry.OwnerId == corporation).ToArray();

        void Advance(ServiceProvider host, Func<bool> ready, bool advanceCycles, int seconds)
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(factorySystem);
            scheduler.PopulationPrepared(Home);
            var live = host.GetRequiredService<LiveWorld>();
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (int step = 0; step < seconds && !ready(); step++)
            {
                var work = live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work;
                if (advanceCycles && work?.PlanetaryProduction is { } job && job.ReadyAt > fixture.Clock.Now)
                {
                    fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(job.ReadyAt - fixture.Clock.Now));
                }
                fixture.Clock.Seconds++;
                worker.Tick();
            }
            var last = scheduler.Hauler(courier);
            var visit = last?.Work.CorporationSupply?.FactorySupply?.Planetary;
            Assert.True(
                ready(),
                $"Planetary work stopped at {last?.Work.Activity} in {last?.Pilot.SystemId}, "
                    + $"{visit?.Phase}, product {visit?.Production.Step.TypeId}: {last?.Work.WaitingReason}. {scheduler.Read().LastFailure}"
            );
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(100)]
    public async Task BotsBuildAndCommissionTheirMoonRefineryAcrossRestarts(int? freightSurvival)
    {
        const int factorySystem = 30000142;
        const int caldariHauler = 3342;
        const int spaceshipCommand = 3327;
        const int moon = 40000991;
        const int athanor = CapsuleerIndustry.AthanorType;
        const int core = 56202;
        const int constructionPart = 21947;
        const int fuel = 4051;
        const int tayra = (int)FreightHull.Tayra;
        const long replacementPrice = 10_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        PrepareMinerTwoCatalog(fixture);
        PassiveCatalogFixture.LoadStructures(fixture.Database.CatalogPath);
        PassiveCatalogFixture.LoadUtilityHulls(
            fixture.Database.CatalogPath,
            new HashSet<int> { (int)FreightHull.Tayra, caldariHauler, spaceshipCommand }
        );
        PrepareRefineryCatalog(fixture);
        UseLocalOrders(fixture, freightSurvival);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long factory;
        long hauler;
        int corporation;
        long hullJob;
        long hullMaturity;
        long cargoId;
        long lostCourier = 0;
        long escapeCapsule = 0;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin())
            {
                var economy = setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!;
                economy.TestingMarket = false;
                economy.TestingGrants = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                factory = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                hauler = agents.First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                foreach (var agent in agents.Where(agent => agent.PilotId != factory && agent.PilotId != hauler))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                var courier = setup.Pilots.Find(hauler)!;
                courier.ActiveShip.StationId = FactoryStation;
                courier.ActiveShip.TypeId = tayra;
                setup.Pilots.Find(factory)!.ActiveShip.StationId = FactoryStation;
                corporation = Charter(setup, catalog, factory, "Refinery Works", "REFW", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, courier, corporation, fixture.Clock.Now);
                var employer = setup.Set<SavedCorporation>().Find(corporation)!;
                employer.CreatedAt = fixture.Clock.Now - TimeSpan.FromDays(60).Ticks;
                employer.BalanceCents = 100_000_000_000;
                SetMinerTwoAdjustedPrices(setup, catalog);
                if (setup.Set<SavedAdjustedPrice>().Find(constructionPart) is null)
                {
                    setup.Add(new SavedAdjustedPrice { TypeId = constructionPart, PriceIsk = 1000 });
                }
                AddStock(setup, factory, FactoryStation, 34, 500);
                AddStock(setup, factory, FactoryStation, core, 1);
                AddStock(setup, factory, FactoryStation, fuel, 6000);
                foreach (int blueprint in new[] { 21948, 36977, 37034, 37032, 45540 })
                {
                    setup.Add(
                        new OwnedItem
                        {
                            Id = setup.NextItemId(),
                            OwnerId = factory,
                            LocationId = FactoryStation,
                            TypeId = blueprint,
                            Quantity = 1,
                            BlueprintRuns = -1,
                            Singleton = true,
                            FlagId = (int)InventoryFlag.Hangar,
                        }
                    );
                }
                setup.Commit();
            }
            using (var presence = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
            {
                Advance(host, () => live.Economy.Read(world => world.Set<IndustryJob>().Any(job => job.ProductTypeId == athanor)), 1400);
                (hullJob, hullMaturity) = live.Economy.Read(world =>
                {
                    var job = Assert.Single(world.Set<IndustryJob>(), job => job.ProductTypeId == athanor);
                    Assert.Equal(IndustryJobStatus.Installed, job.Status);
                    Assert.Single(
                        world.Set<IndustryJob>(),
                        job => job.ProductTypeId == constructionPart && job.Status == IndustryJobStatus.Delivered
                    );
                    Assert.Empty(world.Set<SavedStructure>());
                    Assert.Empty(world.Set<NpcMarketOrder>().Where(order => order.TypeId == athanor));
                    return (job.Id, job.EndTime);
                });
            }
            await live.FlushAsync();
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(hullMaturity - fixture.Clock.Now + TimeSpan.TicksPerSecond));
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(hullMaturity, live.Economy.Read(world => world.Set<IndustryJob>().Find(hullJob)!.EndTime));
            using (var presence = host.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
            {
                Advance(
                    host,
                    () =>
                        live.HaulerCheckpoints.GetValueOrDefault(hauler)?.Work.CorporationSupply?.FactorySupply?.Refinery
                            is { Task.Work: RefineryWork.Deploy, Phase: RefineryVisitPhase.OnGrid, Cargo: not null },
                    1600
                );
                cargoId = live.HaulerCheckpoints[hauler].Work.CorporationSupply!.FactorySupply!.Refinery!.Cargo!.ItemId;
                Assert.True(live.Economy.Read(world => world.Set<OwnedItem>().Find(cargoId) is { TypeId: athanor, Quantity: 1 }));
                if (freightSurvival.HasValue)
                {
                    using var loss = live.Economy.Begin();
                    using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
                    var pilot = loss.Pilots.Find(hauler)!;
                    lostCourier = pilot.ActiveShipId;
                    var cargo = loss.Set<OwnedItem>().Find(cargoId)!;
                    Assert.Equal((hauler, lostCourier, (int)InventoryFlag.Cargo), (cargo.OwnerId, cargo.LocationId, cargo.FlagId));
                    escapeCapsule = DestroyBotShip(loss, catalog, hauler, fixture.Clock.Now);
                    loss.Commit();
                }
            }
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        long structureId;
        using (var presence = reopened.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem))
        {
            if (freightSurvival is int survival)
            {
                restored.Economy.Inspect(world =>
                {
                    Assert.Equal(escapeCapsule, world.Pilots.Find(hauler)!.ActiveShipId);
                    Assert.Null(world.Ships.Find(lostCourier));
                    Assert.Empty(world.Set<SavedStructure>());
                    var report = world.Set<ShipLoss>().Find(lostCourier)!;
                    var original = Assert.Single(JsonSerializer.Deserialize<OwnedItem[]>(report.ItemsJson)!, item => item.Id == cargoId);
                    Assert.Equal((athanor, 1, lostCourier), (original.TypeId, original.Quantity, original.LocationId));
                    var wreck = Assert.Single(world.Set<WorldWreck>(), item => item.ShipId == lostCourier);
                    Assert.Equal((hauler, corporation), (wreck.OwnerId, wreck.CorporationId));
                    Assert.Equal(survival == 100, JsonSerializer.Deserialize<long[]>(report.DroppedItemIdsJson)!.Contains(cargoId));
                    var debris = world.Set<OwnedItem>().Find(cargoId);
                    if (survival == 100)
                    {
                        Assert.NotNull(debris);
                        Assert.Equal(
                            (athanor, 1, wreck.Id, (int)InventoryFlag.Cargo),
                            (debris.TypeId, debris.Quantity, debris.LocationId, debris.FlagId)
                        );
                    }
                    else
                    {
                        Assert.Null(debris);
                    }
                    Assert.Equal(0, Stock(world, factory, FactoryStation, athanor));
                    return true;
                });

                using (var rebuild = restored.Economy.Begin())
                using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
                {
                    // The factory can act before the destroyed courier's checkpoint has been replaced by a capsule job.
                    var stale = restored.HaulerCheckpoints[hauler].Work;
                    Assert.Equal(lostCourier, stale.ShipId);
                    Assert.Equal(cargoId, stale.CorporationSupply?.FactorySupply?.Refinery?.Cargo?.ItemId);
                    var ships = reopened.GetRequiredService<SqliteWorld>();
                    var owner = ships.FindPilot(rebuild, -1, factory)!;
                    if (owner.Position is not null || owner.StationId != FactoryStation)
                    {
                        var station = SystemObjects.Geometry(catalog, factorySystem).Single(body => body.Id == FactoryStation);
                        SqliteWorld.SetPosition(rebuild, catalog, owner.ShipId, station.Position, factorySystem, fixture.Clock.Now);
                        owner = ships.Dock(rebuild, catalog, factory, owner.ShipId, FactoryStation);
                    }
                    reopened.GetRequiredService<CapsuleerIndustry>().Produce(rebuild, catalog, owner, fixture.Clock.Now);
                    Assert.Single(
                        rebuild.Set<IndustryJob>(),
                        job => job.ProductTypeId == constructionPart && job.Status == IndustryJobStatus.Installed
                    );
                    rebuild.Commit();
                }

                var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
                Advance(
                    reopened,
                    () => scheduler.Hauler(hauler)?.Pilot is { Position: null, ShipId: var ship } && ship == escapeCapsule,
                    1000
                );
                int replacementStation = scheduler.Hauler(hauler)!.Pilot.StationId;
                MarketSale offer;
                using (var stock = restored.Economy.Begin())
                {
                    stock.Pilots.Find(Customer)!.ActiveShip.StationId = replacementStation;
                    if (stock.Set<PilotWallet>().Find(Customer) is not { } wallet)
                    {
                        wallet = stock.Add(new PilotWallet { PilotId = Customer }).Entity;
                    }
                    wallet.Cents += PersonalMarketRules.MinimumBrokerFeeCents;
                    AddStock(stock, Customer, replacementStation, tayra, 1);
                    long package = stock.Filter<OwnedItem>(item => item.OwnerId == Customer).Single(item => item.TypeId == tayra).Id;
                    offer = new(package, replacementStation, tayra, replacementPrice, 1);
                    stock.Commit();
                }
                reopened.GetRequiredService<IPersonalMarket>().PlaceSell(Customer, [offer], 7);
                Advance(reopened, () => scheduler.Hauler(hauler)?.Pilot.ShipTypeId == tayra, 1000);
                long replacement = scheduler.Hauler(hauler)!.Pilot.ShipId;
                Assert.NotEqual(lostCourier, replacement);
                Assert.Null(scheduler.Hauler(hauler)!.Work.CorporationSupply);
                restored.Economy.Inspect(world =>
                {
                    Assert.Null(world.Ships.Find(escapeCapsule));
                    var trade = Assert.Single(
                        world.Filter<MarketExecution>(entry => entry.PilotId == hauler),
                        entry => entry.IsBuy && entry.TypeId == tayra
                    );
                    Assert.Equal((replacementPrice, 1), (trade.PriceCents, trade.Quantity));
                    Assert.Empty(world.Filter<OwnedItem>(item => item.LocationId == replacement).Where(item => item.TypeId == athanor));
                    return true;
                });
                Advance(
                    reopened,
                    () =>
                        restored.HaulerCheckpoints[hauler].Work.CorporationSupply?.FactorySupply?.Refinery
                            is { Task.Work: RefineryWork.Deploy, Phase: RefineryVisitPhase.OnGrid, Cargo: not null },
                    3000
                );
                var newCargo = restored.HaulerCheckpoints[hauler].Work.CorporationSupply!.FactorySupply!.Refinery!.Cargo!;
                Assert.Equal((athanor, 1), (newCargo.TypeId, newCargo.Quantity));
                Assert.NotEqual(cargoId, newCargo.ItemId);
                cargoId = newCargo.ItemId;
            }
            Advance(reopened, () => restored.Economy.Read(world => world.Set<SavedStructure>().Any()), 500);
            structureId = restored.Economy.Read(world =>
            {
                var structure = Assert.Single(world.Set<SavedStructure>());
                Assert.Equal(StructureState.Anchoring, structure.State);
                Assert.Equal(moon, structure.MoonId);
                Assert.Equal(corporation, structure.CorporationId);
                Assert.Null(world.Set<OwnedItem>().Find(cargoId));
                return structure.Id;
            });
            Advance(reopened, () => restored.Economy.Read(world => world.Set<SavedMoonExtraction>().Any()), 12000, advanceAnchoring: true);
            restored.Economy.Inspect(world =>
            {
                var structure = Assert.Single(world.Set<SavedStructure>());
                Assert.Equal(structureId, structure.Id);
                Assert.True(structure.HasCore);
                var modules = world.Filter<SavedStructureService>(module => module.StructureId == structure.Id).ToArray();
                Assert.Equal(3, modules.Length);
                Assert.All(modules, module => Assert.True(module.Online));
                var extraction = Assert.Single(world.Filter<SavedMoonExtraction>(job => job.StructureId == structure.Id));
                Assert.Equal(MoonMiningRules.MinimumExtractionSeconds * TimeSpan.TicksPerSecond, extraction.ReadyAt - extraction.StartedAt);
                var jobs = world.Set<IndustryJob>().ToArray();
                Assert.Equal(freightSurvival.HasValue ? 7 : 5, jobs.Length);
                var payments = world
                    .Filter<SavedWalletLedger>(entry => entry.OwnerId == corporation && entry.EntryType == WalletEntryType.Manufacturing)
                    .ToArray();
                Assert.Single(payments, entry => entry.ReferenceId == hullJob);
                Assert.All(
                    jobs,
                    job =>
                    {
                        Assert.Equal(IndustryJobStatus.Delivered, job.Status);
                        Assert.Single(payments, entry => entry.ReferenceId == job.Id);
                    }
                );
                Assert.Empty(world.Set<SavedPersonalOrder>().Where(order => order.TypeId == athanor));
                return true;
            });
        }
        await restored.FlushAsync();
        long fractureAt = restored.Economy.Read(world => world.Set<SavedMoonExtraction>().Single().AutoFractureAt);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(fractureAt - fixture.Clock.Now));
        var structures = reopened.GetRequiredService<IPilotStructures>();
        structures.Advance();
        var rocks = restored.Economy.Read(world => world.Filter<WorldAsteroid>(rock => rock.BeltId == moon).Count());
        Assert.True(rocks > 0);
        structures.Advance();
        Assert.Equal(rocks, restored.Economy.Read(world => world.Filter<WorldAsteroid>(rock => rock.BeltId == moon).Count()));

        void Advance(ServiceProvider host, Func<bool> ready, int seconds, bool advanceAnchoring = false)
        {
            var live = host.GetRequiredService<LiveWorld>();
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(factorySystem);
            scheduler.PopulationPrepared(Home);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (int step = 0; step < seconds && !ready(); step++)
            {
                if (advanceAnchoring)
                {
                    long? deadline = live.Economy.Read(world => world.Set<SavedStructure>().Single().StateEndsAt);
                    if (deadline > fixture.Clock.Now)
                    {
                        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(deadline.Value - fixture.Clock.Now));
                    }
                }
                fixture.Clock.Seconds++;
                worker.Tick();
            }
            var courier = scheduler.Hauler(hauler);
            var task = courier?.Work.CorporationSupply?.FactorySupply?.Refinery;
            var factoryWork = scheduler.Miner(factory)?.Work;
            Assert.True(
                ready(),
                $"Refinery shipment {task?.Phase} {task?.Task.Work}, system {courier?.Pilot.SystemId}. "
                    + $"{courier?.Work.WaitingReason} {scheduler.Read().LastFailure}; factory {factoryWork?.Activity} "
                    + $"{factoryWork?.FactorySupply?.Refinery}; courier {courier?.Work.Activity}"
            );
        }
    }

    private static void RemoveUnpricedFixtureMaterials(CapsuleerHaulerTests.Fixture fixture)
    {
        var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock).Read().Active.Draft;
        var prices = configuration.AdjustedPrices.Select(price => price.TypeId).ToHashSet();
        // These fixtures extend an initialized save whose existing price schedule covers the materials they use.
        using CatalogDbContext lookup = new(fixture.Database.CatalogPath);
        int[] unpriced = lookup
            .Definitions.Items.Values.Where(item =>
                item.CategoryId == (int)InventoryCategory.Material
                && !prices.Contains(item.Id)
                && lookup.Read(CatalogData.Client.Types, (long)item.Id)?.BasePrice is > 0
            )
            .Select(item => item.Id)
            .ToArray();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        foreach (int type in unpriced)
        {
            string key = type.ToString(CultureInfo.InvariantCulture);
            catalog.Database.ExecuteSql(
                $"""
                UPDATE SourceRecord SET Data=json_remove(Data,'$.basePrice') WHERE Dataset='client/types' AND Key={key}
                """
            );
        }
    }

    private static void PrepareRefineryCatalog(CapsuleerHaulerTests.Fixture fixture)
    {
        RemoveUnpricedFixtureMaterials(fixture);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory VALUES (17,'Commodity');
            INSERT OR IGNORE INTO ItemGroup VALUES (1884,'Ubiquitous Moon Asteroids',25),(1915,'Upwell Moon Mining Beacon',2),
                (1888,'Composite Reaction Formulas',9),(429,'Advanced Moon Materials',4),(536,'Structure Components',17);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (46329,'Upwell Moon Mining Beacon',1915),
                (21947,'Structure Construction Parts',536),(16670,'Crystalline Carbonide',429),(46205,'Crystalline Carbonide Formula',1888),
                (17357,'Photon Microprocessor Blueprint',134),(21948,'Structure Construction Parts Blueprint',134),
                (36977,'Athanor Blueprint',134),(37034,'Moon Drill Blueprint',134),(37032,'Reprocessing Facility Blueprint',134),
                (45540,'Composite Reactor Blueprint',134);
            INSERT OR REPLACE INTO SourceRecord VALUES
                ('client/systems','30000141','{{"securityStatus":0.4}}'),
                ('client/types','16670','{{"published":1,"marketGroupID":1,"volume":0.1}}'),
                ('client/types','46205','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/types','21947','{{"published":1,"marketGroupID":1,"volume":10}}');
            """
        );
        const string moon = """
            {"miningBeacon":{"position":{"x":500000000000,"y":-20000000000,"z":-850000000000}}}
            """;
        string system = "{\"security\":0.4,\"planets\":{\"40000990\":{\"moons\":{\"40000991\":" + moon + "}}}}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/solarsystemcontent','30000141',{system})");
        foreach (
            var (blueprint, output, input) in new[]
            {
                (17357, 11541, 16670),
                (21948, 21947, 34),
                (36977, CapsuleerIndustry.AthanorType, 21947),
                (37034, CapsuleerIndustry.MoonDrillType, 34),
                (37032, 35899, 34),
                (45540, 45537, 34),
            }
        )
        {
            string key = blueprint.ToString(CultureInfo.InvariantCulture);
            string blueprintKey = JsonSerializer.Serialize(key);
            string recipe = JsonSerializer.Serialize(
                new
                {
                    blueprintTypeID = blueprint,
                    activities = new
                    {
                        manufacturing = new
                        {
                            time = 20,
                            products = new[] { new { typeID = output, quantity = 1 } },
                            materials = new[] { new { typeID = input, quantity = input == 34 ? 50 : 1 } },
                            skills = new[] { new { typeID = 3380, level = 1 } },
                        },
                    },
                }
            );
            const string item = """{"published":1,"volume":0.01}""";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/types',{key},{item})");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/blueprints',{blueprintKey},{recipe})");
        }
        foreach (var grade in MoonMiningRules.Composition(40000991, 0.4))
        {
            string key = grade.TypeId.ToString(CultureInfo.InvariantCulture);
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES({grade.TypeId},'Moon ore',1884)");
            const string item = """{"published":1,"volume":10}""";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/types',{key},{item})");
            string materials = "{\"materials\":[{\"materialTypeID\":16670,\"quantity\":10}]}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/typematerials',{key},{materials})");
        }
    }

    private static void SetMinerTwoAdjustedPrices(WorldAction world, CatalogDbContext catalog)
    {
        var recipes = ManufacturingCatalog.Recipes(catalog);
        var types = recipes[CapsuleerMinerTwoRules.Original]
            .Materials.Concat(recipes[CapsuleerMinerTwoRules.InventedBlueprint].Materials)
            .Select(material => (int)material.TypeId!.Value)
            .Distinct();
        foreach (int type in types)
        {
            if (world.Set<SavedAdjustedPrice>().Find(type) is null)
            {
                world.Add(new SavedAdjustedPrice { TypeId = type, PriceIsk = 1000 });
            }
        }
    }

    private static void PrepareResearchSuppliers(CapsuleerHaulerTests.Fixture fixture)
    {
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemGroup VALUES (333,'Datacores',4);
            UPDATE ItemType SET GroupId=333 WHERE Id IN (20424,20413);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3402,'Science',257),(12179,'Research Project Management',257);
            INSERT OR REPLACE INTO TypeAttribute VALUES (3402,275,1),(12179,275,8),(20424,182,11452),(20413,182,11447);
            """
        );
        foreach (var (id, skill) in new[] { (3010612, 11452), (3012669, 11447) })
        {
            string key = id.ToString(CultureInfo.InvariantCulture);
            string data = JsonSerializer.Serialize(
                new
                {
                    deleted = 0,
                    corporationID = 1000044,
                    stationID = Depot,
                    agent = new
                    {
                        agentType = (int)AgentType.Research,
                        agentLevel = 1,
                        agentDivisionID = 18,
                        agentLocateCharacter = 0,
                    },
                    skills = new[] { new { typeID = skill } },
                }
            );
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/npccharacters',{key},{data})");
            string name = JsonSerializer.Serialize(new { name = new { en = $"Research Agent {id}" } });
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO SourceRecord VALUES ('sde/npcCharacters',{key},{name})");
        }
    }

    private sealed class MinerTwoRandom : IIndustryRandom
    {
        public int Calls { get; private set; }

        public IReadOnlyList<double> Rolls(int count)
        {
            Assert.Equal(1, count);
            return Calls++ == 0 ? [0.99] : [0];
        }
    }

    private static void PrepareMinerTwoCatalog(CapsuleerHaulerTests.Fixture fixture)
    {
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        // The test factory can keep its regular operator docked while the lab jobs mature.
        catalog.Database.ExecuteSql($"DELETE FROM Celestial WHERE SolarSystemId={30000142} AND GroupId={AsteroidDefinitions.BeltGroup}");
        // Pinned Miner II recipe matches the build 3662168 production audit.
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemGroup VALUES (134,'Mining Laser Blueprint',9);
            INSERT OR IGNORE INTO ItemCategory VALUES (25,'Asteroid');
            INSERT OR IGNORE INTO ItemGroup VALUES (468,'Mercoxit',25);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (482,'Miner II',54),(784,'Miner II Blueprint',134),
                (20424,'Mechanical Engineering Datacore',18),(20413,'Laser Physics Datacore',18),
                (11399,'Morphite',18),(3689,'Mechanical Parts',18),(11541,'Photon Microprocessor',18),
                (11689,'Laser Focusing Crystals',18),(11482,'R.A.M. Energy Tech',18),
                (11447,'Laser Physics',257),(11452,'Mechanical Engineering',257),
                (23121,'Gallente Encryption Methods',257),(3443,'Trade',257),(11396,'Mercoxit',468);
            INSERT OR IGNORE INTO TypeAttribute VALUES (11447,275,5),(11452,275,5),(23121,275,5),(3443,275,1);
            INSERT OR REPLACE INTO SourceRecord VALUES
                ('client/stationoperations','14','{{"services":[5,7,14,15,17]}}'),
                ('client/systems','30000142','{{"securityStatus":0.9}}'),
                ('client/typelist','799','{{"includedTypeIDs":[23121]}}'),
                ('client/types','482','{{"published":1,"marketGroupID":1039,"volume":5,"basePrice":600000}}'),
                ('client/types','784','{{"published":1,"volume":0.01}}'),
                ('client/types','20424','{{"published":1,"marketGroupID":1,"volume":0.1}}'),
                ('client/types','20413','{{"published":1,"marketGroupID":1,"volume":0.1}}'),
                ('client/types','11399','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/types','11396','{{"published":1,"marketGroupID":530,"volume":40,"portionSize":100}}'),
                ('client/typematerials','11396','{{"materials":[{{"materialTypeID":11399,"quantity":140}}]}}'),
                ('client/types','3689','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/types','11541','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/types','11689','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/types','11482','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                ('client/blueprints','"784"','{{"blueprintTypeID":784,"activities":{{"manufacturing":{{"time":2340,
                    "skills":[{{"typeID":3380,"level":5}},{{"typeID":11447,"level":1}},{{"typeID":11452,"level":1}}],
                    "materials":[{{"typeID":11399,"quantity":1}},{{"typeID":3689,"quantity":8}},{{"typeID":483,"quantity":1}},
                        {{"typeID":11541,"quantity":5}},{{"typeID":11689,"quantity":3}},{{"typeID":11482,"quantity":1}}],
                    "products":[{{"typeID":482,"quantity":1}}]}}}}}}');
            """
        );
    }

    private static void UseLocalOrders(
        CapsuleerHaulerTests.Fixture fixture,
        int? freightSurvival = null,
        bool fundRefits = true,
        bool standardMarket = false,
        bool earnedPatrol = false
    )
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
                    Economy = draft.Settings.Economy with
                    {
                        ChargeServiceCosts = !standardMarket && draft.Settings.Economy.ChargeServiceCosts,
                        ProgressiveMarkets = false,
                        StandardMarket = standardMarket,
                        RegionalHubSupply = !standardMarket && draft.Settings.Economy.RegionalHubSupply,
                        BeginnerMarketSupply = !standardMarket && draft.Settings.Economy.BeginnerMarketSupply,
                    },
                    DevelopmentSupply = draft.Settings.DevelopmentSupply with
                    {
                        TestingMarket = !standardMarket && draft.Settings.DevelopmentSupply.TestingMarket,
                    },
                    Capsuleers = draft.Settings.Capsuleers with
                    {
                        FundRefits = fundRefits,
                        EarnedPvpShips = earnedPatrol || draft.Settings.Capsuleers.EarnedPvpShips,
                        GateCamps = !earnedPatrol && draft.Settings.Capsuleers.GateCamps,
                        NullsecExpeditions = !earnedPatrol && draft.Settings.Capsuleers.NullsecExpeditions,
                        RoamingGangs = !earnedPatrol && draft.Settings.Capsuleers.RoamingGangs,
                        RoamPercent = earnedPatrol ? 100 : draft.Settings.Capsuleers.RoamPercent,
                        Freight = freightSurvival is int survival
                            ? draft.Settings.Capsuleers.Freight with
                            {
                                WreckSurvivalPercent = survival,
                            }
                            : draft.Settings.Capsuleers.Freight,
                    },
                },
            },
            "Corporate industry fixture"
        );
    }

    private static long DestroyBotShip(EconomyTransaction world, CatalogDbContext catalog, long pilotId, long now)
    {
        var pilot = world.Pilots.Find(pilotId)!;
        var ship = pilot.ActiveShip;
        var position = Assert.IsType<Eden.Data.World.Docking.FlightPosition>(ship.Position);
        var attacker = world.Pilots.Find(Customer)!;
        ShipLossPersistence.Apply(
            world,
            catalog,
            pilot,
            null,
            ship.Id,
            Assert.IsType<int>(position.SystemId),
            position,
            now,
            new NoShipLoot(),
            PilotInteractionHistoryStore.Player(world, attacker, attacker.ActiveShipId)
        );
        return pilot.ActiveShipId;
    }

    private sealed class NoShipLoot : IShipLootRoll
    {
        public bool Survives() => false;
    }

    private static long Stock(WorldAction world, long pilot, long station, int type) =>
        world
            .Filter<OwnedItem>(item => item.OwnerId == pilot)
            .Where(item => item.LocationId == station && item.TypeId == type)
            .Sum(item => item.Quantity);

    private static void AddStock(EconomyTransaction world, long owner, long station, int type, int amount) =>
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
        // Supply replays reconcile their treasuries independently of automatic recruitment purchases.
        var details = PlayerCorporationTests.Details(name, ticker) with
        {
            ApplicationsEnabled = false,
        };
        int id = PlayerCorporationStore.Create(world, catalog, ceo, details, now).Corporation.Id;
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
