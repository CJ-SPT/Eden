using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerHandoffTests(ITestOutputHelper output)
{
    private const int Source = 30000141;
    private const int Destination = 30000142;
    private const int Remote = 30000144;
    private const int SourceStation = 60015157;
    private const long Human = 90000001;

    [Fact]
    public async Task UnrelatedSystemEntryLeavesSavedBacklogUntouched()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        using var first = presence.Select(Human, Remote);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var before = scheduler.Jobs().ToArray();
        using WorldDbContext dormant = new(f.Database.Path);
        var savedBefore = CapsuleerPopulationStore.Jobs(dormant);
        f.Clock.Seconds = 128;
        int calls = 0;
        await host.GetRequiredService<ICapsuleerHandoff>().SynchronizeAsync(Remote, () => calls++);
        Assert.Equal(1, calls);
        Assert.Equal(before, scheduler.Jobs());
        Assert.Contains(savedBefore, j => j.DueTicks < presence.Read().ElapsedTicks);
        Assert.Equal(savedBefore, CapsuleerPopulationStore.Jobs(dormant));
        Assert.Empty(host.GetRequiredService<LiveWorld>().MinerCheckpoints);
        Assert.Empty(host.GetRequiredService<LiveWorld>().HaulerCheckpoints);
    }

    [Fact]
    public async Task DetachedJobProjectionRetainsDifferencesAcrossSuccessfulSavesAndFailedProjection()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(Source);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var original = scheduler.Jobs().First();
        var live = host.GetRequiredService<LiveWorld>();
        var next = original.Next(original.DueTicks + TimeSpan.TicksPerSecond, original.Action);
        live.CapsuleerJobs[original.PilotId] = next;
        live.ObjectsChanged();
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            using (var command = snapshot.Database.CreateCommand())
            {
                command.CommandText = "CREATE TRIGGER RejectJob BEFORE UPDATE ON WorldCapsuleerJob BEGIN SELECT RAISE(ABORT,'test'); END";
                command.ExecuteNonQuery();
            }
            Assert.Throws<SqliteException>(() => snapshot.Project());
            using WorldDbContext captured = new(snapshot.Database);
            Assert.Equal(
                original.Sequence,
                captured
                    .Set<Eden.Data.World.Capsuleers.SavedCapsuleerJob>()
                    .AsNoTracking()
                    .Single(j => j.PilotId == original.PilotId)
                    .Sequence
            );
        }
        await live.FlushAsync();
        // An unrelated change causes a new backup of the resident SQL image.
        // Previously saved resident job state must still be projected into that backup.
        live.ObjectsChanged();
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            snapshot.Project();
            using WorldDbContext captured = new(snapshot.Database);
            Assert.Equal(
                next.Sequence,
                captured
                    .Set<Eden.Data.World.Capsuleers.SavedCapsuleerJob>()
                    .AsNoTracking()
                    .Single(j => j.PilotId == original.PilotId)
                    .Sequence
            );
        }
        await live.FlushAsync();
        // Read the on-disk image separately from the registered resident world.
        using SqliteConnection disk = new($"Data Source={f.Database.Path};Mode=ReadOnly;Pooling=False");
        disk.Open();
        using var query = disk.CreateCommand();
        query.CommandText = $"SELECT Sequence FROM WorldCapsuleerJob WHERE PilotId={original.PilotId}";
        Assert.Equal(next.Sequence, (long)query.ExecuteScalar()!);
    }

    private sealed class AdvancingClock(TestClock inner) : TimeProvider
    {
        public bool Advance { get; set; }
        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp()
        {
            if (Advance)
            {
                inner.Seconds += 10;
            }
            return inner.GetTimestamp();
        }
    }

    [Fact]
    public void RegistryUsesExplicitTransitionsAndRejectsRewindingSettledTime()
    {
        SystemSimulationRegistry registry = new();
        registry.Register(Source);
        Assert.Equal(SystemSimulationMode.Background, registry.Get(Source).Mode);
        Assert.Equal(SystemSimulationMode.Activating, Assert.Single(registry.Begin([Source])).Mode);
        Assert.Empty(registry.Begin([Source]));
        registry.Settle(Source, 10);
        Assert.Equal(new(Source, SystemSimulationMode.Active, 1, 10), registry.Get(Source));
        Assert.Equal(SystemSimulationMode.Deactivating, Assert.Single(registry.Begin([])).Mode);
        registry.Settle(Source, 20);
        Assert.Equal(new(Source, SystemSimulationMode.Background, 2, 20), registry.Get(Source));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Settle(Source, 19));
        registry.Begin([Source]);
        registry.Settle(Source, 20);
        Assert.Equal(3, registry.Get(Source).Generation);
        registry.Register(Destination);
        registry.SettleAll(19);
        Assert.Equal(20, registry.Get(Source).SettledTicks);
        Assert.Equal(19, registry.Get(Destination).SettledTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.SettleAll(18));
        registry.SettleAll(20);
        Assert.Equal(20, registry.Get(Destination).SettledTicks);
    }

    [Fact]
    public async Task RestartRetainsPaidWorkAndJobsButRebuildsObserverModesFromSelections()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        MinerObservation paid;
        CapsuleerJob[] jobs;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(Source);
            using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Remote);
            paid = UntilMiner(host, f.Clock, m => m.Work.Activity == MinerActivity.Mining);
            jobs = host.GetRequiredService<ICapsuleerScheduler>().Jobs().ToArray();
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        f.Clock.Seconds += 86400;
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var scheduler = reopened.GetRequiredService<ICapsuleerScheduler>();
        Assert.All(reopened.GetRequiredService<ICapsuleerHandoff>().Systems(), s => Assert.Equal(SystemSimulationMode.Background, s.Mode));
        Assert.Equal(jobs.OrderBy(j => j.PilotId), scheduler.Jobs().OrderBy(j => j.PilotId));
        using (WorldDbContext saved = new(f.Database.Path))
        {
            Assert.Equal(jobs.OrderBy(job => job.PilotId), CapsuleerPopulationStore.Jobs(saved).OrderBy(job => job.PilotId));
            var miner = saved.Set<Eden.Data.World.Capsuleers.SavedCapsuleerCommunity>().Single(row => row.PilotId == paid.Pilot.PilotId);
            Assert.Equal(paid.Work, MinerCheckpoint.Read(miner.Progress));
        }
        Assert.Equal(0, reopened.GetRequiredService<CapsuleerScene>().MaterializedPilots);
        using var selected = reopened.GetRequiredService<IHumanPresence>().Select(Human, Source);
        reopened.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(jobs.Select(j => j.PilotId).Order(), scheduler.Jobs().Select(j => j.PilotId).Order());
        Assert.All(scheduler.Jobs(), j => Assert.True(j.DueTicks > reopened.GetRequiredService<IHumanPresence>().Read().ElapsedTicks));
        var peer = Assert.Single(Peers(reopened, paid.Pilot), p => p.Pilot.PilotId == paid.Pilot.PilotId);
        Assert.Single(peer.Mining!);
        Assert.Equal(paid.Work.Cycles, scheduler.Miner(paid.Pilot.PilotId)!.Work.Cycles);
    }

    [Theory]
    [InlineData(MinerActivity.FlyingToBelt)]
    [InlineData(MinerActivity.ApproachingOre)]
    [InlineData(MinerActivity.Locking)]
    [InlineData(MinerActivity.Mining)]
    [InlineData(MinerActivity.Returning)]
    public void RepeatedHandoffsRetainNavigationTargetsAndPaidProgress(MinerActivity phase)
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Source);
        var before = UntilMiner(host, f.Clock, m => m.Work.Activity == phase);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        human.Transfer(Remote);
        var jobs = scheduler.Jobs().ToArray();
        for (int i = 0; i < 3; i++)
        {
            human.Transfer(Source);
            host.GetRequiredService<ICapsuleerPresentation>().Refresh();
            Assert.Contains(Peers(host, before.Pilot), p => p.Pilot.PilotId == before.Pilot.PilotId);
            human.Transfer(Remote);
            host.GetRequiredService<ICapsuleerPresentation>().Refresh();
            Assert.Empty(Peers(host, before.Pilot));
        }
        var after = scheduler.Miner(before.Pilot.PilotId)!;
        output.WriteLine(
            $"Handoff {phase}: {before.Work.Stamp}/{before.Work.ActiveTicks}/{before.Work.DueTicks} -> "
                + $"{after.Work.Stamp}/{after.Work.ActiveTicks}/{after.Work.DueTicks}; "
                + $"X {before.Pilot.Position?.X:R} -> {after.Pilot.Position?.X:R}; "
                + $"Y {before.Pilot.Position?.Y:R} -> {after.Pilot.Position?.Y:R}; "
                + $"Z {before.Pilot.Position?.Z:R} -> {after.Pilot.Position?.Z:R}"
        );
        Assert.Equal(before.Pilot.ShipId, after.Pilot.ShipId);
        Assert.Equal(before.Pilot.Position, after.Pilot.Position);
        var json = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        var rebased = before.Work with
        {
            ActiveTicks = after.Work.ActiveTicks,
            DueTicks = after.Work.DueTicks,
            ClockOffsetTicks = after.Work.ClockOffsetTicks,
        };
        Assert.Equal(JsonSerializer.Serialize(rebased, json), JsonSerializer.Serialize(after.Work, json));
        Assert.Equal(jobs.Select(j => j.PilotId).Order(), scheduler.Jobs().Select(j => j.PilotId).Order());
    }

    [Fact]
    public void FailedInitialSceneConstructionReleasesItsSelectionAndNativeBotScenes()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var human = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        FlightSessionFactory factory = new(
            host.GetRequiredService<IShipPositionStore>(),
            f.Clock,
            host.GetRequiredService<SharedSpace>(),
            new UnavailableObjects(),
            host.GetRequiredService<IPilotInventory>(),
            host.GetRequiredService<IStationDocking>(),
            host.GetRequiredService<IPilotHealth>(),
            host.GetRequiredService<INpcEncounters>(),
            host.GetRequiredService<Eden.Gameplay.Loot.IWrecks>(),
            host.GetRequiredService<IPilotShipAppearance>(),
            presence: host.GetRequiredService<IHumanPresence>(),
            presentation: host.GetRequiredService<ICapsuleerPresentation>()
        );
        Assert.Throws<InvalidOperationException>(() => factory.Create(human));
        Assert.False(host.GetRequiredService<IHumanPresence>().Read().Active);
        Assert.Equal(0, host.GetRequiredService<CapsuleerScene>().MaterializedPilots);
        Assert.Equal(
            SystemSimulationMode.Background,
            Assert.Single(host.GetRequiredService<ICapsuleerHandoff>().Systems(), s => s.SystemId == human.SystemId).Mode
        );
    }

    private sealed class UnavailableObjects : ISystemObjects
    {
        public FlightSystem GetSystem(int systemId) => throw new InvalidOperationException("Initial geometry unavailable.");

        public IReadOnlyList<StaticSpaceObject> Load(int systemId) => throw new InvalidOperationException("Initial scene unavailable.");
    }

    [Fact]
    public void FailedPresentationRetriesCommittedObservationsWithoutExecutingTheirJobsAgain()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Source);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        using CapsuleerMaintenance maintenance = new(
            scheduler,
            host.GetRequiredService<ICapsuleerHandoff>(),
            new FailingScene(host.GetRequiredService<ICapsuleerScene>()),
            host.GetRequiredService<ChatRuntime>(),
            f.Clock,
            host.GetRequiredService<IWorldSynchronization>(),
            host.GetRequiredService<ICapsuleerUpdateDiagnostics>()
        );
        f.Clock.Seconds++;
        Assert.Throws<InvalidOperationException>(() => maintenance.Tick());
        var jobs = scheduler.Jobs().ToArray();
        Assert.All(jobs, j => Assert.Equal(1, j.Sequence));
        maintenance.Refresh();
        var miner = scheduler.Miner(
            host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId
        )!;
        Assert.Contains(Peers(host, miner.Pilot), p => p.Pilot.PilotId == miner.Pilot.PilotId);
        Assert.Equal(jobs, scheduler.Jobs());
        Assert.Null(maintenance.Tick());
        Assert.Equal(jobs.Select(j => j.PilotId).Order(), scheduler.Jobs().Select(j => j.PilotId).Order());
    }

    private sealed class FailingScene(ICapsuleerScene actual) : ICapsuleerScene
    {
        private bool _failed;
        public int Stamp => actual.Stamp;

        public void Publish(MinerObservation observation) => actual.Publish(observation);

        public void Publish(HaulerObservation observation) => actual.Publish(observation);

        public void Apply(CapsuleerBatch batch)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("First presentation failed.");
            }
            actual.Apply(batch);
        }
    }

    [Fact]
    public void EnteringAnUnrelatedSystemDoesNotHydrateBackgroundFitsOrNavigation()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        _ = host.GetRequiredService<ICapsuleerScheduler>().Jobs();
        using var human = presence.Select(Human, Remote);
        using var metrics = SessionPerformance.Begin("remote activation metadata", output.WriteLine, force: true)!;
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
        Assert.Equal(0, host.GetRequiredService<CapsuleerScene>().MaterializedPilots);
        Assert.Empty(host.GetRequiredService<LiveWorld>().MinerCheckpoints);
        Assert.Empty(host.GetRequiredService<LiveWorld>().HaulerCheckpoints);
    }

    private static IReadOnlyList<ShipContact> Peers(ServiceProvider host, DockedPilot bot)
    {
        var human = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        return host.GetRequiredService<SharedSpace>().Peers(human with { Position = bot.Position, CurrentSystem = bot.CurrentSystem });
    }

    private static MinerObservation UntilMiner(ServiceProvider host, TestClock clock, Func<MinerObservation, bool> matches)
    {
        for (int i = 0; i < 10000; i++)
        {
            var miner = CapsuleerTestDriver.Miner(host, clock);
            if (matches(miner))
            {
                return miner;
            }
        }
        throw new InvalidOperationException("The miner did not reach the requested handoff phase.");
    }
}
