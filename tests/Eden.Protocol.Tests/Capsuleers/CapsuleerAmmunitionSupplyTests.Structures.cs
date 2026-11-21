using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Structures;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ammo = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Fact]
    public async Task BotStagesAtAPlayerFactoryAndFinishesItsPaidJobAfterServicePauseAndRestart()
    {
        const long factory = StructureRules.FirstStructureId + 10;
        const int raitaru = 35825;
        const int manufacturingPlant = 35878;
        const int fuelBlock = 4051;
        const int distantSystem = 30000142;
        const long observer = Customer + 1;
        const long capital = 10_000_000;
        const int servicePauseSeconds = 75;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        PassiveCatalogFixture.LoadStructures(fixture.Database.CatalogPath);
        RemoveUnpricedFixtureMaterials(fixture);
        UseLocalOrders(fixture);
        using (CatalogDbContext edited = new(fixture.Database.CatalogPath, writable: true))
        {
            edited.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/systems','30000141','{{"securityStatus":1.0}}'),
                    ('sde/npcStations','60015157','{{"operationID":14,"reprocessingEfficiency":0.5,"reprocessingStationsTake":0.05}}');
                """
            );
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long producer;
        long shipId;
        long module;
        int corporation;
        int facilityOwner;
        CapsuleerFactoryCargo[] aboard;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                var agents = setup.Set<SavedCapsuleerAgent>().ToArray();
                producer = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                setup.Filter<SavedCapsuleerCommunity>(row => row.PilotId == producer).Single().Progress = null;
                foreach (var agent in agents.Where(agent => agent.PilotId != producer))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                shipId = setup.Pilots.Find(producer)!.ActiveShipId;
                corporation = Charter(setup, catalog, producer, "Staging Foundry", "STFD", fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = capital;
                if (setup.Set<PilotWallet>().Find(Customer) is null)
                {
                    setup.Add(new PilotWallet { PilotId = Customer });
                }
                facilityOwner = Charter(setup, catalog, Customer, "Player Engineering", "PLEN", fixture.Clock.Now);
                var station = catalog.RequireStation(Depot);
                setup.Add(
                    new SavedStructure
                    {
                        Id = factory,
                        CorporationId = facilityOwner,
                        Name = "Player Workshop",
                        TypeId = raitaru,
                        State = StructureState.ShieldVulnerable,
                        HasCore = true,
                        SystemId = Home,
                        X = station.X + 30_000_000,
                        Y = station.Y,
                        Z = station.Z,
                    }
                );
                setup.Pilots.Find(Customer)!.ActiveShip.StructureId = factory;
                AddStock(setup, Customer, factory, manufacturingPlant, 1);
                AddStock(setup, Customer, factory, fuelBlock, 5000);
                AddStock(setup, producer, Depot, Ammo.BlueprintType, 1);
                AddStock(setup, producer, Depot, Tritanium, 180);
                AddStock(setup, producer, Depot, Pyerite, 150);
                setup.Commit();
            }
            var (plant, fuel) = live.Economy.Read(world =>
            {
                var items = world.Filter<OwnedItem>(item => item.OwnerId == Customer).Where(item => item.LocationId == factory).ToArray();
                return (items.Single(item => item.TypeId == manufacturingPlant).Id, items.Single(item => item.TypeId == fuelBlock).Id);
            });
            module = plant;
            var structures = host.GetRequiredService<IPilotStructures>();
            structures.FitService(Customer, factory, module, StructureRules.FirstServiceSlot);
            structures.DepositFuel(Customer, factory, fuel, 5000);
            structures.SetAccess(Customer, factory, publicAccess: false, [new(corporation, true)]);
            AssertUnavailable();
            structures.SetServiceOnline(Customer, factory, module, true);
            structures.SetAccess(Customer, factory, publicAccess: true, [new(producer, false)]);
            AssertUnavailable();
            structures.SetAccess(Customer, factory, publicAccess: false, [new(corporation, true)]);

            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            using var online = host.GetRequiredService<IHumanPresence>().Select(observer, distantSystem);
            AdvanceUntil(host, () => live.MinerCheckpoints.GetValueOrDefault(producer)?.Work.FactoryStage?.Cargo.Length == 3);
            var stage = live.MinerCheckpoints[producer].Work.FactoryStage!;
            Assert.Equal(factory, stage.StructureId);
            Assert.Equal(Depot, stage.StationId);
            Assert.False(live.IsDetailedSystem(Home));
            aboard = stage.Cargo;
            live.Economy.Read(world =>
            {
                Assert.Empty(world.Filter<IndustryJob>(job => job.PilotId == producer));
                Assert.NotNull(world.Positions.Find(shipId));
                Assert.Null(world.Ships.Find(shipId)!.StructureId);
                Assert.Equal(0, Stock(world, producer, Depot, Ammo.BlueprintType));
                foreach (var stack in aboard)
                {
                    var item = world.Set<OwnedItem>().Find(stack.ItemId)!;
                    Assert.Equal(
                        (producer, shipId, (int)InventoryFlag.Cargo, stack.Quantity),
                        (item.OwnerId, item.LocationId, item.FlagId, item.Quantity)
                    );
                }
                return 0;
            });
            online.Dispose();
            fixture.Clock.Seconds += 3600;
            Assert.Null(maintenance.Tick());
            await live.FlushAsync();

            void AssertUnavailable()
            {
                using var read = live.Economy.Begin(TransactionParticipants.None);
                var docked = host.GetRequiredService<SqliteWorld>().FindPilot(read, -1, producer)!;
                Assert.Null(host.GetRequiredService<CapsuleerIndustry>().PlanFactoryStaging(read, catalog, docked, fixture.Clock.Now));
                Assert.Equal(1, Stock(read, producer, Depot, Ammo.BlueprintType));
                Assert.Null(read.Ships.Find(shipId)!.StructureId);
            }
        }

        long jobId;
        long deadline;
        long activePause;
        long cost;
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            Assert.All(
                aboard,
                stack => Assert.Equal(shipId, live.Economy.Read(world => world.Set<OwnedItem>().Find(stack.ItemId)!.LocationId))
            );
            using var online = host.GetRequiredService<IHumanPresence>().Select(observer, distantSystem);
            AdvanceUntil(host, () => live.Economy.Read(world => world.Ships.Find(shipId)!.StructureId) == factory);
            AdvanceUntil(host, () => live.Economy.Read(world => world.Filter<IndustryJob>(job => job.PilotId == producer).Any()));
            (jobId, deadline, cost) = live.Economy.Read(world =>
            {
                var job = Assert.Single(world.Filter<IndustryJob>(job => job.PilotId == producer));
                Assert.Equal(factory, job.StationId);
                Assert.Equal(corporation, job.OutputCorporationId);
                Assert.True(job.CostCents > 0);
                Assert.Equal(capital - job.CostCents, world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                Assert.Equal(factory, world.Set<OwnedItem>().Find(job.BlueprintId)!.LocationId);
                Assert.Equal(0, Stock(world, producer, factory, Ammo.ChargeType));
                return (job.Id, Assert.IsType<long>(job.CapsuleerEndTicks), job.CostCents);
            });
            Assert.Empty(live.MinerCheckpoints[producer].Work.FactoryStage!.Cargo);
            long elapsed = live.CapsuleerActivity.Read().ElapsedTicks;
            online.Dispose();
            fixture.Clock.Seconds += 3600;
            Assert.Null(host.GetRequiredService<CapsuleerMaintenance>().Tick());
            Assert.Equal(elapsed, live.CapsuleerActivity.Read().ElapsedTicks);
            Assert.Equal(IndustryJobStatus.Installed, host.GetRequiredService<IPilotIndustry>().Job(producer, jobId).Status);
            using var resumedOnline = host.GetRequiredService<IHumanPresence>().Select(observer, distantSystem);
            var structures = host.GetRequiredService<IPilotStructures>();
            structures.SetServiceOnline(Customer, factory, module, false);
            activePause = live.CapsuleerActivity.Read().ElapsedTicks;
            fixture.Clock.Seconds += servicePauseSeconds;
            resumedOnline.Dispose();
            fixture.Clock.Seconds += 7200;
            Assert.Null(host.GetRequiredService<CapsuleerMaintenance>().Tick());
            Assert.Equal(IndustryJobStatus.Paused, host.GetRequiredService<IPilotIndustry>().Job(producer, jobId).Status);
            await live.FlushAsync();
        }

        fixture.Clock.Seconds += 7200;
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var industry = host.GetRequiredService<IPilotIndustry>();
            Assert.Null(host.GetRequiredService<CapsuleerMaintenance>().Tick());
            Assert.Equal(activePause, live.Economy.Read(world => world.Set<IndustryJob>().Find(jobId)!.CapsuleerSuspendedTicks));
            Assert.Equal(deadline, live.Economy.Read(world => world.Set<IndustryJob>().Find(jobId)!.CapsuleerEndTicks));
            Assert.Equal(IndustryJobStatus.Paused, industry.Job(producer, jobId).Status);
            using var online = host.GetRequiredService<IHumanPresence>().Select(observer, distantSystem);
            host.GetRequiredService<IPilotStructures>().SetServiceOnline(Customer, factory, module, true);
            long rescheduled = deadline + TimeSpan.FromSeconds(servicePauseSeconds).Ticks;
            Assert.Equal(rescheduled, live.Economy.Read(world => world.Set<IndustryJob>().Find(jobId)!.CapsuleerEndTicks));
            Assert.Equal(IndustryJobStatus.Installed, industry.Job(producer, jobId).Status);
            fixture.Clock.Seconds += (rescheduled - live.CapsuleerActivity.Read().ElapsedTicks) / TimeSpan.TicksPerSecond - 1;
            Assert.Equal(IndustryJobStatus.Installed, industry.Job(producer, jobId).Status);
            fixture.Clock.Seconds++;
            Assert.Equal(IndustryJobStatus.Ready, industry.Job(producer, jobId).Status);
            AdvanceUntil(host, () => industry.Job(producer, jobId).Status == IndustryJobStatus.Delivered);
            Assert.False(live.IsDetailedSystem(Home));
            await live.FlushAsync();
        }

        using WorldDbContext reopened = new(fixture.Database.Path);
        Assert.Equal(IndustryJobStatus.Delivered, reopened.Set<IndustryJob>().Single(job => job.PilotId == producer).Status);
        Assert.Equal(
            Ammo.ProductionRuns * 100,
            reopened
                .Set<OwnedItem>()
                .Single(item => item.OwnerId == producer && item.TypeId == Ammo.ChargeType && item.LocationId == factory)
                .Quantity
        );
        var payment = Assert.Single(
            reopened.Set<SavedWalletLedger>().Where(row => row.OwnerId == corporation && row.EntryType == WalletEntryType.Manufacturing)
        );
        Assert.Equal(-cost, payment.AmountCents);
        Assert.Equal(facilityOwner, payment.CounterpartyId);

        void AdvanceUntil(ServiceProvider host, Func<bool> done)
        {
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (int turn = 0; turn < 1200 && !done(); turn++)
            {
                fixture.Clock.Seconds++;
                worker.Tick();
            }
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var last = scheduler.Miner(producer)?.Work;
            Assert.True(done(), scheduler.Read().LastFailure ?? $"{last?.Activity}: {last?.WaitingReason}");
        }
    }
}
