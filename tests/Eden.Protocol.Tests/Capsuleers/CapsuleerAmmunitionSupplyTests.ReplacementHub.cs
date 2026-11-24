using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Theory]
    [InlineData(CapsuleerCareer.Hauling, true)]
    [InlineData(CapsuleerCareer.Mining, false)]
    public async Task StrandedBotTravelsToJitaAndBuysAnOnlineFitAcrossRestart(CapsuleerCareer career, bool funded)
    {
        bool mining = career == CapsuleerCareer.Mining;
        int hull = mining ? (int)CapsuleerMiningHull.Venture : (int)FreightHull.Nereus;
        int module = mining ? (int)MiningLaserType.MinerI : (int)StarterMarketType.Afterburner;
        const int jita = 30000142;
        const long ask = 100;
        long opening = funded ? 0 : 10_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
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
                setup.Set<PilotWallet>().Find(bot)!.Cents = opening;
                var docked = host.GetRequiredService<SqliteWorld>().Dock(setup, catalog, bot, capsule, Depot);
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
            Assert.Equal(new[] { Home, jita }, route?.Systems);
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
}
