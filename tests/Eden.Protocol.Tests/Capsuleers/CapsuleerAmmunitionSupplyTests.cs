using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Market;
using Eden.Protocol.Tests.Combat;
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
            "Ammunition chain fixture"
        );
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
            var ammunition = host.GetRequiredService<CapsuleerAmmunitionSupply>();
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
                var plan = CapsuleerAmmunitionSupply.PlanCollection(review, catalog, live, pilot, new HashSet<int>(), fixture.Clock.Now);
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
                Assert.Empty(CapsuleerAmmunitionSupply.DepotStock(world, buyer, Depot));
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
        Assert.Equal(shipment, CapsuleerAmmunitionSupply.DepotStock(received, buyer, Depot).Sum(item => item.Quantity));
        using CatalogDbContext reopenedCatalog = new(fixture.Database.CatalogPath);
        var deliveryPilot = reopened.GetRequiredService<SqliteWorld>().FindPilot(received, -1, courier)!;
        CapsuleerAmmunitionSupply.Deliver(received, reopenedCatalog, deliveryPilot, new(buyer, listingId), paidShipment);
        Assert.Equal(shipment, CapsuleerAmmunitionSupply.DepotStock(received, buyer, Depot).Sum(item => item.Quantity));
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
                .Sum(item => item.Quantity) + CapsuleerAmmunitionSupply.DepotStock(received, buyer, Depot).Sum(item => item.Quantity)
        );
        Assert.Single(
            received
                .Filter<SavedWalletLedger>(row => row.OwnerId == buyer)
                .Where(row => row.EntryType == WalletEntryType.MarketTransaction && row.AmountCents == -purchaseCents)
        );
        received.Commit();
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
