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
    public async Task LoginCatchupUsesTheEntryDeadlineEvenWhenEveryTurnAdvancesTheClock()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        AdvancingClock clock = new(f.Clock);
        using var host = ServerServices.Build(f.Database, clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Remote);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        _ = scheduler.Jobs();
        f.Clock.Seconds = 10;
        clock.Advance = true;
        int calls = 0;
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        try
        {
            await host.GetRequiredService<ICapsuleerHandoff>().SynchronizeAsync(() => calls++, cancellation.Token);
            Assert.Equal(1, calls);
            Assert.True(f.Clock.Seconds > 10);
            Assert.Contains(scheduler.Jobs(), j => j.DueTicks < host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks);
        }
        finally
        {
            clock.Advance = false;
        }
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
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.SettleAll(19));
        Assert.Equal(0, registry.Get(Destination).SettledTicks);
        registry.SettleAll(20);
        Assert.Equal(20, registry.Get(Destination).SettledTicks);
    }

    [Fact]
    public void BackgroundMiningMaterializesOnEntryAndLastObserverReleasesTheScene()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        using var first = presence.Select(Human, Remote);
        var paid = UntilMiner(host, f.Clock, m => m.Work.Activity == MinerActivity.Mining);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var handoff = host.GetRequiredService<ICapsuleerHandoff>();
        var presentation = host.GetRequiredService<ICapsuleerPresentation>();
        var scene = host.GetRequiredService<CapsuleerScene>();
        var space = host.GetRequiredService<SharedSpace>();
        Assert.Equal(0, scene.MaterializedPilots);
        Assert.Empty(Peers(host, paid.Pilot));
        Assert.Equal(Source, host.GetRequiredService<ChatRuntime>().User(paid.Pilot.PilotId)!.SystemId);
        var expected = scheduler.Jobs().ToArray();
        first.Transfer(Source);
        presentation.Refresh();
        var peer = Assert.Single(Peers(host, paid.Pilot), p => p.Pilot.PilotId == paid.Pilot.PilotId);
        Assert.Equal(paid.Work.TargetId, Assert.Single(peer.Mining!).Target);
        Assert.Equal(paid.Pilot.Position, peer.Pilot.Position);
        Assert.Equal(paid.Health, peer.Health);
        Assert.Equal(expected, scheduler.Jobs());
        using var second = presence.Select(Human + 1, Source);
        long generation = Assert.Single(handoff.Systems(), s => s.SystemId == Source).Generation;
        first.Transfer(Remote);
        presentation.Refresh();
        Assert.Equal(SystemSimulationMode.Active, Assert.Single(handoff.Systems(), s => s.SystemId == Source).Mode);
        Assert.Equal(generation, Assert.Single(handoff.Systems(), s => s.SystemId == Source).Generation);
        var stale = handoff.Capture();
        second.Dispose();
        presentation.Refresh();
        Assert.Equal(0, scene.MaterializedPilots);
        scene.Apply(stale);
        Assert.Equal(0, scene.MaterializedPilots);
        Assert.Empty(Peers(host, paid.Pilot));
        Assert.Equal(expected, scheduler.Jobs());
        Assert.Equal(paid.Work, scheduler.Miner(paid.Pilot.PilotId)!.Work);
        Assert.Equal(generation + 1, Assert.Single(handoff.Systems(), s => s.SystemId == Source).Generation);
    }

    [Fact]
    public void EnteringBackgroundWarpSamplesTheOriginalCourseWithoutExecutingItsArrival()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Remote);
        var flying = UntilMiner(host, f.Clock, m => m.Work.Navigation.Warp is not null);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var job = Assert.Single(scheduler.Jobs(), j => j.PilotId == flying.Pilot.PilotId);
        Assert.True(job.DueTicks > flying.Work.ActiveTicks + TimeSpan.TicksPerSecond);
        var frame = flying.Work.Navigation.Warp!;
        WarpCourse course = new(frame.Origin, frame.Destination, frame.StartStamp, flying.Pilot.Flight, frame.EntrySpeed);
        f.Clock.Seconds += 2;
        human.Transfer(Source);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        var sampled = scheduler.Miner(flying.Pilot.PilotId)!;
        Assert.Equal(flying.Work.Stamp + 2, sampled.Work.Stamp);
        var expected = course.Sample(sampled.Work.Stamp, Math.Min(flying.Pilot.Flight.MaxSpeed / 2, 100));
        Assert.False(expected.Exited);
        Assert.Equal(expected.Position, sampled.Pilot.Position);
        Assert.Equal(expected.Velocity, sampled.Work.Navigation.Velocity);
        Assert.Equal(frame, sampled.Work.Navigation.Warp);
        Assert.Equal(job, Assert.Single(scheduler.Jobs(), j => j.PilotId == flying.Pilot.PilotId));
        var peer = Assert.Single(Peers(host, sampled.Pilot), p => p.Pilot.PilotId == sampled.Pilot.PilotId);
        Assert.NotNull(peer.Warp);
        Assert.Equal(sampled.Pilot.Position, peer.Pilot.Position);
        f.Clock.Seconds++;
        host.GetRequiredService<CapsuleerMaintenance>().Tick();
        var advancing = scheduler.Miner(flying.Pilot.PilotId)!;
        Assert.Equal(sampled.Work.Stamp + 1, advancing.Work.Stamp);
        Assert.Equal(
            course.Sample(advancing.Work.Stamp, Math.Min(flying.Pilot.Flight.MaxSpeed / 2, 100)).Position,
            advancing.Pilot.Position
        );
        Assert.Equal(job, Assert.Single(scheduler.Jobs(), j => j.PilotId == flying.Pilot.PilotId));
        Assert.Equal(
            advancing.Pilot.Position,
            Assert.Single(Peers(host, advancing.Pilot), p => p.Pilot.PilotId == advancing.Pilot.PilotId).Pilot.Position
        );
    }

    [Fact]
    public void GateHandoffAtTheExactDeadlineTransfersCargoOnceAndPublishesArrival()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        long miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
        f.AddStock(miner, 1000);
        host.GetRequiredService<CapsuleerTrade>().ListStock(miner, SourceStation);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Remote);
        HaulerObservation jumping;
        do
        {
            jumping = CapsuleerTestDriver.Hauler(host, f.Clock);
        } while (jumping.Work.Activity != HaulerActivity.Jumping);
        var job = Assert.Single(scheduler.Jobs(), j => j.PilotId == jumping.Pilot.PilotId);
        var inventory = host.GetRequiredService<IPilotInventory>();
        var cargo = inventory.List(jumping.Pilot.PilotId, jumping.Pilot.ShipId).ToArray();
        f.Clock.Seconds += 3;
        human.Transfer(Source);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(HaulerActivity.Jumping, scheduler.Hauler(jumping.Pilot.PilotId)!.Work.Activity);
        var departure = Assert.Single(Peers(host, jumping.Pilot), p => p.Pilot.PilotId == jumping.Pilot.PilotId);
        Assert.Equal(jumping.Work.TargetId, departure.DepartureGate);
        f.Clock.Milliseconds +=
            (job.DueTicks - host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks) / TimeSpan.TicksPerMillisecond;
        human.Transfer(Destination);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        var arrived = scheduler.Hauler(jumping.Pilot.PilotId)!;
        Assert.Equal(Destination, arrived.Pilot.SystemId);
        Assert.Equal(cargo, inventory.List(arrived.Pilot.PilotId, arrived.Pilot.ShipId));
        Assert.Equal(job.Sequence + 1, Assert.Single(scheduler.Jobs(), j => j.PilotId == arrived.Pilot.PilotId).Sequence);
        var peer = Assert.Single(Peers(host, arrived.Pilot), p => p.Pilot.PilotId == arrived.Pilot.PilotId);
        Assert.True(peer.Arriving);
        Assert.Equal(0, scheduler.Run(0).Turn.Completed);
    }

    [Fact]
    public async Task RestartRetainsPaidWorkAndJobsButRebuildsObserverModesFromSelections()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        MinerObservation paid;
        CapsuleerJob[] jobs;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
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
        Assert.Equal(jobs, scheduler.Jobs());
        Assert.Equal(paid.Work, scheduler.Miner(paid.Pilot.PilotId)!.Work);
        Assert.Equal(0, reopened.GetRequiredService<CapsuleerScene>().MaterializedPilots);
        using var selected = reopened.GetRequiredService<IHumanPresence>().Select(Human, Source);
        reopened.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(jobs, scheduler.Jobs());
        var peer = Assert.Single(Peers(reopened, paid.Pilot), p => p.Pilot.PilotId == paid.Pilot.PilotId);
        Assert.Single(peer.Mining!);
        Assert.Equal(paid.Work.Cycles, scheduler.Miner(paid.Pilot.PilotId)!.Work.Cycles);
    }

    [Fact]
    public async Task EntryFailureKeepsOldMembershipAndAsyncRetrySettlesBeforeTheCommand()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Remote);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        long miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_handoff BEFORE UPDATE ON WorldCapsuleerJob
                BEGIN SELECT RAISE(ABORT,'handoff failure'); END
                """
            );
        }
        f.Clock.Seconds++;
        Assert.Throws<SqliteException>(() => human.Transfer(Source));
        Assert.Equal(new[] { Remote }, host.GetRequiredService<IHumanPresence>().Read().ActiveSystems);
        Assert.Equal(0, scheduler.Jobs().Single(j => j.PilotId == miner).Sequence);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_handoff");
        }
        f.Clock.Seconds += 100;
        bool called = false;
        var settlement = host.GetRequiredService<ICapsuleerHandoff>()
            .SynchronizeAsync(() =>
            {
                called = true;
                long through = host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks;
                Assert.All(scheduler.Jobs(), job => Assert.True(job.DueTicks > through));
                Assert.Equal(0, scheduler.Run(0).Turn.Completed);
            });
        Assert.False(settlement.IsCompleted);
        await settlement;
        Assert.True(called);
        human.Transfer(Source);
        Assert.Equal(new[] { Source }, host.GetRequiredService<IHumanPresence>().Read().ActiveSystems);
    }

    [Fact]
    public void WarmBackgroundFramesHaveNoSqlFittingOrNativeScenesAndUseTheWorldMotionGate()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Remote);
        UntilMiner(host, f.Clock, m => m.Work.Activity == MinerActivity.Mining);
        Assert.Same(host.GetRequiredService<IWorldSynchronization>().Gate, host.GetRequiredService<SharedSpace>().MotionLock);
        var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
        using var metrics = SessionPerformance.Begin("warm background handoff", output.WriteLine, force: true)!;
        for (int i = 0; i < 10; i++)
        {
            f.Clock.Milliseconds += 50;
            maintenance.Tick();
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
        Assert.Equal(0, host.GetRequiredService<CapsuleerScene>().MaterializedPilots);
    }

    [Theory]
    [InlineData(MinerActivity.FlyingToBelt)]
    [InlineData(MinerActivity.ApproachingOre)]
    [InlineData(MinerActivity.Locking)]
    [InlineData(MinerActivity.Mining)]
    [InlineData(MinerActivity.Returning)]
    public void RepeatedHandoffsRetainNavigationTargetsAndPaidProgress(MinerActivity phase)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Remote);
        var before = UntilMiner(host, f.Clock, m => m.Work.Activity == phase);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
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
        Assert.Equal(before, after);
        Assert.Equal(jobs, scheduler.Jobs());
    }

    [Fact]
    public void ActiveAndBackgroundMiningHaveEqualOutcomesAtTheSameClockBoundary()
    {
        static (MinerProgress Work, SpacePosition? Position, int Units) Simulate(bool background)
        {
            using CapsuleerHaulerTests.Fixture f = new();
            using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
            using var human = host.GetRequiredService<IHumanPresence>().Select(Human, background ? Remote : Source);
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            long miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
            for (int i = 0; i < 240; i++)
            {
                f.Clock.Seconds++;
                worker.Tick();
            }
            human.Transfer(Source);
            host.GetRequiredService<ICapsuleerPresentation>().Refresh();
            var observation = scheduler.Miner(miner)!;
            return (
                observation.Work,
                observation.Pilot.Position,
                host.GetRequiredService<IPilotInventory>().List(miner, observation.Pilot.ShipId).Sum(i => i.Quantity)
            );
        }
        var active = Simulate(false);
        var background = Simulate(true);
        Assert.Equal(active.Work with { PaidUntil = 0 }, background.Work with { PaidUntil = 0 });
        Assert.Equal(active.Position, background.Position);
        Assert.Equal(active.Units, background.Units);
        Assert.True(active.Work.Cycles > 0);
    }

    [Fact]
    public async Task HandoffDropsObserverCachesAndCheckpointsUnresolvedNpcWorkWithoutReplacingIt()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Source);
        var paid = UntilMiner(host, f.Clock, m => m.Work.Activity == MinerActivity.Mining);
        const long attacker = 700000001;
        const long missileId = 700000002;
        long now = f.Clock.Now;
        var position = paid.Pilot.Position!;
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Add(
                new WorldNpc
                {
                    Id = attacker,
                    TypeId = 601,
                    SystemId = Source,
                    X = position.X + 1000,
                    Y = position.Y,
                    Z = position.Z,
                    HomeX = position.X + 1000,
                    HomeY = position.Y,
                    HomeZ = position.Z,
                    MotionAt = now,
                    UpdatedAt = now,
                }
            );
            world.Add(
                new NpcAggression
                {
                    NpcId = attacker,
                    TargetShipId = paid.Pilot.ShipId,
                    ReadyAt = now + TimeSpan.TicksPerMinute,
                    MissileReadyAt = now + TimeSpan.TicksPerMinute,
                }
            );
            NpcEwarPower power = new(SignaturePercent: 10);
            NpcEwarAbility ability = new(NpcEwarKind.TargetPainting, DogmaEffect.TargetPainter, 10000, 20000, 0, 1, power);
            world.Add(
                new WorldNpcEwar
                {
                    NpcId = attacker,
                    Kind = ability.Kind,
                    TypeId = 601,
                    TargetShipId = paid.Pilot.ShipId,
                    StartedAt = now,
                    ReadyAt = now + TimeSpan.TicksPerSecond * 10,
                    AbilityJson = JsonSerializer.Serialize(ability),
                    AppliedJson = JsonSerializer.Serialize(power),
                }
            );
            WorldMissile missile = new()
            {
                Id = missileId,
                SourceKind = MissileSourceKind.Npc,
                SourceTypeId = 601,
                SourceShipId = attacker,
                PilotId = paid.Pilot.PilotId,
                TargetId = paid.Pilot.ShipId,
                SystemId = Source,
                LaunchedAt = now,
                UpdatedAt = now,
                ExpiresAt = now + TimeSpan.TicksPerMinute,
                Snapshot = "",
            };
            missile.Save(
                new(
                    new(new(1000, 1, 100, 1, 60000), position + new SpacePosition(1000, 0, 0), SpacePosition.Zero, position),
                    new(new(1, 0, 0, 0), 10, 100, 1)
                )
            );
            world.Add(missile);
            world.SaveChanges();
        }
        f.Database.MotionStamps[Source] = host.GetRequiredService<ICapsuleerScene>().Stamp;
        Assert.NotEmpty(host.GetRequiredService<INpcEncounters>().List(Source, now));
        var combat = host.GetRequiredService<LiveWorld>().Combat!;
        Assert.True(f.Database.MotionStamps.ContainsKey(Source));
        string Snapshot()
        {
            using WorldDbContext world = new(f.Database.Path);
            return JsonSerializer.Serialize(
                new
                {
                    Aggression = world.Set<NpcAggression>().AsNoTracking().Single(a => a.NpcId == attacker),
                    Ewar = world.Set<WorldNpcEwar>().AsNoTracking().Single(e => e.NpcId == attacker),
                    Missile = world.Set<WorldMissile>().AsNoTracking().Single(m => m.Id == missileId),
                }
            );
        }
        string before = Snapshot();
        human.Transfer(Remote);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(0, combat.ObserverContactCount(Source));
        Assert.False(f.Database.MotionStamps.ContainsKey(Source));
        Assert.Equal(before, Snapshot());
        human.Transfer(Source);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(before, Snapshot());
        await host.GetRequiredService<LiveWorld>().FlushAsync();
        using SqliteConnection file = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = f.Database.Path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        using WorldDbContext saved = new(file);
        Assert.Equal(now, saved.Set<WorldMissile>().Single(m => m.Id == missileId).UpdatedAt);
        Assert.Equal(now + TimeSpan.TicksPerSecond * 10, saved.Set<WorldNpcEwar>().Single(e => e.NpcId == attacker).ReadyAt);
    }

    [Fact]
    public void BackgroundNpcDisruptionPreventsEscapeAndHandoffPreservesItsDeadline()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        PassiveCatalogFixture.LoadNpcPopulation(f.Database.CatalogPath);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO TypeAttribute VALUES (13032,504,1),(13032,105,10);
                """
            );
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Remote);
        var paid = UntilMiner(host, f.Clock, m => m.Work.Activity == MinerActivity.Mining);
        const long attacker = 700000001;
        var position = paid.Pilot.Position!;
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Add(
                new WorldNpc
                {
                    Id = attacker,
                    TypeId = 13032,
                    SystemId = Source,
                    X = position.X + 1000,
                    Y = position.Y,
                    Z = position.Z,
                    HomeX = position.X + 1000,
                    HomeY = position.Y,
                    HomeZ = position.Z,
                    MotionAt = f.Clock.Now,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.Add(
                new NpcAggression
                {
                    NpcId = attacker,
                    TargetShipId = paid.Pilot.ShipId,
                    ReadyAt = f.Clock.Now,
                    MissileReadyAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        var blocked = CapsuleerTestDriver.Miner(host, f.Clock);
        var effect = Assert.Single(
            host.GetRequiredService<INpcElectronicWarfare>().Incoming(paid.Pilot.ShipId),
            e => e.Ability.Kind == NpcEwarKind.WarpDisruption
        );
        Assert.Equal(MinerActivity.Waiting, blocked.Work.Activity);
        Assert.Contains("Warp disruption", blocked.Work.WaitingReason);
        Assert.Null(blocked.Work.Navigation.Warp);
        Assert.Equal(paid.Work.Cycles, blocked.Work.Cycles);
        human.Transfer(Source);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        human.Transfer(Remote);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(
            effect,
            Assert.Single(
                host.GetRequiredService<INpcElectronicWarfare>().Incoming(paid.Pilot.ShipId),
                e => e.Ability.Kind == NpcEwarKind.WarpDisruption
            )
        );
        Assert.Equal(blocked, host.GetRequiredService<ICapsuleerScheduler>().Miner(paid.Pilot.PilotId));
    }

    [Fact]
    public void FailedDueWorkDuringDisconnectStillRemovesTheHumanAndPausesTheClock()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var human = host.GetRequiredService<IHumanPresence>().Select(Human, Source);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_disconnect BEFORE UPDATE ON WorldCapsuleerJob
                BEGIN SELECT RAISE(ABORT,'disconnect failure'); END
                """
            );
        }
        f.Clock.Seconds++;
        Assert.Throws<SqliteException>(() => human.Dispose());
        Assert.False(host.GetRequiredService<IHumanPresence>().Read().Active);
        long paused = host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks;
        f.Clock.Seconds += 3600;
        Assert.Equal(paused, host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.Equal(0, host.GetRequiredService<CapsuleerScene>().MaterializedPilots);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_disconnect");
        }
        host.GetRequiredService<ICapsuleerHandoff>().Synchronize(() => { });
        Assert.All(host.GetRequiredService<ICapsuleerScheduler>().Jobs(), j => Assert.True(j.DueTicks > paused));
        Assert.Equal(
            SystemSimulationMode.Background,
            Assert.Single(host.GetRequiredService<ICapsuleerHandoff>().Systems(), s => s.SystemId == Source).Mode
        );
        human.Dispose();
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
            host.GetRequiredService<IWorldSynchronization>()
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
        Assert.Equal(jobs, scheduler.Jobs());
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
        using CapsuleerHaulerTests.Fixture f = new();
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
