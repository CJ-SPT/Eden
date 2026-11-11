using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Combat;

internal static class ResidentMonitorAssertions
{
    internal static void WithoutSql(Action read)
    {
        List<string> commands = [];
        using var metrics = SessionPerformance.Begin("resident monitoring invalidation", _ => { }, force: true, command: commands.Add)!;
        read();
        Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}

public sealed partial class NpcEwarTests
{
    [Fact]
    public void ResidentMonitorNpcEffectsKeepIndependentViewsAndRollbackWithoutSql()
    {
        const int JammingNpc = 2385;
        using Fixture f = new(JammingNpc, resident: true);
        Assert.NotEmpty(f.Advance());
        var live = LiveWorld.Find(f.Combat.Db.Path)!;
        live.Economy.Warm();
        var reads = live.Reads!;
        long ship = NpcCombatTests.Fixture.ShipId;
        long source = f.Combat.Npc.Id;
        IReadOnlyList<NpcEwarContact> before = [];
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.True(reads.HasNpcEwar(ship));
            before = reads.NpcEwarIncoming(ship);
            Assert.Equal(source, Assert.Single(before).SourceId);
            Assert.Single(reads.NpcEwarAttempts(ship));
            Assert.Equal(ship, Assert.Single(reads.NpcEwarSource(source)).TargetShipId);
        });
        using (var rejected = live.Economy.Begin(new(ships: [ship])))
        {
            rejected.Set<WorldNpcEwar>().Find(source, (long)NpcEwarKind.Jamming)!.StartedAt++;
            Assert.Equal(before[0].StartedAt + 1, Assert.Single(reads.NpcEwarIncoming(ship)).StartedAt);
        }
        Assert.Same(before, reads.NpcEwarIncoming(ship));
        using (var unrelated = live.Economy.Begin(new(ships: [ship])))
        {
            unrelated.Set<NpcAggression>().Find(source)!.ReadyAt++;
            unrelated.Commit();
        }
        Assert.Same(before, reads.NpcEwarIncoming(ship));
        using (var committed = live.Economy.Begin(new(ships: [ship])))
        {
            committed.Remove(committed.Set<WorldNpcEwar>().Find(source, (long)NpcEwarKind.Jamming)!);
            committed.Commit();
        }
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.False(reads.HasNpcEwar(ship));
            Assert.Empty(reads.NpcEwarIncoming(ship));
            Assert.Empty(reads.NpcEwarAttempts(ship));
            Assert.Empty(reads.NpcEwarSource(source));
        });
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Empty(reopened.Set<WorldNpcEwar>());
    }
}

public sealed partial class NpcMissileTests
{
    [Fact]
    public void ResidentMonitorNpcFlightsKeepTheExactHistoryBoundaryAndIgnoreRejectedChangesWithoutSql()
    {
        const int HistorySeconds = 120;
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        Prepare(f);
        Fire(f);
        var live = LiveWorld.Find(f.Db.Path)!;
        live.Economy.Warm();
        var reads = live.Reads!;
        long pilot = NpcCombatTests.Fixture.PilotId;
        long id = live.Economy.Inspect(world => world.Set<WorldMissile>().Single().Id);
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.True(reads.HasNpcMissileWork(pilot, f.Clock.Now, HistorySeconds));
            Assert.False(reads.Impacted(id));
            Assert.False(reads.HasPilotMissiles(f.Npc.SystemId));
            var error = Assert.Throws<InvalidDataException>(() => reads.HasNpcMissileWork(long.MaxValue, f.Clock.Now, HistorySeconds));
            Assert.Equal("Unknown character.", error.Message);
        });
        using (var rejected = live.Economy.Begin(new(pilots: [pilot])))
        {
            rejected.Set<WorldMissile>().Find(id)!.Status = MissileFlightStatus.Expired;
            Assert.False(reads.HasNpcMissileWork(pilot, f.Clock.Now, HistorySeconds));
        }
        Assert.True(reads.HasNpcMissileWork(pilot, f.Clock.Now, HistorySeconds));
        using (var committed = live.Economy.Begin(new(pilots: [pilot])))
        {
            var row = committed.Set<WorldMissile>().Find(id)!;
            row.Status = MissileFlightStatus.Expired;
            row.EndedAt = f.Clock.Now - HistorySeconds * TimeSpan.TicksPerSecond;
            committed.Commit();
        }
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.False(reads.HasNpcMissileWork(pilot, f.Clock.Now, HistorySeconds));
            Assert.True(reads.HasNpcMissileWork(pilot, f.Clock.Now + 1, HistorySeconds));
        });
        using (var removal = live.Economy.Begin(new(pilots: [pilot])))
        {
            removal.Remove(removal.Set<WorldMissile>().Find(id)!);
            removal.Commit();
        }
        ResidentMonitorAssertions.WithoutSql(() => Assert.False(reads.HasNpcMissileWork(pilot, f.Clock.Now + 1, HistorySeconds)));
    }
}

