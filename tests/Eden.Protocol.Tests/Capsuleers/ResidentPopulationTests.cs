using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class ResidentPopulationTests
{
    private const int Home = 30000141;
    private const int Remote = 30000144;
    private const long Human = 90000001;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PopulationMetadataUsesResidentCommitsAndPreservesRejectedChanges(bool commit)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var population = host.GetRequiredService<ICapsuleerPopulation>();
        var live = host.GetRequiredService<LiveWorld>();
        var original = population.Communities().Single(row => row.SystemId == Home);
        _ = population.Summary();
        long pilot = population.Agents(Home).First().PilotId;
        long originalCents = population.Agents(Home).Single(agent => agent.PilotId == pilot).StartingCents;
        int originalPaused = population.Summary().PausedAgents;
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident population metadata", _ => { }, force: true, command: commands.Add)!)
        {
            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Set<SavedCapsuleerPopulation>().Find(Home)!.Version++;
                action.Set<SavedCapsuleerAgent>().Find(pilot)!.StartingCents++;
                action.Add(new SavedCapsuleerSuspension { PilotId = pilot, StartedTicks = 0 });
                Assert.Equal(original.Version + 1, population.Communities().Single(row => row.SystemId == Home).Version);
                Assert.Equal(originalCents + 1, population.Agents(Home).Single(agent => agent.PilotId == pilot).StartingCents);
                Assert.Equal(originalPaused + 1, population.Summary().PausedAgents);
                if (commit)
                {
                    action.Commit();
                }
            }
            Assert.Equal(original.Version + (commit ? 1 : 0), population.Communities().Single(row => row.SystemId == Home).Version);
            Assert.Equal(originalCents + (commit ? 1 : 0), population.Agents(Home).Single(agent => agent.PilotId == pilot).StartingCents);
            Assert.Equal(originalPaused + (commit ? 1 : 0), population.Summary().PausedAgents);
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        live.ObjectsChanged();
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        captured.Project();
        using WorldDbContext reopened = new(captured.Database);
        Assert.Equal(original.Version + (commit ? 1 : 0), reopened.Set<SavedCapsuleerPopulation>().Find(Home)!.Version);
        Assert.Equal(originalCents + (commit ? 1 : 0), reopened.Set<SavedCapsuleerAgent>().Find(pilot)!.StartingCents);
    }

    [Fact]
    public void SchedulerRecreationPreservesUnprojectedCoarseCursorAndAdmissionsWithoutSql()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        long pilot = fixture.Founder().PilotId;
        using (WorldDbContext saved = new(fixture.Database.Path))
        {
            saved.Add(
                new SavedCapsuleerCoarseWork
                {
                    SystemId = Home,
                    Generation = 3,
                    NextTicks = TimeSpan.TicksPerMinute,
                }
            );
            saved.Add(new SavedCapsuleerCoarsePilot { PilotId = pilot });
            saved.SaveChanges();
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Home);
        var live = host.GetRequiredService<LiveWorld>();
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        _ = scheduler.Jobs();
        CapsuleerCoarseWork work = new(Home, 17, pilot, TimeSpan.TicksPerSecond, TimeSpan.TicksPerMinute);
        live.CapsuleerRoutines.Publish(work);
        live.CapsuleerRoutines.Settle(pilot, TimeSpan.TicksPerSecond);
        live.CapsuleerRoutines.Activate(Home, TimeSpan.TicksPerSecond);
        live.CapsuleerRoutines.Admit(pilot);
        var jobs = scheduler.Jobs().ToArray();
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident scheduler recreation", _ => { }, force: true, command: commands.Add)!)
        {
            var recreated = ActivatorUtilities.CreateInstance<CapsuleerScheduler>(host);
            Assert.Equal(jobs, recreated.Jobs());
            Assert.Same(work, live.CapsuleerRoutines.Work[Home]);
            Assert.Equal(TimeSpan.TicksPerSecond, live.CapsuleerRoutines.Settled[pilot]);
            Assert.Contains(pilot, live.CapsuleerRoutines.Admissions);
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        var pending = live.CapsuleerRoutines.Capture();
        Assert.Contains(work, pending.Work);
        Assert.Contains(pilot, pending.Admissions);
        Assert.Contains(pending.Activations, activation => activation.SystemId == Home);
    }

    [Fact]
    public void RemoteJobPreparationRejectsAnAdvancedCursorWithoutReplayingItsJob()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Home);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        var live = host.GetRequiredService<LiveWorld>();
        human.Transfer(Remote);
        scheduler.Run(0);
        fixture.Clock.Seconds += 180;
        var input = Assert.IsType<CapsuleerScheduler.PreparationInput>(scheduler.CapturePreparation());
        var work = Assert.IsType<CapsuleerCoarseWork>(input.Coarse);
        using (var metrics = SessionPerformance.Begin("warmed remote preparation", _ => { }, force: true)!)
        {
            var repeated = Assert.IsType<CapsuleerScheduler.PreparationInput>(scheduler.CapturePreparation());
            Assert.Equal(input.Job, repeated.Job);
            Assert.Equal(work, repeated.Coarse);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.True(scheduler.PreparationCurrent(input));
        live.CapsuleerRoutines.Publish(work with { Generation = work.Generation + 1 });
        Assert.False(scheduler.PreparationCurrent(input));
        Assert.Equal(input.Job, live.CapsuleerJobs[input.Job.PilotId]);
    }
}
