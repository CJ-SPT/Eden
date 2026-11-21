using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Fact]
    public async Task UnobservedMinerAndPaidFactoryPauseAndResumeAcrossRestart()
    {
        const int distantSystem = 30000142;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
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
        using (WorldDbContext oldSave = new(fixture.Database.Path))
        {
            oldSave.Database.ExecuteSqlRaw("ALTER TABLE WorldIndustryJob DROP COLUMN CapsuleerEndTicks");
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long producer;
        int corporation;
        long jobId;
        long deadline;
        long deposit;
        long oreBefore;
        long elapsed;
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
                corporation = Charter(setup, catalog, producer, "Offscreen Foundry", "OSFD", fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = 10_000_000;
                setup.Add(
                    new PilotSkill
                    {
                        PilotId = producer,
                        TypeId = 3380,
                        Points = 250,
                    }
                );
                AddStock(setup, producer, Depot, Rules.BlueprintType, 1);
                AddStock(setup, producer, Depot, Tritanium, 135);
                AddStock(setup, producer, Depot, Pyerite, 105);
                var docked = host.GetRequiredService<SqliteWorld>().FindPilot(setup, -1, producer)!;
                Assert.Equal(Depot, docked.StationId);
                Assert.True(StationCapabilities.Has(catalog, docked.OperationId, Eden.Gameplay.Inventory.StationService.Manufacturing));
                Assert.NotNull(CapsuleerIndustry.Employer(setup, producer, docked.CorpId));
                Assert.Contains(Rules.BlueprintType, ManufacturingCatalog.Recipes(catalog).Keys);
                setup.Commit();
            }
            var presence = host.GetRequiredService<IHumanPresence>();
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            Assert.Empty(scheduler.Jobs());
            using var first = presence.Select(Customer, distantSystem);
            using var last = presence.Select(Customer + 1, distantSystem);
            for (
                int turn = 0;
                turn < 400 && live.MinerCheckpoints.GetValueOrDefault(producer)?.Work.Activity != MinerActivity.Mining;
                turn++
            )
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
            }
            Assert.False(live.IsDetailedSystem(Home));
            Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
            var mining = live.MinerCheckpoints[producer].Work;
            Assert.True(mining.Activity == MinerActivity.Mining, $"{mining.Activity}: {mining.WaitingReason}");
            deposit = mining.TargetId;
            var collection = scheduler.Jobs().Single(job => job.PilotId == producer);
            Assert.Equal(CapsuleerAction.Collect, collection.Action);
            Assert.Equal(mining.DueTicks, collection.DueTicks);
            using (var metrics = SessionPerformance.Begin("quiet offscreen mining", _ => { }, force: true)!)
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
                Assert.Equal(collection, scheduler.Jobs().Single(job => job.PilotId == producer));
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            last.Transfer(Home);
            var observation = scheduler.Jobs().Single(job => job.PilotId == producer);
            Assert.Equal(collection.Sequence, observation.Sequence);
            Assert.True(observation.DueTicks < collection.DueTicks);
            Assert.Contains(scheduler.Capture().Systems, state => state.SystemId == Home && state.Mode == SystemSimulationMode.Active);
            fixture.Clock.Seconds++;
            maintenance.Tick();
            var observedMining = live.MinerCheckpoints[producer].Work;
            Assert.Equal(mining.Cycles, observedMining.Cycles);
            Assert.Equal(mining.DueTicks, observedMining.DueTicks);
            Assert.Equal(mining.PaidUntil, observedMining.PaidUntil);
            last.Transfer(distantSystem);
            first.Dispose();
            long before = live.CapsuleerActivity.Read().ElapsedTicks;
            fixture.Clock.Seconds++;
            maintenance.Tick();
            Assert.True(live.CapsuleerActivity.Read().ElapsedTicks > before);
            last.Dispose();
            var pausedJob = scheduler.Jobs().Single(job => job.PilotId == producer);
            elapsed = live.CapsuleerActivity.Read().ElapsedTicks;
            oreBefore = live.Economy.Read(world => world.Set<WorldAsteroid>().Find(deposit)!.Quantity);
            (jobId, deadline) = live.Economy.Read(world =>
            {
                var job = Assert.Single(world.Filter<IndustryJob>(job => job.PilotId == producer));
                Assert.Equal(IndustryJobStatus.Installed, job.Status);
                Assert.Equal(corporation, job.OutputCorporationId);
                Assert.True(job.CostCents > 0);
                return (job.Id, Assert.IsType<long>(job.CapsuleerEndTicks));
            });
            Assert.True(deadline > elapsed);
            Assert.Equal(new IndustryMapJobCount(Home, 1), Assert.Single(host.GetRequiredService<IIndustryMap>().JobsStarted(null)));
            fixture.Clock.Seconds += 7200;
            Assert.Null(maintenance.Tick());
            Assert.Equal(elapsed, live.CapsuleerActivity.Read().ElapsedTicks);
            Assert.Contains(pausedJob, scheduler.Jobs());
            Assert.Equal(oreBefore, live.Economy.Read(world => world.Set<WorldAsteroid>().Find(deposit)!.Quantity));
            Assert.Equal(IndustryJobStatus.Installed, host.GetRequiredService<IPilotIndustry>().Job(producer, jobId).Status);
            await live.FlushAsync();
        }

        fixture.Clock.Seconds += 7200;
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var restarted = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var resumed = restarted.GetRequiredService<LiveWorld>();
        var worker = restarted.GetRequiredService<CapsuleerMaintenance>();
        var industry = restarted.GetRequiredService<IPilotIndustry>();
        Assert.Null(worker.Tick());
        Assert.Equal(elapsed, resumed.CapsuleerActivity.Read().ElapsedTicks);
        Assert.Equal(deadline, resumed.Economy.Read(world => world.Set<IndustryJob>().Find(jobId)!.CapsuleerEndTicks));
        Assert.Equal(IndustryJobStatus.Installed, industry.Job(producer, jobId).Status);
        Assert.Equal(
            new IndustryMapJobCount(Home, 1),
            Assert.Single(restarted.GetRequiredService<IIndustryMap>().JobsStarted(IndustryActivity.Manufacturing))
        );
        using var online = restarted.GetRequiredService<IHumanPresence>().Select(Customer, distantSystem);
        for (
            int turn = 0;
            turn < 120 && resumed.Economy.Read(world => world.Set<WorldAsteroid>().Find(deposit)!.Quantity) == oreBefore;
            turn++
        )
        {
            fixture.Clock.Seconds++;
            worker.Tick();
        }
        Assert.True(resumed.Economy.Read(world => world.Set<WorldAsteroid>().Find(deposit)!.Quantity) < oreBefore);
        Assert.Equal(IndustryJobStatus.Installed, industry.Job(producer, jobId).Status);
        fixture.Clock.Seconds += (deadline - resumed.CapsuleerActivity.Read().ElapsedTicks) / TimeSpan.TicksPerSecond + 1;
        for (int turn = 0; turn < 800 && industry.Job(producer, jobId).Status != IndustryJobStatus.Delivered; turn++)
        {
            fixture.Clock.Seconds++;
            worker.Tick();
        }
        Assert.Equal(IndustryJobStatus.Delivered, industry.Job(producer, jobId).Status);
        Assert.False(resumed.IsDetailedSystem(Home));
        resumed.Economy.Read(world =>
        {
            var listing = Assert.Single(world.Filter<SavedPersonalOrder>(order => order.CorporationId == corporation));
            Assert.Equal(Rules.ChargeType, listing.TypeId);
            Assert.Equal(Rules.ProductionRuns * 100, listing.Remaining);
            return 0;
        });
    }
}