public sealed partial class PilotMissileTests
{
    [Fact]
    public void ResidentMonitorPilotFlightsAndUnreportedImpactsFollowRealCommitsWithoutSql()
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        Launch(f, launcher);
        live.Economy.Warm();
        var reads = live.Reads!;
        long pilot = f.PilotId;
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.True(reads.HasPilotMissiles(SystemId));
            Assert.False(reads.HasMissileReports(pilot));
        });
        using (var rejected = live.Economy.Begin(new(pilots: [pilot])))
        {
            var row = rejected.Set<WorldMissile>().Single();
            row.Status = MissileFlightStatus.Expired;
            Assert.False(reads.HasPilotMissiles(SystemId));
        }
        Assert.True(reads.HasPilotMissiles(SystemId));
        Advance(f);
        long id = live.Economy.Inspect(world => world.Set<WorldMissile>().Single().Id);
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.False(reads.HasPilotMissiles(SystemId));
            Assert.True(reads.HasMissileReports(pilot));
            Assert.True(reads.Impacted(id));
        });
        Assert.Single(Store(f).TakeImpacts(pilot));
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.False(reads.HasMissileReports(pilot));
            Assert.True(reads.Impacted(id));
            Assert.Empty(Store(f).TakeImpacts(pilot));
        });
    }
}

public sealed partial class LiveSimulationTests
{
    [Fact]
    public void ResidentMonitorRehydratesAnEvictedNpcMotionSceneWithoutSqlOrFittingWork()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        var live = LiveWorld.Find(f.Db.Path)!;
        live.Economy.Warm();
        Assert.Empty(f.Db.Store.Advance(f.Npc.SystemId, 1, f.Clock.Now, [], []));
        var runtime = live.CombatForSystem(f.Npc.SystemId)!;
        using (var ownership = live.EnterShared())
        using (var context = live.UseSystem(f.Npc.SystemId))
        using (var simulation = live.Enter())
        {
            Assert.True(runtime.ClearResident(retainPositions: false));
        }
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.Empty(f.Db.Store.Advance(f.Npc.SystemId, 2, f.Clock.Now, [], []));
            Assert.True(runtime.DetailedEntries > 0);
        });
    }
}

public sealed partial class PlayerEwarTests
{
    [Fact]
    public void ResidentMonitorPlayerEffectsDoNotLeakRejectedRemovalOrRequeryAfterACommittedStop()
    {
        const int Dampener = 1968;
        using var f = Prepare();
        long module = f.Fit(Dampener);
        Space(f);
        long pilot = f.PilotId;
        long ship = f.ShipId;
        long target = Wing(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        Activate(f, module, Eden.Gameplay.Inventory.DogmaEffect.SensorDampener);
        live.Economy.Warm();
        var reads = live.Reads!;
        IReadOnlyList<ElectronicWarfareContact> before = [];
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.True(reads.HasPlayerEwar());
            before = reads.PlayerEwarIncoming(target);
            Assert.Equal(module, Assert.Single(before).Module.ItemId);
        });
        using (var rejected = live.Economy.Begin(new(pilots: [pilot], ships: [target])))
        {
            rejected.Remove(rejected.Set<WorldPlayerEwar>().Find(module)!);
            Assert.False(reads.HasPlayerEwar());
            Assert.Empty(reads.PlayerEwarIncoming(target));
        }
        Assert.Same(before, reads.PlayerEwarIncoming(target));
        using (var unrelated = live.Economy.Begin(new(pilots: [pilot])))
        {
            unrelated.Set<ModuleCooldown>().Find(module)!.ReadyAt++;
            unrelated.Commit();
        }
        Assert.Same(before, reads.PlayerEwarIncoming(target));
        Store(f).Stop(pilot, ship, module);
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.False(reads.HasPlayerEwar());
            Assert.Empty(reads.PlayerEwarIncoming(target));
        });
    }
}
