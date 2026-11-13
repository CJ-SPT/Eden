using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Ammunition;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CheckpointOwnershipTests(ITestOutputHelper report)
{
    private const int LocalSystem = 30000144;
    private const int RemoteSystem = 30000142;

    [Theory]
    [InlineData("station", "GetGuests")]
    [InlineData("marketProxy", "GetRegionBest")]
    [InlineData("config", "GetMultiOwnersEx")]
    [InlineData("search", "QuickQuery")]
    public async Task WarmDockedCallsCompleteDuringCheckpointCopyOnFirstPeriodicPoll(string service, string method)
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        using PausedCopy copy = new();
        using LiveWorld live = new(new(fixture.Path), clock, _ => { }, copy: copy.Copy);
        using var scope = fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var pilot = fixture.Store.FindPilot(1000001)!;
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
        List<byte[]> replies = [];
        PilotSessionOutput output = new(
            context,
            1000001,
            42,
            packet =>
            {
                if (packet is BlueInstance instance && instance.Name.EndsWith(".CallRsp", StringComparison.Ordinal))
                {
                    replies.Add(BlueMarshal.Encode(packet));
                }
                return Task.CompletedTask;
            },
            _ => { }
        );
        PilotSessionCalls calls = new(
            context,
            output,
            services.GetRequiredService<PilotCallRouter>(),
            updates,
            services.GetRequiredService<SkillHandler>(),
            clock,
            report.WriteLine,
            1000001,
            42,
            services.GetRequiredService<IPilotOperationScope>(),
            handoff: services.GetRequiredService<ICapsuleerHandoff>(),
            presentation: services.GetRequiredService<ICapsuleerPresentation>()
        );
        object?[] arguments = method switch
        {
            "GetMultiOwnersEx" => [new List<object?> { 90000001L, 90000002L, 1000044L }],
            "QuickQuery" => ["Eden", new List<object?> { 2L }],
            _ => [],
        };
        var request = PilotServicesTests.Request(service, method, arguments);
        await calls.ProcessAsync(request);
        Assert.NotEmpty(Assert.Single(replies));
        clock.Seconds++;
        live.ObjectsChanged();
        var capture = Task.Run(live.CaptureSnapshot);
        Task<(int Commands, int Connections, int Fittings)>? call = null;
        try
        {
            Assert.True(copy.Started.Wait(TimeSpan.FromSeconds(10)));
            call = Task.Run(async () =>
            {
                using var metrics = SessionPerformance.Begin(
                    method,
                    report.WriteLine,
                    force: true,
                    command: sql => report.WriteLine("SQL " + sql.ReplaceLineEndings(" "))
                )!;
                await calls.ProcessAsync(request);
                return (metrics.Commands, metrics.Connections, metrics.FittingCalculations);
            });
            Assert.Same(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.Equal((0, 0, 0), await call);
            Assert.Equal(2, replies.Count);
            Assert.Equal(replies[0], replies[1]);
        }
        finally
        {
            copy.Release.Set();
            using var snapshot = await capture;
            if (call is not null)
            {
                await call.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task DestructionQueuedDuringCopyCannotBeResurrectedByTheEarlierCapture()
    {
        using AmmunitionTests.Fixture fixture = new();
        using PausedCopy copy = new();
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { }, copy: copy.Copy);
        fixture.Load(quantity: 400);
        fixture.Clock.Milliseconds += 5000;
        fixture.Space();
        fixture.Fire();
        var capture = Task.Run(live.CaptureSnapshot);
        Task? destruction = null;
        try
        {
            Assert.True(copy.Started.Wait(TimeSpan.FromSeconds(10)));
            destruction = Task.Run(() =>
            {
                using WorldDbContext world = new(fixture.Db.Path) { Participants = new(systems: [LocalSystem], pilots: [90000001]) };
                using var transaction = world.Database.BeginTransaction();
                using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
                var npc = world.Set<WorldNpc>().Find(fixture.Npc.Id)!;
                var pilot = world.Pilots.Find(90000001L)!;
                var profile = new NpcProfileCatalog().Read(catalog, npc.TypeId);
                const double LethalDamage = 1_000_000;
                var result = NpcDamagePersistence.Apply(
                    world,
                    catalog,
                    npc,
                    pilot,
                    pilot.ActiveShipId,
                    profile.Attributes,
                    new(LethalDamage, LethalDamage, LethalDamage, LethalDamage),
                    fixture.Clock.Now
                );
                Assert.True(result.Destroyed);
                world.SaveChanges();
                transaction.Commit();
            });
            Assert.True(SpinWait.SpinUntil(() => live.WaitingOwners != 0, TimeSpan.FromSeconds(5)));
            Assert.False(destruction.IsCompleted);
        }
        finally
        {
            copy.Release.Set();
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(await capture);
        await destruction!.WaitAsync(TimeSpan.FromSeconds(5));
        snapshot.Project();
        using (WorldDbContext earlier = new(snapshot.Database))
        {
            Assert.Null(earlier.Set<WorldNpc>().Find(fixture.Npc.Id)!.DestroyedAt);
        }
        await live.FlushAsync();
        string reopenedPath = Path.Combine(Path.GetDirectoryName(fixture.Db.Path)!, "destroyed-after-copy.sqlite");
        File.Copy(fixture.Db.Path, reopenedPath);
        using WorldDbContext reopened = new(reopenedPath);
        Assert.NotNull(reopened.Set<WorldNpc>().Find(fixture.Npc.Id)!.DestroyedAt);
        Assert.Equal(199, reopened.Set<OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
        Assert.Equal(0, reopened.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void CaptureDoesNotInitializeDeadlinesOrFitsInDormantRuntimeShells(int scale)
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        fixture.Load(quantity: 400);
        fixture.Clock.Milliseconds += 5000;
        fixture.Space();
        fixture.Fire();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        using var initial = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        const int SystemsPerScale = 32;
        for (int system = 1; system <= SystemsPerScale * scale; system++)
        {
            Assert.NotNull(live.CombatForSystem(system));
        }
        live.ObjectsChanged();
        using var metrics = SessionPerformance.Begin("dormant system capture", report.WriteLine, force: true)!;
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public async Task CopyFailureRetainsPendingChangesAndTheNextSaveRetriesThem()
    {
        using TestWorld fixture = new();
        int copies = 0;
        using LiveWorld live = new(
            new(fixture.Path),
            new Fixtures.TestClock(),
            _ => { },
            copy: (source, destination) =>
            {
                if (Interlocked.Increment(ref copies) == 1)
                {
                    throw new IOException("Injected copy failure");
                }
                source.BackupDatabase(destination);
            }
        );
        using (WorldDbContext world = new(fixture.Path) { Participants = new(pilots: [90000001]) })
        {
            world.Pilots.Find(90000001L)!.Name = "Pending after copy failure";
            world.SaveChanges();
        }
        Assert.Equal("Injected copy failure", (await Assert.ThrowsAsync<IOException>(live.FlushAsync)).Message);
        Assert.Null(live.LastCheckpoint);
        Assert.IsType<IOException>(live.LastSaveFailure);
        await live.FlushAsync();
        Assert.Null(live.LastSaveFailure);
        Assert.NotNull(live.LastCheckpoint);
        using SqliteConnection disk = new($"Data Source={fixture.Path};Mode=ReadOnly;Pooling=False");
        disk.Open();
        using var command = disk.CreateCommand();
        command.CommandText = "SELECT Name FROM WorldPilot WHERE Id=90000001";
        Assert.Equal("Pending after copy failure", command.ExecuteScalar());
    }

    private sealed class PausedCopy : IDisposable
    {
        internal ManualResetEventSlim Started { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        private int _copies;

        internal void Copy(SqliteConnection source, SqliteConnection destination)
        {
            var backup = SQLitePCL.raw.sqlite3_backup_init(destination.Handle!, "main", source.Handle!, "main");
            Assert.NotNull(backup);
            try
            {
                int first = SQLitePCL.raw.sqlite3_backup_step(backup, 1);
                Assert.True(first is SQLitePCL.raw.SQLITE_OK or SQLitePCL.raw.SQLITE_DONE);
                if (Interlocked.Increment(ref _copies) == 1)
                {
                    Started.Set();
                    Assert.True(Release.Wait(TimeSpan.FromSeconds(30)));
                }
                Assert.Equal(SQLitePCL.raw.SQLITE_DONE, SQLitePCL.raw.sqlite3_backup_step(backup, -1));
            }
            finally
            {
                SQLitePCL.raw.sqlite3_backup_finish(backup);
            }
        }

        public void Dispose()
        {
            Release.Set();
            Started.Dispose();
            Release.Dispose();
        }
    }

    [Fact]
    public async Task CopyAllowsAnotherPaidShotAndKeepsEachCapturedGenerationCoherent()
    {
        using AmmunitionTests.Fixture fixture = new();
        using PausedCopy copy = new();
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { }, copy: copy.Copy);
        fixture.Load(quantity: 400);
        fixture.Clock.Milliseconds += 5000;
        fixture.Space();
        var pilot = fixture.Pilot;
        var weapons = fixture.Combat;
        CombatShot Fire() =>
            weapons.Fire(pilot.PilotId, fixture.Gun, fixture.Npc.Id, SpacePosition.Zero, fixture.Clock.Now, 0.5, WeaponActivation.AmmoFed);
        var first = Fire();
        var capture = Task.Run(live.CaptureSnapshot);
        try
        {
            Assert.True(copy.Started.Wait(TimeSpan.FromSeconds(10)));
            await Task.Run(() => Assert.NotNull(live.CombatForSystem(RemoteSystem))).WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.Milliseconds += 3000;
            await Task.Run(() =>
                {
                    using var metrics = SessionPerformance.Begin("paid shot during copy", _ => { }, force: true)!;
                    var second = Fire();
                    Assert.Equal(198, second.Magazine!.Quantity);
                    Assert.True(second.Applied > 0);
                    Assert.Equal(0, metrics.Commands);
                    Assert.Equal(0, metrics.Connections);
                    Assert.Equal(0, metrics.FittingCalculations);
                })
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            copy.Release.Set();
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(await capture);
        snapshot.Project();
        using (WorldDbContext firstSaved = new(snapshot.Database))
        {
            Assert.Equal(199, firstSaved.Set<OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
            Assert.Equal(
                first.Health.Remaining.Shield,
                firstSaved.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(fixture.Npc.Id)!.ShieldCharge
            );
        }
        using var later = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        Assert.True(later.Generation > snapshot.Generation);
        later.Project();
        using WorldDbContext secondSaved = new(later.Database);
        Assert.Equal(198, secondSaved.Set<OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
        Assert.True(snapshot.CopyBytes > 0);
        Assert.True(snapshot.CopyAllocatedBytes >= 0);
        Assert.True(snapshot.CaptureAllocatedBytes > 0);
        Assert.True(snapshot.ProjectionAllocatedBytes > 0);
        await live.FlushAsync();
        using SqliteConnection disk = new($"Data Source={fixture.Db.Path};Mode=ReadOnly;Pooling=False");
        disk.Open();
        using var command = disk.CreateCommand();
        command.CommandText = $"SELECT Quantity FROM WorldItem WHERE LocationId={fixture.Gun}";
        Assert.Equal(198L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public async Task PausedCopyAllowsWarmFramesAndIndependentMovementWhileSqlWritersWait(int clients, bool sameSystem)
    {
        using PassiveFittingTests.Fixture fixture = new();
        fixture.Db.Store.SetPosition(fixture.ShipId, new(1_000_000_000, 0, 0), LocalSystem);
        using PausedCopy copy = new();
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { }, copy: copy.Copy);
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
        services.GetRequiredService<SkillHandler>().Advance(context);
        context.Flight.Snapshot();
        PilotSessionTick tick = new(
            fixture.Clock,
            updates,
            services.GetRequiredService<SkillHandler>(),
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
        var synchronization = services.GetRequiredService<IWorldSynchronization>();
        var movement = services.GetRequiredService<ISystemMotionStore>();
        string writerOperation = sameSystem ? "coordinated SQL writer" : "SQLite connection";
        void Move(int client)
        {
            int system = sameSystem ? RemoteSystem : RemoteSystem + client + 10;
            Assert.Empty(synchronization.Run("checkpoint isolation movement", system, () => movement.Advance(system, 1, 0, [], [])));
        }
        for (int client = 0; client < clients; client++)
        {
            Move(client);
        }
        live.ObjectsChanged();
        var capture = Task.Run(live.CaptureSnapshot);
        Task? writer = null;
        try
        {
            Assert.True(copy.Started.Wait(TimeSpan.FromSeconds(10)));
            writer = Task.Run(() =>
            {
                using var mutation = sameSystem ? synchronization.Enter(writerOperation) : null;
                using WorldDbContext world = new(fixture.Db.Path) { Participants = new(systems: [RemoteSystem]) };
                world.Database.ExecuteSqlRaw("UPDATE WorldWallet SET Cents=Cents+1");
            });
            Assert.True(SpinWait.SpinUntil(() => live.WaitingOwners != 0, TimeSpan.FromSeconds(5)));
            Assert.False(writer.IsCompleted);
            var requests = Enumerable
                .Range(0, clients)
                .Select(client =>
                    Task.Run(() =>
                    {
                        using var metrics = SessionPerformance.Begin("movement during copy", _ => { }, force: true)!;
                        Move(client);
                        Assert.Equal(0, metrics.Commands);
                        Assert.Equal(0, metrics.Connections);
                        Assert.Equal(0, metrics.FittingCalculations);
                    })
                );
            var frame = Task.Run(async () =>
            {
                using var metrics = SessionPerformance.Begin("complete frame during copy", _ => { }, force: true)!;
                await tick.AdvanceAsync(context, output, _ => { }, 1000001, 42);
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            });
            await Task.WhenAll(requests.Append(frame)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(writer.IsCompleted);
        }
        finally
        {
            copy.Release.Set();
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(await capture);
        if (writer is not null)
        {
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Contains(live.PersistenceWaits, wait => wait.Operation == writerOperation && wait.Delay > TimeSpan.Zero);
        snapshot.Project();
        await live.FlushAsync();
        var checkpoint = Assert.IsType<WorldCheckpoint>(live.LastCheckpoint);
        Assert.True(checkpoint.Bytes > 0);
        Assert.True(checkpoint.CopyBytes > 0);
        Assert.Equal(TimeSpan.FromSeconds(15), LiveWorld.SaveInterval);
        double writerWait = live.PersistenceWaits.Where(w => w.Operation == writerOperation).Max(w => w.Delay).TotalMilliseconds;
        report.WriteLine(
            $"Checkpoint copy={checkpoint.Copy.TotalMilliseconds:F2} ms; "
                + $"capture={checkpoint.Capture.TotalMilliseconds:F2} ms; projection={checkpoint.Projection.TotalMilliseconds:F2} ms; "
                + $"bytes={checkpoint.Bytes}; copiedBytes={checkpoint.CopyBytes}; "
                + $"managed capture/copy/project/write={checkpoint.CaptureAllocatedBytes}/{checkpoint.CopyAllocatedBytes}/"
                + $"{checkpoint.ProjectionAllocatedBytes}/{checkpoint.WriteAllocatedBytes}; "
                + $"paused-copy writer wait={writerWait:F2} ms."
        );
    }
}
