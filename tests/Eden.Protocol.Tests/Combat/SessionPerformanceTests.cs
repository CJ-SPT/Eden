using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.Server.Persistence;
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
            using (var inner = SessionPerformance.Begin("read", reports.Add)!)
            {
                using WorldDbContext world = new(db.Path);
                _ = world.Set<WorldNpc>().AsNoTracking().ToArray();
                Assert.Equal(1, inner.Commands);
                Assert.Equal(1, inner.Connections);
            }
            Assert.True(SessionPerformance.IsActive);
            Assert.Equal(1, outer.Commands);
            Assert.Equal(1, outer.Connections);
        }
        Assert.False(SessionPerformance.IsActive);
        Assert.Equal(2, reports.Count);
    }
}
