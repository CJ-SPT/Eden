using Eden.Data.Catalog.Market;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Market;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Theory]
    [InlineData(CapsuleerCareer.Hauling, true, 0, false)]
    [InlineData(CapsuleerCareer.Mining, false, 0, false)]
    [InlineData(CapsuleerCareer.Hauling, false, 2, false)]
    [InlineData(CapsuleerCareer.Hauling, false, 0, true)]
    public async Task StrandedBotTravelsToJitaAndBuysAnOnlineFitAcrossRestart(
        CapsuleerCareer career,
        bool funded,
        int podLosses,
        bool avoidThreat
    )
    {
        bool mining = career == CapsuleerCareer.Mining;
        int hull = mining ? (int)CapsuleerMiningHull.Venture : (int)FreightHull.Nereus;
        int module = mining ? (int)MiningLaserType.MinerI : (int)StarterMarketType.Afterburner;
        const int jita = 30000142;
        const int threatened = 30000143;
        const int detour = 30000144;
        const long ask = 100;
        long opening = funded ? 0 : 10_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        if (avoidThreat)
        {
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30000143,20000020,'Threatened');
                DELETE FROM Stargate WHERE Id IN (50009991,50009992);
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                    (50010000,30000143,16,10,1e11,0,0),(50010001,30000143,16,10,-1e11,0,0),
                    (50010002,30000141,16,10,2e11,0,0),(50010003,30000144,16,10,1e11,0,0),
                    (50010004,30000144,16,10,-1e11,0,0),(50010005,30000142,16,10,2e11,0,0);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                    (50009991,30000141,50010000),(50010000,30000143,50009991),
                    (50009992,30000142,50010001),(50010001,30000143,50009992),
                    (50010002,30000141,50010003),(50010003,30000144,50010002),
                    (50010004,30000144,50010005),(50010005,30000142,50010004);
                """
            );
        }
        PassiveCatalogFixture.LoadStarterMarket(fixture.Database.CatalogPath, new HashSet<int> { module });
        UseLocalOrders(fixture, fundRefits: funded);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        if (!mining)
        {
            PassiveCatalogFixture.LoadUtilityHull(fixture.Database.CatalogPath, hull);
        }
        long bot;
        long capsule;
        long lostShip;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            if (avoidThreat)
            {
                var normalRoute = CapsuleerTrafficRoutes.CapsuleToTradeHub(catalog, Depot, FactoryStation);
                Assert.Equal(new[] { Home, threatened, jita }, normalRoute?.Systems);
            }
            using (var setup = live.Economy.Begin())
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                bot = setup.Set<SavedCapsuleerAgent>().First(agent => agent.Career == career).PilotId;
                foreach (var other in setup.Set<SavedCapsuleerAgent>().Where(agent => agent.PilotId != bot).ToArray())
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = other.PilotId });
                }
                var pilot = setup.Pilots.Find(bot)!;
                lostShip = pilot.ActiveShipId;
                pilot.ActiveShip.TypeId = hull;
                CapsuleerCommunityStore.GrantRequirements(setup, catalog, bot, hull, []);
                CapsuleerCommunityStore.GrantRequirements(setup, catalog, bot, module, []);
                var station = SystemObjects.Geometry(catalog, Home).Single(body => body.Id == Depot);
                SqliteWorld.SetPosition(setup, catalog, lostShip, station.Position, Home, fixture.Clock.Now);
                capsule = DestroyBotShip(setup, catalog, bot, fixture.Clock.Now);
                var ships = host.GetRequiredService<SqliteWorld>();
                if (podLosses > 0)
                {
                    var home = setup.Set<SavedHomeStation>().Find(bot) ?? setup.Add(new SavedHomeStation { PilotId = bot }).Entity;
                    home.StationId = Depot;
                    var attacker = setup.Pilots.Find(Customer)!;
                    var party = PilotInteractionHistoryStore.Player(setup, attacker, attacker.ActiveShipId);
                    var losses = host.GetRequiredService<PilotLossSettlement>();
                    for (int destroyed = 0; destroyed < podLosses; destroyed++)
                    {
                        SqliteWorld.SetPosition(setup, catalog, capsule, station.Position, Home, fixture.Clock.Now);
                        losses.Apply(setup, catalog, pilot, capsule, Home, pilot.ActiveShip.Position!, fixture.Clock.Now, party);
                        capsule = pilot.ActiveShipId;
                    }
                }
                setup.Set<PilotWallet>().Find(bot)!.Cents = opening;
                var docked =
                    podLosses == 0
                        ? ships.Dock(setup, catalog, bot, capsule, Depot)
                        : ships.FindPilot(setup, CapsuleerCommunityStore.MinerAccountId, bot)!;
                Assert.Null(docked.Position);
                Assert.Equal(Depot, docked.StationId);
                Assert.Null(CapsuleerIndustry.Employer(setup, bot, docked.CorpId));

                var seller = setup.Pilots.Find(Customer)!;
                seller.ActiveShip.StationId = FactoryStation;
                if (setup.Positions.Find(seller.ActiveShipId) is { } sellerPosition)
                {
                    setup.Remove(sellerPosition);
                }
                setup.Add(new PilotWallet { PilotId = Customer, Cents = 10 * PersonalMarketRules.MinimumBrokerFeeCents });
                AddStock(setup, Customer, FactoryStation, hull, 1);
                AddStock(setup, Customer, FactoryStation, module, 1);
                var offers = setup
                    .Filter<OwnedItem>(item => item.OwnerId == Customer)
                    .Where(item => item.LocationId == FactoryStation && (item.TypeId == hull || item.TypeId == module))
                    .Select(item => new MarketSale(item.Id, FactoryStation, item.TypeId, ask, 1))
                    .ToArray();
                Assert.Equal(2, offers.Length);
                host.GetRequiredService<PilotMarketStore>().ExecuteSell(setup, catalog, Customer, offers, 30);

                // Reopen the same docked retry state produced by older servers when a refuge has no fit.
                var navigation = new NavigationState().Capture();
                long due = setup.Set<SavedCapsuleerJob>().Find(bot)!.DueTicks;
                const string waiting = "Waiting for an affordable replacement in stock.";
                long expired = fixture.Clock.Now - TimeSpan.TicksPerSecond;
                if (mining)
                {
                    new MinerCheckpoint(
                        bot,
                        new(MinerActivity.Waiting, 0, 0, 0, 0, 0, 0, navigation, capsule, DueTicks: due, WaitingReason: waiting)
                        {
                            ReplacementOrderDeadline = expired,
                        }
                    ).Apply(setup);
                }
                else
                {
                    new HaulerCheckpoint(
                        bot,
                        new(HaulerActivity.Waiting, 0, 0, capsule, Depot, 0, navigation, waiting, DueTicks: due, Retreating: true)
                        {
                            ReplacementOrderDeadline = expired,
                            Routine = avoidThreat
                                ? TravelerRoutineRules.Create(bot) with
                                {
                                    Threats = [new(threatened, TravelerRoutineRules.ThreatMemory.Ticks)],
                                }
                                : null,
                        }
                    ).Apply(setup);
                }
                setup.Commit();
            }
            await live.FlushAsync();
        }

        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            Advance(
                host,
                scheduler =>
                    mining
                        ? scheduler.Miner(bot)?.Work.Activity == MinerActivity.Jumping
                        : scheduler.Hauler(bot)?.Work
                            is { Activity: HaulerActivity.Jumping, Journey.Kind: TrafficJourneyKind.ReplacementShopping }
            );
            Assert.True(live.Economy.ActiveShip(bot, capsule));
            var route = mining ? live.MinerCheckpoints[bot].Work.ReplacementJourney : live.HaulerCheckpoints[bot].Work.Journey;
            Assert.Equal(avoidThreat ? new[] { Home, detour, jita } : [Home, jita], route?.Systems);
            Assert.Equal(FactoryStation, route?.DestinationStationId);
            Assert.Empty(live.Economy.Read(world => world.Filter<MarketExecution>(row => row.PilotId == bot).ToArray()));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        Advance(
            reopened,
            scheduler => mining ? scheduler.Miner(bot)?.Pilot.ShipTypeId == hull : scheduler.Hauler(bot)?.Pilot.ShipTypeId == hull
        );
        long replacement = restored.Economy.Read(world => world.Pilots.Find(bot)!.ActiveShipId);
        Assert.NotEqual(lostShip, replacement);
        restored.Economy.Read(world =>
        {
            Assert.Null(world.Ships.Find(capsule));
            Assert.NotNull(world.Set<ShipLoss>().Find(lostShip));
            Assert.Equal(
                podLosses,
                world.Filter<ShipLoss>(loss => loss.PilotId == bot).Count(loss => loss.TypeId == FleetDefinitions.CapsuleType)
            );
            Assert.Contains(world.FittedItems(bot, replacement), fitted => fitted.TypeId == module && fitted.Online);
            var purchases = world.Filter<MarketExecution>(row => row.PilotId == bot).ToArray();
            Assert.Equal(new[] { hull, module }.Order(), purchases.Select(row => row.TypeId).Order());
            Assert.All(purchases, trade => Assert.Equal(FactoryStation, trade.StationId));
            Assert.Equal(opening - (funded ? 0 : 2 * ask), world.Set<PilotWallet>().Find(bot)!.Cents);
            return true;
        });
        if (mining)
        {
            Advance(reopened, scheduler => scheduler.Miner(bot)?.Pilot is { Position: null, StationId: Depot });
            Assert.Null(restored.MinerCheckpoints[bot].Work.ReplacementJourney);
        }

        void Advance(ServiceProvider host, Func<CapsuleerScheduler, bool> reached)
        {
            using var active = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            scheduler.PopulationPrepared(jita);
            if (avoidThreat)
            {
                scheduler.PopulationPrepared(detour);
            }
            for (int turn = 0; turn < 600; turn++)
            {
                fixture.Clock.Seconds++;
                worker.Tick();
                if (reached(scheduler))
                {
                    return;
                }
            }
            throw new InvalidOperationException(
                $"Replacement journey stalled. Miner {scheduler.Miner(bot)?.Work} Hauler {scheduler.Hauler(bot)?.Work}"
            );
        }
    }

    [Fact]
    public void UnfundedBotBuysReplacementAtCombinedNpcAskPrice()
    {
        const int jita = 30000142;
        const int theForge = 10000002;
        const int hull = (int)FreightHull.Nereus;
        const int module = (int)StarterProductionType.ExpandedCargohold;
        const int hullPrice = 100;
        const int firstModulePrice = 100;
        const int secondModulePrice = 200;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        PassiveCatalogFixture.LoadStarterMarket(fixture.Database.CatalogPath, new HashSet<int> { module });
        PassiveCatalogFixture.LoadUtilityHull(fixture.Database.CatalogPath, hull);
        RemoveUnpricedFixtureMaterials(fixture);
        StandardMarketTests.WriteSnapshot(
            fixture.Database,
            fixture.Clock,
            [Ask(1, hull, hullPrice), Ask(2, module, firstModulePrice), Ask(3, module, secondModulePrice)]
        );
        UseLocalOrders(fixture, fundRefits: false, standardMarket: true);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var world = host.GetRequiredService<LiveWorld>().Economy.Begin();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        long bot = world.Set<SavedCapsuleerAgent>().First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
        var pilot = world.Pilots.Find(bot)!;
        long lost = pilot.ActiveShipId;
        pilot.ActiveShip.TypeId = hull;
        CapsuleerCommunityStore.GrantRequirements(world, catalog, bot, hull, []);
        CapsuleerCommunityStore.GrantRequirements(world, catalog, bot, module, []);
        foreach (int flag in Enumerable.Range((int)InventoryFlag.LowSlot0, 2))
        {
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    LocationId = lost,
                    OwnerId = bot,
                    TypeId = module,
                    FlagId = flag,
                    Quantity = 1,
                    Singleton = true,
                    Online = true,
                }
            );
        }
        var station = SystemObjects.Geometry(catalog, jita).Single(body => body.Id == FactoryStation);
        SqliteWorld.SetPosition(world, catalog, lost, station.Position, jita, fixture.Clock.Now);
        long capsule = DestroyBotShip(world, catalog, bot, fixture.Clock.Now);
        var docked = host.GetRequiredService<SqliteWorld>().Dock(world, catalog, bot, capsule, FactoryStation);
        Assert.Null(CapsuleerIndustry.Employer(world, bot, docked.CorpId));
        var wallet = world.Set<PilotWallet>().Find(bot)!;
        wallet.Cents = hullPrice + firstModulePrice + secondModulePrice;

        var result = host.GetRequiredService<CapsuleerIndustry>()
            .ReplaceShip(world, catalog, docked, CapsuleerCareer.Hauling, fixture.Clock.Now, null, null);

        Assert.True(result.Boarded);
        Assert.Equal(0, wallet.Cents);
        Assert.Equal(2, world.FittedItems(bot, pilot.ActiveShipId).Count(item => item.TypeId == module && item.Online));
        Assert.Equal(
            new long[] { firstModulePrice, secondModulePrice },
            world
                .Filter<MarketExecution>(trade => trade.PilotId == bot)
                .Where(trade => trade.TypeId == module)
                .Select(trade => trade.PriceCents)
                .Order()
        );
        world.Commit();

        LiveMarketOrder Ask(long id, int type, long price) =>
            new(id, FactoryStation, jita, theForge, type, false, price, 1, 1, fixture.Clock.Now, 90, MarketDefinitions.StationRange, 1);
    }
}
