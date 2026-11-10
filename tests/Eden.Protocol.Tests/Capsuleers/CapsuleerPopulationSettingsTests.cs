using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Configuration;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerPopulationSettingsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Source = 30000141;
    private static readonly WatchedTrafficGate s_gate = new(Source, 30000142, 50009991, 50009992);

    [Fact]
    public void RestartSuspendsAndReusesSavedPilotsWithoutChangingTheirPossessionsOrReplayingJobs()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        var policy = new CapsuleerPopulationPolicy(TrafficMultiplier: 2, GateVisitors: 6);
        Save(fixture, policy);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        CapsuleerProvisioner provision = new(fixture.Database, fixture.Clock);
        provision.EnsureSystem(Source);
        long[] visitors = Enumerable.Range(0, 6).Select(v => provision.EnsureInbound(s_gate, visitor: v)!.Value).ToArray();
        Assert.Equal(6, visitors.Distinct().Count());
        var original = Assets(fixture);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            var job = world.Set<SavedCapsuleerJob>().Find(visitors[5])!;
            job.DueTicks = TimeSpan.FromMinutes(10).Ticks;
            var row = world.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == visitors[5]);
            var work = HaulerCheckpoint.Read(row.Progress)!;
            new HaulerCheckpoint(row.PilotId, work with { DueTicks = job.DueTicks }).Apply(world);
            world.SaveChanges();
        }
        Save(fixture, policy with { Enabled = false });
        using (WorldDbContext pending = new(fixture.Database.Path))
        {
            Assert.Empty(pending.Set<SavedCapsuleerSuspension>());
            Assert.True(pending.Settings.Capsuleers.Enabled);
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        Assert.Equal(original, Assets(fixture));
        var disabledSummary = new CapsuleerPopulationStore(fixture.Database).Summary();
        Assert.Equal(original.Pilots.Length, disabledSummary.PausedAgents);
        Assert.Equal(0, disabledSummary.EnabledAgents);
        Assert.Equal(0, disabledSummary.PreparedSystems);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            Assert.Equal(world.Set<SavedCapsuleerAgent>().Count(), world.Set<SavedCapsuleerSuspension>().Count());
        }
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            using var observer = host.GetRequiredService<IHumanPresence>().Select(90000001, Source);
            var handoff = host.GetRequiredService<ICapsuleerHandoff>();
            var batch = handoff.Capture();
            Assert.Empty(batch.Miners);
            Assert.Empty(batch.Haulers);
            fixture.Clock.Seconds += 180;
            Assert.Equal(0, host.GetRequiredService<ICapsuleerScheduler>().Run(180).Turn.Completed);
            Assert.Equal(original, Assets(fixture));
        }
        // Reopening while still disabled must preserve the original suspension start.
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long elapsed;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            elapsed = world.Set<SavedCapsuleerClock>().Single().ElapsedTicks;
            Assert.True(elapsed >= TimeSpan.FromMinutes(3).Ticks);
            Assert.All(world.Set<SavedCapsuleerSuspension>(), s => Assert.Equal(0, s.StartedTicks));
        }
        Save(fixture, policy with { TrafficMultiplier = 1, GateVisitors = 2, Miners = false });
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            var suspended = world.Set<SavedCapsuleerSuspension>().Select(s => s.PilotId).ToHashSet();
            Assert.DoesNotContain(visitors[0], suspended);
            Assert.DoesNotContain(visitors[1], suspended);
            Assert.All(visitors.Skip(2), id => Assert.Contains(id, suspended));
            int count = CapsuleerEncounterRules.Haulers(1, Source, 1, 1, new(TrafficMultiplier: 1));
            Assert.All(
                world
                    .Set<SavedCapsuleerAgent>()
                    .Where(a =>
                        a.CommunitySystemId == Source
                        && a.Career == CapsuleerCareer.Hauling
                        && a.Slot < CapsuleerPopulationRules.MaximumHomeRosterSlots
                    ),
                a => Assert.Equal(a.Slot >= count, suspended.Contains(a.PilotId))
            );
        }
        Save(fixture, policy);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        provision.EnsureSystem(Source);
        Assert.Equal(visitors, Enumerable.Range(0, 6).Select(v => provision.EnsureInbound(s_gate, visitor: v)!.Value));
        Assert.Equal(original, Assets(fixture));
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            Assert.Empty(world.Set<SavedCapsuleerSuspension>());
            var job = world.Set<SavedCapsuleerJob>().Find(visitors[5])!;
            Assert.Equal(TimeSpan.FromMinutes(10).Ticks + elapsed, job.DueTicks);
            Assert.Equal(
                job.DueTicks,
                HaulerCheckpoint.Read(world.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == visitors[5]).Progress)!.DueTicks
            );
        }
        using var resumed = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var presence = resumed.GetRequiredService<IHumanPresence>().Select(90000001, Source);
        var visible = resumed.GetRequiredService<ICapsuleerHandoff>().Capture();
        Assert.NotEmpty(visible.Haulers);
        Assert.All(visible.Haulers, h => Assert.Contains(h.Pilot.PilotId, original.Pilots));
    }

    [Fact]
    public void SuspendedPilotsHaveNoSpaceStationLocalOrScanPresenceAndWarmFramesDoNoDatabaseWork()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioner provision = new(fixture.Database, fixture.Clock);
        provision.EnsureSystem(Source);
        long visitor = provision.EnsureInbound(s_gate, sourceObserved: true)!.Value;
        long human;
        long ship;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == visitor);
            ship = pilot.ActiveShipId;
            pilot.ActiveShip.Position ??= new FlightPosition { ShipId = ship };
            pilot.ActiveShip.Position.SystemId = Source;
            pilot.ActiveShip.Position.X = 0;
            pilot.ActiveShip.Position.Y = 0;
            pilot.ActiveShip.Position.Z = 0;
            var observer = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == 90000001);
            human = observer.Id;
            observer.ActiveShip.Position ??= new FlightPosition { ShipId = observer.ActiveShipId };
            observer.ActiveShip.Position.SystemId = Source;
            observer.ActiveShip.Position.X = 100;
            observer.ActiveShip.Position.Y = 0;
            observer.ActiveShip.Position.Z = 0;
            world.SaveChanges();
        }
        using (var enabled = ServerServices.Build(fixture.Database, fixture.Clock))
        {
            Assert.Contains(
                enabled
                    .GetRequiredService<IPilotInformation>()
                    .Directional(human, Math.Tau, 10000, new(1, 0, 0), fixture.Clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
                contact => contact.Id == ship
            );
        }
        Save(fixture, new(Enabled: false));
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var presence = host.GetRequiredService<IHumanPresence>().Select(human, Source);
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        worker.Tick();
        Assert.Equal(0, host.GetRequiredService<CapsuleerScene>().MaterializedPilots);
        Assert.False(host.GetRequiredService<SharedStationGuests>().IsOnline(visitor));
        Assert.Null(host.GetRequiredService<ChatRuntime>().User(visitor));
        Assert.DoesNotContain(
            host.GetRequiredService<IPilotInformation>()
                .Directional(human, Math.Tau, 10000, new(1, 0, 0), fixture.Clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            contact => contact.Id == ship
        );
        using var metrics = SessionPerformance.Begin("100 warmed disabled-population frames", output.WriteLine, force: true)!;
        for (int i = 0; i < 100; i++)
        {
            fixture.Clock.Seconds++;
            worker.Tick();
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void MaximumVisitorsShareReciprocalIdentitiesAndPopulationResumeRollsBackAtomically()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        Save(fixture, new(GateVisitors: CapsuleerPopulationRules.MaximumGateVisitors));
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        CapsuleerProvisioner provision = new(fixture.Database, fixture.Clock);
        long[] visitors = Enumerable
            .Range(0, CapsuleerPopulationRules.MaximumGateVisitors)
            .Select(v => provision.EnsureInbound(s_gate, visitor: v)!.Value)
            .ToArray();
        Assert.Equal(CapsuleerPopulationRules.MaximumGateVisitors, visitors.Distinct().Count());
        Assert.Equal(
            visitors,
            Enumerable
                .Range(0, CapsuleerPopulationRules.MaximumGateVisitors)
                .Select(v => provision.EnsureInbound(new(30000142, Source, 50009992, 50009991), sourceObserved: true, visitor: v)!.Value)
        );
        Save(fixture, new(Enabled: false));
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        string jobs;
        string progress;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            jobs = JsonSerializer.Serialize(world.Set<SavedCapsuleerJob>().AsNoTracking().OrderBy(j => j.PilotId).ToArray());
            progress = JsonSerializer.Serialize(
                world.Set<SavedCapsuleerHauler>().AsNoTracking().OrderBy(h => h.PilotId).Select(h => h.Progress).ToArray()
            );
            world.Database.ExecuteSqlRaw(
                """
                UPDATE WorldCapsuleerClock SET ElapsedTicks=1000000000;
                CREATE TRIGGER reject_population_resume BEFORE DELETE ON WorldCapsuleerSuspension
                BEGIN SELECT RAISE(ABORT,'resume rejected'); END;
                """
            );
        }
        Save(fixture, new(GateVisitors: CapsuleerPopulationRules.MaximumGateVisitors));
        WorldConfigurationStore.Activate(fixture.Database);
        var rejected = Assert.Throws<DbUpdateException>(() => CapsuleerPopulationActivation.Apply(fixture.Database, fixture.Clock));
        Assert.IsType<SqliteException>(rejected.InnerException);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            Assert.Equal(visitors.Length, world.Set<SavedCapsuleerSuspension>().Count());
            Assert.Equal(jobs, JsonSerializer.Serialize(world.Set<SavedCapsuleerJob>().AsNoTracking().OrderBy(j => j.PilotId).ToArray()));
            Assert.Equal(
                progress,
                JsonSerializer.Serialize(
                    world.Set<SavedCapsuleerHauler>().AsNoTracking().OrderBy(h => h.PilotId).Select(h => h.Progress).ToArray()
                )
            );
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_population_resume");
        }
        CapsuleerPopulationActivation.Apply(fixture.Database, fixture.Clock);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            Assert.Empty(world.Set<SavedCapsuleerSuspension>());
            jobs = JsonSerializer.Serialize(world.Set<SavedCapsuleerJob>().AsNoTracking().OrderBy(j => j.PilotId).ToArray());
        }
        CapsuleerPopulationActivation.Apply(fixture.Database, fixture.Clock);
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(jobs, JsonSerializer.Serialize(saved.Set<SavedCapsuleerJob>().AsNoTracking().OrderBy(j => j.PilotId).ToArray()));
    }

    [Fact]
    public void MaximumTrafficDensityCreatesExtendedHomeSlotsAndReusesThemAfterAPause()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        var policy = new CapsuleerPopulationPolicy(Miners: false, TrafficMultiplier: 10, GateVisitors: 0);
        Save(fixture, policy);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        CapsuleerProvisioner provision = new(fixture.Database, fixture.Clock);
        provision.EnsureSystem(Source);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            var agents = world.Set<SavedCapsuleerAgent>().OrderBy(a => a.Slot).ToArray();
            int expected = CapsuleerEncounterRules.Haulers(1, Source, 1, 1, policy);
            Assert.True(expected > TrafficJourneyRules.HomeRosterSlots);
            Assert.Equal(Enumerable.Range(0, expected), agents.Select(a => a.Slot));
            Assert.Equal(expected, agents.Select(a => a.PilotId).Distinct().Count());
            Assert.All(agents, a => Assert.True(CapsuleerPopulationRules.Included(a.Career, a.Slot, expected, policy)));
        }
        var original = Assets(fixture);
        Save(fixture, policy with { TrafficMultiplier = 0 });
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            Assert.Equal(original.Pilots.Length, world.Set<SavedCapsuleerSuspension>().Count());
        }
        Save(fixture, policy);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        provision.EnsureSystem(Source);
        Assert.Equal(original, Assets(fixture));
    }

    [Fact]
    public void RattingPausePreservesTargetsLootBudgetsAndRemainingCombatDeadlines()
    {
        const long delay = 100;
        HaulerProgress work = new(
            HaulerActivity.Flying,
            1,
            2,
            3,
            4,
            5,
            new NavigationState().Capture(),
            ActiveTicks: 10,
            DueTicks: 20,
            ClockOffsetTicks: 3
        )
        {
            Ratting = new(4, [11, 12], 1, 200)
            {
                Phase = RattingPhase.Fighting,
                TargetId = 99,
                LastObservedAt = 1000,
                LootIds = [88],
            },
            Resupply = new(4, 2500, 1000) { DestinationStationId = 6 },
            Defense = new()
            {
                TargetId = 99,
                Locks = [new(99, 1100)],
                RecallUntil = 1200,
            },
            RattingLoot = [88],
        };
        var resumed = CapsuleerPauseRules.Resume(work, delay, 5000);
        Assert.Equal(110, resumed.ActiveTicks);
        Assert.Equal(120, resumed.DueTicks);
        Assert.Equal(103, resumed.ClockOffsetTicks);
        Assert.Equal(200, resumed.Ratting!.RemainingTicks);
        Assert.Equal(99, resumed.Ratting.TargetId);
        Assert.Equal(5000, resumed.Ratting.LastObservedAt);
        Assert.Equal(5100, Assert.Single(resumed.Defense!.Locks).ReadyAt);
        Assert.Equal(5200, resumed.Defense.RecallUntil);
        Assert.Same(work.Resupply, resumed.Resupply);
        Assert.Equal(work.RattingLoot, resumed.RattingLoot);
        Assert.Equal(work.Ratting.LootIds, resumed.Ratting.LootIds);
        Assert.Equal(resumed, CapsuleerPauseRules.Resume(resumed, 0, 5000));
    }

    [Fact]
    public void TrafficScalingAndLegacyDocumentsKeepDeterministicDefaultsAndRejectInvalidLimits()
    {
        Assert.True(CapsuleerPopulationRules.Included(CapsuleerCareer.Mining, 0, 0, new()));
        Assert.False(CapsuleerPopulationRules.Included(CapsuleerCareer.Mining, 0, 0, new(Miners: false)));
        Assert.False(CapsuleerPopulationRules.Included(CapsuleerCareer.Hauling, 1, 1, new()));
        Assert.True(CapsuleerPopulationRules.Included(CapsuleerCareer.Hauling, 0, 1, new()));
        Assert.False(CapsuleerPopulationRules.Included(CapsuleerCareer.Hauling, 0, 1, new(Enabled: false)));
        Assert.False(CapsuleerPopulationRules.Included((CapsuleerCareer)0, 0, 1, new()));
        var legacy = JsonSerializer.Deserialize<CapsuleerPopulationPolicy>("{\"GlobalProvisioning\":false}")!;
        Assert.Equal(new CapsuleerPopulationPolicy(false), legacy);
        Assert.Equal(6, CapsuleerPopulationRules.Traffic(6, 1, Source, new()));
        Assert.Equal(60, CapsuleerPopulationRules.Traffic(6, 1, Source, new(TrafficMultiplier: 10)));
        Assert.Equal(0, CapsuleerPopulationRules.Traffic(6, 1, Source, new(TrafficMultiplier: 0)));
        Assert.Equal(0, CapsuleerPopulationRules.Traffic(6, 1, Source, new(Enabled: false)));
        var sparse = Enumerable
            .Range(1, 100)
            .Select(system => CapsuleerPopulationRules.Traffic(1, 1, system, new(TrafficMultiplier: 0.5m)))
            .ToArray();
        Assert.Contains(0, sparse);
        Assert.Contains(1, sparse);
        Assert.Equal(
            sparse,
            Enumerable.Range(1, 100).Select(system => CapsuleerPopulationRules.Traffic(1, 1, system, new(TrafficMultiplier: 0.5m)))
        );
        Assert.Throws<InvalidDataException>(() => CapsuleerPopulationRules.Validate(new(TrafficMultiplier: -0.1m)));
        Assert.Throws<InvalidDataException>(() => CapsuleerPopulationRules.Validate(new(TrafficMultiplier: 10.1m)));
        Assert.Throws<InvalidDataException>(() => CapsuleerPopulationRules.Validate(new(GateVisitors: -1)));
        Assert.Throws<InvalidDataException>(() => CapsuleerPopulationRules.Validate(new(GateVisitors: 17)));
    }

    private static void Save(CapsuleerHaulerTests.Fixture fixture, CapsuleerPopulationPolicy policy)
    {
        WorldConfigurationStore store = new(fixture.Database, fixture.Clock);
        var state = store.Read();
        store.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { Capsuleers = policy },
            },
            "test"
        );
    }

    private sealed record SavedAssets(string Contents, long[] Pilots)
    {
        public bool Equals(SavedAssets? other) => other is not null && Contents == other.Contents && Pilots.SequenceEqual(other.Pilots);

        public override int GetHashCode() => Contents.GetHashCode(StringComparison.Ordinal);
    }

    private static SavedAssets Assets(CapsuleerHaulerTests.Fixture fixture)
    {
        using WorldDbContext world = new(fixture.Database.Path);
        var pilots = world.Set<SavedCapsuleerAgent>().OrderBy(a => a.PilotId).Select(a => a.PilotId).ToArray();
        return new(
            JsonSerializer.Serialize(
                new
                {
                    Pilots = world
                        .Pilots.Where(p => pilots.Contains(p.Id))
                        .OrderBy(p => p.Id)
                        .Select(p => new
                        {
                            p.Id,
                            p.Name,
                            p.ActiveShipId,
                        })
                        .ToArray(),
                    Ships = world
                        .Ships.Where(s => pilots.Contains(s.OwnerId))
                        .OrderBy(s => s.Id)
                        .Select(s => new
                        {
                            s.Id,
                            s.TypeId,
                            s.Name,
                            s.OwnerId,
                            s.StationId,
                        })
                        .ToArray(),
                    Items = world
                        .Set<OwnedItem>()
                        .Where(i => pilots.Contains(i.OwnerId))
                        .OrderBy(i => i.Id)
                        .Select(i => new
                        {
                            i.Id,
                            i.TypeId,
                            i.LocationId,
                            i.FlagId,
                            i.Quantity,
                        })
                        .ToArray(),
                    Wallets = world
                        .Set<PilotWallet>()
                        .Where(w => pilots.Contains(w.PilotId))
                        .OrderBy(w => w.PilotId)
                        .Select(w => new { w.PilotId, w.Cents })
                        .ToArray(),
                }
            ),
            pilots
        );
    }
}
