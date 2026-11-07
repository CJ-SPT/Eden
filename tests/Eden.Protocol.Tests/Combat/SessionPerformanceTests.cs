using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class SessionPerformanceTests
{
    private sealed class CountingCombat : INpcCombat
    {
        public int Acquisitions { get; private set; }

        public void Acquire(IReadOnlyList<NpcTargetCandidate> candidates, long now) => Acquisitions++;

        public IReadOnlyList<long> Aggressors(long shipId) => [];

        public NpcShot? Fire(long pilotId, long shipId, long npcId, SpacePosition velocity, long now, double roll, int destinyStamp) =>
            null;

        public void Disengage(long shipId, long? npcId = null) { }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    public void MotionBatchesTackleAndAggressionReadsRegardlessOfNpcCount(int count)
    {
        using TestWorld db = new();
        NpcEncounterTests.Prepare(db);
        var contact = db.Services.GetRequiredService<INpcEncounters>().List(30000144, DateTime.UtcNow.ToFileTimeUtc())[0];
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<WorldNpc>().ExecuteDelete();
            for (int index = 0; index < count; index++)
            {
                world.Add(
                    new WorldNpc
                    {
                        Id = contact.Id + index,
                        TypeId = contact.TypeId,
                        SystemId = contact.SystemId,
                        HomeX = contact.Position.X + index * 1000,
                        HomeY = contact.Position.Y,
                        HomeZ = contact.Position.Z,
                        X = contact.Position.X + index * 1000,
                        Y = contact.Position.Y,
                        Z = contact.Position.Z,
                        ShieldCharge = 20,
                    }
                );
            }
            world.SaveChanges();
        }
        using var metrics = SessionPerformance.Begin("motion", _ => { }, force: true)!;
        db.Store.Advance(contact.SystemId, 1, DateTime.UtcNow.ToFileTimeUtc(), [], []);
        Assert.Equal(1, metrics.TackleReads);
        Assert.Equal(1, metrics.AggressionReads);
        Assert.Equal(2, metrics.Connections);
    }

    [Fact]
    public void UnrelatedCallBurstsAcquireOnceAndCandidateChangesAndDeadlinesAcquireAgain()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        NpcEncounterTests.Prepare(db);
        var npc = db.Services.GetRequiredService<INpcEncounters>().List(30000144, clock.Now)[0];
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, npc.Position, npc.SystemId);
        using var scope = db.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var pilots = provider.GetRequiredService<IPilotLookup>();
        using PilotCallContext context = new(
            1000001,
            pilots.ReadAccount(1000001),
            pilots.ListPilots(1000001),
            p =>
                new(
                    db.Store,
                    p,
                    clock,
                    provider.GetRequiredService<SharedSpace>(),
                    [],
                    encounters: provider.GetRequiredService<INpcEncounters>()
                ),
            42,
            SignedSettings.Empty
        );
        context.Select(db.Store.FindPilot(1000001)!);
        context.Flight.Snapshot();
        clock.Seconds += 10;
        context.Flight.Checkpoint();
        CountingCombat combat = new();
        var space = provider.GetRequiredService<SharedSpace>();
        using NpcCombatHandler handler = new(
            clock,
            combat,
            provider.GetRequiredService<SharedTargetLocks>(),
            provider.GetRequiredService<ShipLossCoordinator>(),
            space
        );
        handler.Enter(context);
        for (int call = 0; call < 20; call++)
        {
            handler.Advance(context);
        }
        Assert.Equal(1, combat.Acquisitions);
        clock.Seconds++;
        handler.Advance(context);
        Assert.Equal(2, combat.Acquisitions);
        var contact = new ShipContact(PilotContact.From(context.Pilot) with { ShipId = 999 }, SpacePosition.Zero, null, 0, null);
        space.Publish(contact);
        handler.Advance(context);
        Assert.Equal(3, combat.Acquisitions);
        handler.Leave();
        handler.Enter(context);
        handler.Advance(context);
        Assert.Equal(4, combat.Acquisitions);
    }

    [Fact]
    public void TickDeadlineRetainsCadenceAndSkipsExpiredWakeups()
    {
        TestClock clock = new();
        SessionTickSchedule schedule = new(clock);
        Assert.Equal(TimeSpan.FromSeconds(1), schedule.Remaining);
        clock.Milliseconds = 1300;
        schedule.Complete();
        Assert.Equal(TimeSpan.FromMilliseconds(700), schedule.Remaining);
        clock.Milliseconds = 4500;
        schedule.Complete();
        Assert.Equal(TimeSpan.FromMilliseconds(500), schedule.Remaining);
        clock.Milliseconds = 5000;
        Assert.Equal(TimeSpan.Zero, schedule.Remaining);
    }

    [Fact]
    public async Task DatabaseCountsFlowAcrossAwaitAndNestedScopesWithoutLeaking()
    {
        using TestWorld db = new();
        List<string> reports = [];
        using (var outer = SessionPerformance.Begin("test", reports.Add, force: true)!)
        {
            await Task.Yield();
            using (var inner = SessionPerformance.Phase("read")!)
            {
                using WorldDbContext world = new(db.Path);
                using var transaction = world.Database.BeginTransaction();
                _ = world.Set<WorldNpc>().AsNoTracking().ToArray();
                transaction.Commit();
                Assert.Equal(1, inner.Commands);
                Assert.Equal(1, inner.Connections);
                Assert.True(inner.SqlMilliseconds > 0);
                Assert.True(inner.CommitMilliseconds > 0);
                Assert.True(inner.TransactionStartMilliseconds > 0);
            }
            Assert.True(SessionPerformance.IsActive);
            Assert.Equal(1, outer.Commands);
            Assert.Equal(1, outer.Connections);
        }
        Assert.False(SessionPerformance.IsActive);
        Assert.Equal(2, reports.Count);
        Assert.All(reports, report => Assert.Contains("sql=", report));
        Assert.All(reports, report => Assert.Contains("commit=", report));
        Assert.All(reports, report => Assert.Contains("begin=", report));
    }

    [Fact]
    public void PhaseDurationsRollUpOnceAndStopAtDisposal()
    {
        List<string> reports = [];
        using (var outer = SessionPerformance.Begin("operation", reports.Add, force: true)!)
        {
            using (var inner = SessionPerformance.Phase("phase")!)
            {
                SessionPerformance.DatabaseElapsed(TimeSpan.FromMilliseconds(2));
                SessionPerformance.DatabaseElapsed(TimeSpan.FromMilliseconds(3), commit: true);
                SessionPerformance.TransactionStarted(TimeSpan.FromMilliseconds(5));
                Assert.Equal(2, inner.SqlMilliseconds);
                Assert.Equal(3, inner.CommitMilliseconds);
                Assert.Equal(5, inner.TransactionStartMilliseconds);
            }
            SessionPerformance.DatabaseElapsed(TimeSpan.FromMilliseconds(4));
            Assert.Equal(6, outer.SqlMilliseconds);
            Assert.Equal(3, outer.CommitMilliseconds);
            Assert.Equal(5, outer.TransactionStartMilliseconds);
        }
        Assert.False(SessionPerformance.IsActive);
        Assert.Null(SessionPerformance.Phase("outside"));
        Assert.Equal(2, reports.Count);
    }
}
