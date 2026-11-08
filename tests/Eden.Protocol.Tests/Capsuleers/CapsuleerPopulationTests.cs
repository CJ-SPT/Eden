using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Chat;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerPopulationTests
{
    private const int SourceSystem = 30000141;
    private const int DestinationSystem = 30000142;
    private const string DropFoundation = """
        DROP TABLE WorldCapsuleerJob;
        DROP TABLE WorldCapsuleerAgent;
        DROP TABLE WorldCapsuleerPopulation;
        DROP TABLE WorldCapsuleerClock;
        PRAGMA user_version=115;
        """;

    [Fact]
    public void Schema115MigrationRegistersFoundersWithoutChangingPaidWorkOrGrantingAssets()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            MinerObservation? observation = null;
            for (int i = 0; i < 10000 && observation?.Work.Activity != MinerActivity.Mining; i++)
            {
                f.Clock.Seconds++;
                observation = host.GetRequiredService<ICapsuleerSimulation>().Advance(i);
            }
            Assert.Equal(MinerActivity.Mining, observation!.Work.Activity);
        }
        string progress;
        long cents;
        long ship;
        int items;
        long miner;
        using (WorldDbContext world = new(f.Database.Path))
        {
            var founder = world.Set<SavedCapsuleerCommunity>().Single();
            progress = founder.Progress!;
            miner = founder.PilotId!.Value;
            ship = world.Pilots.Find(miner)!.ActiveShipId;
            cents = world.Set<PilotWallet>().Find(miner)!.Cents;
            items = world.Set<OwnedItem>().Count();
            world.Database.ExecuteSqlRaw(DropFoundation);
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            WorldInitialization.Initialize(f.Database, f.Clock);
            using var host = ServerServices.Build(f.Database, f.Clock);
            var population = host.GetRequiredService<ICapsuleerPopulation>();
            var community = Assert.Single(population.Communities());
            Assert.Equal(SourceSystem, community.SystemId);
            var agents = population.Agents(SourceSystem);
            Assert.Equal(2, agents.Count);
            Assert.Contains(agents, a => a.PilotId == miner && a.FounderShipId == ship && a.Career == CapsuleerCareer.Mining);
            Assert.Contains(agents, a => a.PilotId == f.Founder().PilotId && a.Career == CapsuleerCareer.Hauling);
            Assert.Empty(population.Agents(DestinationSystem));
            using WorldDbContext world = new(f.Database.Path);
            Assert.Equal(progress, world.Set<SavedCapsuleerCommunity>().Single().Progress);
            Assert.Equal(cents, world.Set<PilotWallet>().Find(miner)!.Cents);
            Assert.Equal(items, world.Set<OwnedItem>().Count());
            Assert.Equal(ship, world.Pilots.Find(miner)!.ActiveShipId);
            Assert.Empty(CapsuleerPopulationStore.Jobs(world));
            Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        }
    }

    [Fact]
    public void InterruptedRegistryMigrationRollsBackAndCanResumeWithoutDuplicatingFounders()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Database.ExecuteSqlRaw(DropFoundation);
            CapsuleerPopulationStore.Initialize(world);
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_agent BEFORE INSERT ON WorldCapsuleerAgent WHEN NEW.Career=2
                BEGIN SELECT RAISE(ABORT,'test interrupted registry'); END;
                """
            );
        }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => WorldInitialization.Initialize(f.Database, f.Clock));
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Empty(world.Set<SavedCapsuleerPopulation>());
            Assert.Empty(world.Set<SavedCapsuleerAgent>());
            Assert.Equal(115, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_agent");
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Single(saved.Set<SavedCapsuleerPopulation>());
        Assert.Equal(2, saved.Set<SavedCapsuleerAgent>().Count());
        Assert.Equal(2, saved.Pilots.Count(p => p.AccountId < 0));
    }

    [Fact]
    public void JobSettlementCommitsGoodsMoneyAndDeadlineOnceAndRollsBackFailure()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        long pilot;
        using (WorldDbContext world = new(f.Database.Path))
        {
            pilot = world.Set<SavedCapsuleerCommunity>().Single().PilotId!.Value;
            world.Add(
                new SavedCapsuleerJob
                {
                    PilotId = pilot,
                    DueTicks = 10,
                    Action = CapsuleerAction.Trade,
                }
            );
            world.SaveChanges();
        }
        f.AddStock(pilot, 10);
        CapsuleerJob expected = new(pilot, 0, 0, 10, CapsuleerAction.Trade);
        long balance;
        using (WorldDbContext world = new(f.Database.Path))
        {
            balance = world.Set<PilotWallet>().Find(pilot)!.Cents;
            Assert.Throws<IOException>(() =>
                CapsuleerPopulationStore.Settle(
                    world,
                    expected,
                    20,
                    CapsuleerAction.Arrival,
                    transaction =>
                    {
                        Pay(transaction, pilot);
                        throw new IOException("test failed settlement");
                    }
                )
            );
        }
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Equal(balance, world.Set<PilotWallet>().Find(pilot)!.Cents);
            Assert.Equal(10, world.Set<OwnedItem>().Single(i => i.TypeId == 1230).Quantity);
            Assert.Equal(expected, Assert.Single(CapsuleerPopulationStore.Jobs(world)));
            Assert.True(
                CapsuleerPopulationStore.Settle(world, expected, 20, CapsuleerAction.Arrival, transaction => Pay(transaction, pilot))
            );
            Assert.False(
                CapsuleerPopulationStore.Settle(world, expected, 30, CapsuleerAction.Lock, _ => throw new InvalidOperationException())
            );
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using WorldDbContext reopened = new(f.Database.Path);
        Assert.Equal(balance - 17, reopened.Set<PilotWallet>().Find(pilot)!.Cents);
        Assert.Equal(9, reopened.Set<OwnedItem>().Single(i => i.TypeId == 1230).Quantity);
        Assert.Equal(expected.Next(20, CapsuleerAction.Arrival), Assert.Single(CapsuleerPopulationStore.Jobs(reopened)));

        static void Pay(WorldDbContext world, long pilot)
        {
            world.Database.ExecuteSql($"UPDATE WorldWallet SET Cents=Cents-17 WHERE PilotId={pilot}");
            world.Database.ExecuteSql($"UPDATE WorldItem SET Quantity=Quantity-1 WHERE OwnerId={pilot} AND TypeId=1230");
        }
    }

    [Fact]
    public async Task SelectedFlightsTrackDockedHumansAndTransfersIndependentlyOfChatAndPersistClock()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        long elapsed;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var presence = host.GetRequiredService<IHumanPresence>();
            var chat = host.GetRequiredService<ChatRuntime>();
            chat.Update(Guid.NewGuid(), new(90000001, "Eden Pilot", 1000044, SourceSystem, 0));
            Assert.False(presence.Read().Active);
            var factory = host.GetRequiredService<FlightSessionFactory>();
            var pilot = host.GetRequiredService<Eden.World.Docking.SqliteWorld>().FindPilot(1000001)!;
            using var first = factory.Create(pilot);
            using var second = factory.Create(pilot);
            Assert.Null(first.Pilot.Position);
            Assert.Equal(2, presence.Read().SelectedSessions);
            Assert.Equal(new[] { SourceSystem }, presence.Read().ActiveSystems);
            f.Clock.Milliseconds += 1250;
            first.Relocate(pilot with { Position = SpacePosition.Zero, CurrentSystem = new(DestinationSystem, 20000020, 10000002) }, []);
            Assert.Equal(new[] { SourceSystem, DestinationSystem }, presence.Read().ActiveSystems);
            second.Dispose();
            Assert.Equal(new[] { DestinationSystem }, presence.Read().ActiveSystems);
            f.Clock.Milliseconds += 250;
            using (var metrics = SessionPerformance.Begin("human presence warm read", _ => { }, force: true)!)
            {
                elapsed = presence.Read().ElapsedTicks;
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            Assert.Equal(TimeSpan.FromMilliseconds(1500).Ticks, elapsed);
            first.Dispose();
            f.Clock.Seconds += 3600;
            Assert.False(presence.Read().Active);
            Assert.Equal(elapsed, presence.Read().ElapsedTicks);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        f.Clock.Seconds += 3600;
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var restarted = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var restored = restarted.GetRequiredService<IHumanPresence>();
        Assert.False(restored.Read().Active);
        Assert.Equal(elapsed, restored.Read().ElapsedTicks);
        using var selected = restored.Select(90000001, SourceSystem);
        f.Clock.Milliseconds += 125;
        Assert.Equal(elapsed + TimeSpan.FromMilliseconds(125).Ticks, restored.Read().ElapsedTicks);
    }

    [Fact]
    public async Task FailedCheckpointRetainsActivityAndDetachedProjectionDoesNotOverwriteNewerTime()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        int writes = 0;
        long elapsed;
        using (
            LiveWorld live = new(
                f.Database,
                f.Clock,
                _ => { },
                (snapshot, path) =>
                {
                    if (Interlocked.Increment(ref writes) == 1)
                    {
                        throw new IOException("test failed clock checkpoint");
                    }
                    LiveWorld.PersistSnapshot(snapshot, path);
                }
            )
        )
        using (var host = ServerServices.Build(f.Database, f.Clock))
        {
            var presence = host.GetRequiredService<IHumanPresence>();
            using var selected = presence.Select(90000001, SourceSystem);
            f.Clock.Milliseconds += 125;
            using var snapshot = live.CaptureSnapshot();
            Assert.NotNull(snapshot);
            f.Clock.Milliseconds += 125;
            elapsed = presence.Read().ElapsedTicks;
            snapshot.Project();
            using (var command = snapshot.Database.CreateCommand())
            {
                command.CommandText = "SELECT ElapsedTicks FROM WorldCapsuleerClock WHERE Id=1";
                Assert.Equal(TimeSpan.FromMilliseconds(125).Ticks, (long)command.ExecuteScalar()!);
            }
            Assert.Equal(TimeSpan.FromMilliseconds(250).Ticks, elapsed);
            Assert.Equal(elapsed, presence.Read().ElapsedTicks);
            await Assert.ThrowsAsync<IOException>(() => live.FlushAsync());
            Assert.NotNull(live.LastSaveFailure);
            Assert.Equal(elapsed, presence.Read().ElapsedTicks);
            f.Clock.Milliseconds += 125;
            elapsed = presence.Read().ElapsedTicks;
            await live.FlushAsync();
            Assert.Null(live.LastSaveFailure);
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var restarted = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        Assert.Equal(elapsed, restarted.GetRequiredService<IHumanPresence>().Read().ElapsedTicks);
        Assert.False(restarted.GetRequiredService<IHumanPresence>().Read().Active);
    }
}
