using Eden.Data.World.Capsuleers;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed partial class FreshUniverseTests
{
    [InstalledCatalogFact]
    public async Task FreshBountyFounderSwitchesToAFundedManufacturingOrderInTheSameCorvette()
    {
        var directory = TestDirectory.Create("eden-fresh-dynamic-work-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            File.Copy(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)!, database.OriginalCatalogPath);
            TestClock clock = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
            WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
            SetMining(false);
            WorldInitialization.PrepareCapsuleers(database, maximumCommunities: 0);
            long founder;
            long ship;
            long buyer;
            long bid;
            using (var host = Open())
            {
                var live = host.GetRequiredService<LiveWorld>();
                var presence = host.GetRequiredService<IHumanPresence>();
                presence.Update(Guid.NewGuid(), StarterSystem);
                var prepared = host.GetRequiredService<CapsuleerProvisioner>()
                    .PrepareBackgroundSystem(
                        StarterSystem,
                        host.GetRequiredService<IWorldSynchronization>(),
                        () => true,
                        CancellationToken.None
                    );
                founder = Assert.IsType<long>(prepared?.CreatedPilotId);
                ship = live.Economy.Inspect(world =>
                {
                    var agent = Assert.Single(world.Set<SavedCapsuleerAgent>());
                    Assert.Equal(CapsuleerCareer.Hauling, agent.Career);
                    Assert.Equal(FreshCareerRules.CombatSlot, agent.Slot);
                    return world.Pilots.Find(founder)!.ActiveShipId;
                });
                Assert.Equal(0, host.GetRequiredService<IPilotMarket>().Balance(founder));
                buyer = CreateSupplier(host, founder);
                using CatalogDbContext catalog = new(database.CatalogPath);
                long price = CapsuleerIndustry.ReferencePrice(catalog, Miner);
                using (var setup = live.Economy.Begin(TransactionParticipants.None))
                {
                    setup.Pilots.Find(buyer)!.ActiveShip.StationId = Factory;
                    var wallet = setup.Set<PilotWallet>().Find(buyer) ?? setup.Add(new PilotWallet { PilotId = buyer }).Entity;
                    wallet.Cents = price * 2;
                    setup.Commit();
                }
                var market = host.GetRequiredService<IPersonalMarket>();
                market.PlaceBuy(buyer, new(Factory, Miner, price, 1, StarterIndustryRules.OrderDays));
                bid = Assert.Single(market.PersonalOrders(buyer)).Id;
                await live.FlushAsync();
            }

            SetMining(true);
            using var reopened = Open();
            var restored = reopened.GetRequiredService<LiveWorld>();
            var trades = reopened.GetRequiredService<IPilotMarket>();
            AdvanceCareers(reopened, StarterSystem, clock, () => trades.Transactions(buyer).Any(trade => trade.OrderId == bid));
            Assert.Equal(founder, Assert.Single(trades.Transactions(buyer), trade => trade.OrderId == bid).CounterpartyId);
            restored.Economy.Inspect(world =>
            {
                Assert.Equal(ship, world.Pilots.Find(founder)!.ActiveShipId);
                Assert.Equal(CapsuleerCareer.Mining, CapsuleerCorporations.Organization(world, founder).ActiveWorker);
                var job = Assert.Single(world.Filter<IndustryJob>(job => job.PilotId == founder), job => job.ProductTypeId == Miner);
                Assert.Equal(IndustryJobStatus.Delivered, job.Status);
                Assert.True(job.CostCents > 0);
                return true;
            });

            ServiceProvider Open() => ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true);

            void SetMining(bool enabled)
            {
                var store = new WorldConfigurationStore(database, clock);
                var state = store.Read();
                var draft = state.Active.Draft;
                var settings = draft.Settings with
                {
                    Capsuleers = draft.Settings.Capsuleers with
                    {
                        Miners = enabled,
                        FreshCourierPilots = false,
                        PrepareUnvisitedSystems = false,
                    },
                };
                store.Save(state.Generation, draft with { Settings = settings }, "Isolate a rookie worker and a funded factory order");
                WorldInitialization.Initialize(database, clock);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
