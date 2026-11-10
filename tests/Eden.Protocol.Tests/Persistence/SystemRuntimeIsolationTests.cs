using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Ammunition;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SystemRuntimeIsolationTests
{
    private const int LocalSystem = 30000144;
    private const int RemoteSystem = 30000142;

    [Fact]
    public void ACommittedLocalChangeDoesNotInvalidateAnotherSystemsWarmContacts()
    {
        using Combat.NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var encounters = fixture.Db.Services.GetRequiredService<Eden.Gameplay.Combat.INpcEncounters>();
        Assert.Empty(encounters.List(RemoteSystem, fixture.Clock.Now));
        Assert.Empty(encounters.List(RemoteSystem, fixture.Clock.Now));
        using (WorldDbContext world = new(fixture.Db.Path) { Participants = new(systems: [LocalSystem]) })
        {
            var npc = world.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(fixture.Npc.Id)!;
            npc.ShieldCharge /= 2;
            world.SaveChanges();
        }
        using var metrics = SessionPerformance.Begin("unrelated committed contacts", _ => { }, force: true)!;
        Assert.Empty(encounters.List(RemoteSystem, fixture.Clock.Now));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public async Task DurabilityAndShutdownCannotUpgradeARetainedSystemLease()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (live.Enter("resident work", LocalSystem))
        {
            Assert.Throws<InvalidOperationException>(() => live.FlushAsync().GetAwaiter().GetResult());
            Assert.Throws<InvalidOperationException>(live.Dispose);
        }
        await live.FlushAsync();
        Assert.False(live.IsEntered);
    }

    [Fact]
    public async Task FileBackedSessionContextDoesNotRetainOwnershipAcrossAnAwait()
    {
        using TestWorld fixture = new();
        var synchronization = fixture.Services.GetRequiredService<IWorldSynchronization>();
        using var context = synchronization.UseSystem(LocalSystem);
        Assert.False(synchronization.IsEntered);
        await Task.Run(() =>
            {
                using var owner = synchronization.Enter("another request");
                Assert.True(synchronization.IsEntered);
            })
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(synchronization.IsEntered);
    }

    [Fact]
    public async Task TrafficRefreshInsideASessionHandoffEntersPersistenceBeforeSystemOwners()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var handoff = fixture.Services.GetRequiredService<ICapsuleerHandoff>();
        var presentation = fixture.Services.GetRequiredService<ICapsuleerPresentation>();
        await handoff.SynchronizeAsync(LocalSystem, presentation.Refresh);
        await handoff.SynchronizeAsync(RemoteSystem, presentation.Refresh);
        Assert.False(live.IsEntered);
    }

    [Fact]
    public async Task ClassifiedTransactionDoesNotAcquireAnUnrelatedSystem()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (live.Enter("register local", LocalSystem)) { }
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        var remote = Task.Run(() =>
        {
            using var ownership = live.Enter("paused remote simulation", RemoteSystem);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Task.Run(() =>
                {
                    using WorldDbContext world = new(fixture.Path) { Participants = new(systems: [LocalSystem]) };
                    using var transaction = world.Database.BeginTransaction();
                    world.Database.ExecuteSqlRaw(
                        "INSERT INTO WorldWallet(PilotId,Cents) VALUES(90000001,1) " + "ON CONFLICT(PilotId) DO UPDATE SET Cents=Cents+1"
                    );
                    transaction.Commit();
                })
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await remote;
        }
    }

    [Fact]
    public void PersistenceHandoffRollsBackAStagedShotBeforeRevalidatingAndReturnsItsPaidResult()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        fixture.Load(quantity: 400);
        fixture.Clock.Milliseconds += 5000;
        fixture.Space();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        using var system = live.UseSystem(fixture.Npc.SystemId);
        Assert.Equal(199, fixture.Fire().Magazine!.Quantity);
        fixture.Clock.Milliseconds += 3000;
        int attempts = 0;
        var result = live.Run(() =>
        {
            attempts++;
            var shot = fixture.Fire();
            using WorldDbContext world = new(fixture.Db.Path);
            Assert.NotEqual(0, world.Pilots.Count());
            return shot;
        });
        Assert.Equal(2, attempts);
        Assert.Equal(198, result.Magazine!.Quantity);
        Assert.True(result.Applied > 0);
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Equal(198, saved.Set<Eden.Data.World.Inventory.OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
        Assert.Equal(result.Health.Remaining.Shield, saved.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(fixture.Npc.Id)!.ShieldCharge);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public async Task PausedSystemCannotBlockAnotherSystemsWarmedMovement(int clients, bool sameSystem)
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var synchronization = fixture.Services.GetRequiredService<IWorldSynchronization>();
        var movement = fixture.Services.GetRequiredService<ISystemMotionStore>();
        IReadOnlyList<ShipMotionResult> Advance(int client)
        {
            int system = LocalSystem + (sameSystem ? 0 : client);
            return synchronization.Run("movement", system, () => movement.Advance(system, 1, 0, [], []));
        }
        for (int client = 0; client < clients; client++)
        {
            Assert.Empty(Advance(client));
        }
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        var remote = Task.Run(() =>
        {
            using var ownership = live.Enter("paused simulation", RemoteSystem);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Task.WhenAll(
                    Enumerable
                        .Range(0, clients)
                        .Select(client =>
                            Task.Run(() =>
                            {
                                using var metrics = SessionPerformance.Begin("isolated movement", _ => { }, force: true)!;
                                Assert.Empty(Advance(client));
                                Assert.Equal(0, metrics.Commands);
                                Assert.Equal(0, metrics.Connections);
                                Assert.Equal(0, metrics.FittingCalculations);
                            })
                        )
                )
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await remote;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedSystemCannotBlockAnotherSystemsCompleteWarmFrame(bool sharedCoordination)
    {
        using PassiveFittingTests.Fixture fixture = new();
        fixture.Db.Store.SetPosition(fixture.ShipId, new(1_000_000_000, 0, 0), LocalSystem);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using var scope = fixture.Db.Services.CreateScope();
        var services = scope.ServiceProvider;
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            services.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var updates = services.GetRequiredService<PilotUpdateCoordinator>();
        updates.Enter(context);
        var skills = services.GetRequiredService<SkillHandler>();
        skills.Advance(context);
        context.Flight.Snapshot();
        PilotSessionTick tick = new(
            fixture.Clock,
            updates,
            skills,
            services.GetRequiredService<Eden.Gameplay.Mining.IAsteroidField>(),
            services.GetRequiredService<IPilotOperationScope>(),
            anomalies: services.GetRequiredService<Eden.Protocol.Exploration.CombatAnomalyHandler>(),
            handoff: services.GetRequiredService<ICapsuleerHandoff>(),
            presentation: services.GetRequiredService<ICapsuleerPresentation>()
        );
        PilotSessionOutput output = new(context, 1000001, 42, _ => Task.CompletedTask, _ => { });
        for (int warmup = 0; warmup < 4; warmup++)
        {
            await tick.AdvanceAsync(context, output, _ => { }, 1000001, 42);
        }
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        var remote = Task.Run(() =>
        {
            using var ownership = sharedCoordination
                ? live.EnterShared("paused shared coordination")
                : live.Enter("paused remote simulation", RemoteSystem);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Task.Run(async () =>
                {
                    using var metrics = SessionPerformance.Begin("isolated complete frame", _ => { }, force: true)!;
                    await tick.AdvanceAsync(context, output, _ => { }, 1000001, 42);
                    Assert.Equal(0, metrics.Commands);
                    Assert.Equal(0, metrics.Connections);
                    Assert.Equal(0, metrics.FittingCalculations);
                })
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await remote;
        }
    }
}
