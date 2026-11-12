using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class OccupiedCapsuleerSimulationTests
{
    private const int Home = 30000141;
    private const int Elsewhere = 30000144;

    [Fact]
    public void LastObserverLeavingStopsTheQueueAndWarmRemoteTurnsDoNoWork()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        var presence = host.GetRequiredService<IHumanPresence>();
        using var first = presence.Select(90000001, Home);
        using var second = presence.Select(90000002, Home);
        var jobs = scheduler.Jobs().ToArray();
        Assert.NotEmpty(jobs);
        first.Transfer(Elsewhere);
        Assert.Equal(jobs.Length, scheduler.Read().ScheduledAgents);
        second.Dispose();
        scheduler.Run(0);
        Assert.Equal(0, scheduler.Read().ScheduledAgents);
        Assert.Null(scheduler.NextWake().Delay);
        long loaded = scheduler.PopulationRowsLoaded;
        fixture.Clock.Seconds += 180;
        using var metrics = SessionPerformance.Begin("empty occupied system with dormant fleet", _ => { }, force: true)!;
        for (int turn = 0; turn < 20; turn++)
        {
            Assert.Equal(0, scheduler.Run(0).Turn.Completed);
        }
        Assert.Equal(jobs, scheduler.Jobs());
        Assert.Equal(loaded, scheduler.PopulationRowsLoaded);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public async Task PaidMiningPausesAcrossTransferAndRestartThenResumesWithItsRemainingTime()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                UPDATE TypeAttribute SET Value=120000
                WHERE ItemTypeId={(int)MiningLaserType.MinerI} AND AttributeId={(int)DogmaAttributeId.Duration}
                """
            );
        }
        MinerObservation? paid = null;
        CapsuleerJob saved;
        long departed;
        long entered;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var presence = host.GetRequiredService<IHumanPresence>();
            using var human = presence.Select(90000001, Home);
            for (int action = 0; action < 1000; action++)
            {
                paid = CapsuleerTestDriver.Miner(host, fixture.Clock);
                if (paid.Work.Activity == MinerActivity.Mining)
                {
                    break;
                }
            }
            Assert.Equal(MinerActivity.Mining, paid!.Work.Activity);
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            saved = scheduler.Jobs().Single(j => j.PilotId == paid.Pilot.PilotId);
            human.Transfer(Elsewhere);
            departed = presence.Read().ElapsedTicks;
            fixture.Clock.Seconds += 180;
            Assert.Equal(0, scheduler.Run(0).Turn.Completed);
            Assert.Contains(saved, scheduler.Jobs());
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(paid.Work, live.MinerCheckpoints[paid.Pilot.PilotId].Work);
            entered = presence.Read().ElapsedTicks;
            await live.FlushAsync();
        }
        fixture.Clock.Seconds += 86400;
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<ICapsuleerScheduler>();
        Assert.Empty(restored.Jobs());
        Assert.Equal(0, restored.Run(0).Turn.Completed);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            var row = world.Set<SavedCapsuleerCommunity>().Single(m => m.PilotId == saved.PilotId);
            Assert.Equal(paid!.Work, MinerCheckpoint.Read(row.Progress));
        }
        using var returned = reopened.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        Assert.Contains(saved, restored.Jobs());
        restored.Run(0);
        var work = reopened.GetRequiredService<LiveWorld>().MinerCheckpoints[saved.PilotId].Work;
        Assert.Equal(paid!.Work.Cycles, work.Cycles);
        Assert.Equal(paid.Work.DueTicks + entered - departed, work.DueTicks);
        Assert.Equal(fixture.Clock.Now + work.DueTicks - entered, work.PaidUntil);
        Assert.True(restored.Jobs().Single(j => j.PilotId == saved.PilotId).DueTicks > entered);
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(8, 4)]
    [InlineData(600, 4)]
    public void ResumeRetainsTheTransferWithoutReplayingAnExpiredArrival(int ageSeconds, int expectedArrivalUntil)
    {
        const long arrived = TimeSpan.TicksPerDay;
        HaulerProgress work = new(
            HaulerActivity.Flying,
            4,
            0,
            1,
            2,
            3,
            new NavigationState().Capture(),
            ArrivalUntil: 12,
            ArrivalGateId: 4,
            ArrivalAt: arrived
        );
        var resumed = CapsuleerPauseRules.Resume(
            work,
            TimeSpan.FromSeconds(ageSeconds).Ticks,
            arrived + TimeSpan.FromSeconds(ageSeconds).Ticks
        );
        Assert.Equal(expectedArrivalUntil, resumed.ArrivalUntil);
        Assert.Equal(work.ArrivalGateId, resumed.ArrivalGateId);
        Assert.Equal(work.ArrivalAt, resumed.ArrivalAt);
    }

    [Fact]
    public void EnteringSavedPopulationDoesNotHydrateEveryLegacyTraveler()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var metrics = SessionPerformance.Begin("cold membership uses compact metadata", _ => { }, force: true)!;
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        Assert.InRange(metrics.FittingCalculations, 0, 1);
    }

    [Fact]
    public void PopulationMetadataRefreshLeavesUnrelatedResidentMotionDirty()
    {
        using Combat.NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var population = fixture.Db.Services.GetRequiredService<ICapsuleerPopulation>();
        population.Summary();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        var runtime = live.CombatForSystem(Elsewhere)!;
        var original = fixture.Pilot.Position!;
        var moved = original + new SpacePosition(3000, 0, 0);
        runtime.PublishPilotPosition(Combat.NpcCombatTests.Fixture.ShipId, Elsewhere, moved);
        using (WorldDbContext world = new(fixture.Db.Path) { Participants = TransactionParticipants.None })
        {
            var saved = world.Set<SavedCapsuleerSystem>().Find(Home);
            if (saved is null)
            {
                world.Add(
                    new SavedCapsuleerSystem
                    {
                        SystemId = Home,
                        Eligibility = CapsuleerEligibility.NoMarket,
                        CatalogFingerprint = "test",
                        PolicyVersion = CapsuleerProvisioningRules.Version,
                    }
                );
            }
            else
            {
                saved.PolicyVersion++;
            }
            world.SaveChanges();
        }
        population.Summary();
        Assert.Equal(moved, runtime.InformationPosition(Combat.NpcCombatTests.Fixture.ShipId));
        using WorldDbContext committed = new(fixture.Db.Path) { Participants = TransactionParticipants.None };
        var position = committed.Positions.Single(p => p.ShipId == Combat.NpcCombatTests.Fixture.ShipId);
        Assert.Equal(original, new SpacePosition(position.X, position.Y, position.Z));
    }

    [Fact]
    public void UnloadedPopulationMetadataDoesNotReloadKnownPilots()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Add(
                new SavedCapsuleerSystem
                {
                    SystemId = Elsewhere,
                    Eligibility = CapsuleerEligibility.NoMarket,
                    CatalogFingerprint = "test",
                    PolicyVersion = CapsuleerProvisioningRules.Version,
                }
            );
            world.SaveChanges();
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.Run(0);
        long loaded = scheduler.PopulationRowsLoaded;
        var jobs = scheduler.Jobs().ToArray();
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldCapsuleerSystem SET PolicyVersion=PolicyVersion+1 WHERE SystemId={Elsewhere}");
        }
        scheduler.Run(0);
        Assert.Equal(loaded, scheduler.PopulationRowsLoaded);
        Assert.Equal(jobs, scheduler.Jobs());
    }
}
