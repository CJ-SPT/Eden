using System.Diagnostics;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcSingleAcquisitionTests(ITestOutputHelper output)
{
    private static readonly NpcTargetCandidate s_pilot = new(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId);
    private static readonly NpcTargetCandidate s_wing = new(90000002, 1000000000002);

    [Fact]
    public void ColdSingleCandidateRequiresItsOwnActiveShip()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        f.Db.Store.SetPosition(s_pilot.ShipId, f.Npc.Position, f.Npc.SystemId);
        f.Db.Store.SetPosition(s_wing.ShipId, f.Npc.Position, f.Npc.SystemId);
        LiveWorld.Find(f.Db.Path)!.Economy.Warm();
        ResidentMonitorAssertions.WithoutSql(() => f.Combat.Acquire([new(s_pilot.PilotId, s_wing.ShipId)], f.Clock.Now));
        Assert.Empty(f.Combat.Aggressors(s_wing.ShipId));
        Assert.Empty(f.Combat.Aggressors(s_pilot.ShipId));

        ResidentMonitorAssertions.WithoutSql(() => f.Combat.Acquire([s_pilot], f.Clock.Now));
        Assert.Contains(f.Npc.Id, f.Combat.Aggressors(s_pilot.ShipId));
    }

    [Fact]
    public void SingleCandidateDoesNotReplaceExistingAggression()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        f.Aggress();
        var original = Assert.Single(SavedAggression(f));
        f.Db.Store.SetPosition(s_wing.ShipId, f.Npc.Position, f.Npc.SystemId);
        f.Clock.Seconds++;

        f.Combat.Acquire([s_wing], f.Clock.Now);

        Assert.Equal(original, Assert.Single(SavedAggression(f)));
        Assert.Empty(f.Combat.Aggressors(s_wing.ShipId));
    }

    [Fact]
    public void SingleAndMultipleCandidatesProduceTheSameOrderedAggressionWhenOnlyOneIsInRange()
    {
        var single = Run([s_pilot]);
        var multiple = Run([s_wing, s_pilot]);
        Assert.Equal(single, multiple);
        Assert.Equal(2, single.Length);
        Assert.All(single, row => Assert.Equal(s_pilot.ShipId, row.Ship));

        static (long Npc, long Ship, long Ready, long MissileReady)[] Run(IReadOnlyList<NpcTargetCandidate> candidates)
        {
            using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
            var position = f.Npc.Position;
            f.Db.Store.SetPosition(s_pilot.ShipId, position, f.Npc.SystemId);
            f.Db.Store.SetPosition(s_wing.ShipId, position + new SpacePosition(1_000_000, 0, 0), f.Npc.SystemId);
            using (var action = LiveWorld.Find(f.Db.Path)!.Economy.Begin(new(systems: [f.Npc.SystemId])))
            {
                action.Add(
                    new WorldNpc
                    {
                        Id = f.Npc.Id + 1,
                        TypeId = f.Npc.TypeId,
                        SystemId = f.Npc.SystemId,
                        HomeX = position.X,
                        HomeY = position.Y,
                        HomeZ = position.Z,
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                        ShieldCharge = f.Npc.Health.Defense.Shield,
                        UpdatedAt = f.Clock.Now,
                    }
                );
                action.Commit();
            }
            f.Combat.Acquire(candidates, f.Clock.Now);
            return SavedAggression(f);
        }
    }

    [Fact]
    public void WarmSingleCandidateAcquisitionKeepsAllocationsBounded()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        f.Db.Store.SetPosition(s_pilot.ShipId, f.Npc.Position + new SpacePosition(1_000_000, 0, 0), f.Npc.SystemId);
        NpcTargetCandidate[] candidates = [s_pilot];
        const int samples = 256;
        Acquire(samples);
        using var metrics = SessionPerformance.Begin("warm single NPC acquisition", _ => { }, force: true)!;
        long started = Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Acquire(samples);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        output.WriteLine($"{samples} single-candidate calls allocated {allocated:N0} bytes in {elapsed:F2} ms.");
        Assert.InRange(allocated, 0, samples * 10_500L);
        Assert.Equal((0, 0, 0), (metrics.Commands, metrics.Connections, metrics.FittingCalculations));
        Assert.Empty(f.Combat.Aggressors(s_pilot.ShipId));

        void Acquire(int count)
        {
            for (int index = 0; index < count; index++)
            {
                f.Clock.Seconds++;
                f.Combat.Acquire(candidates, f.Clock.Now);
            }
        }
    }

    private static (long Npc, long Ship, long Ready, long MissileReady)[] SavedAggression(NpcCombatTests.Fixture f)
    {
        var live = LiveWorld.Find(f.Db.Path)!;
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        return reopened
            .Set<NpcAggression>()
            .OrderBy(row => row.NpcId)
            .AsEnumerable()
            .Select(row => (row.NpcId, row.TargetShipId, row.ReadyAt, row.MissileReadyAt))
            .ToArray();
    }
}
