using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class DroneCombatTriggerTests
{
    private const long OtherShip = 1000000000002;

    internal static DroneCombatTriggers Read(string path, long shipId)
    {
        using WorldDbContext world = new(path);
        return DroneCombatPersistence.Read(world, shipId);
    }

    internal static void AssertModule(string path, long source, long target, long now, bool player = true)
    {
        var fired = Read(path, source);
        Assert.Equal(new(1, target, now), fired.Offensive);
        Assert.Equal(0, fired.Incoming.Revision);
        if (player)
        {
            var attacked = Read(path, target);
            Assert.Equal(new(1, source, now), attacked.Incoming);
            Assert.Equal(0, attacked.Offensive.Revision);
        }
    }

    [Fact]
    public void OrderedActionsAtTheSameInstantKeepSeparateImmutableRevisions()
    {
        var empty = DroneCombatTriggers.Empty(Fixture.ShipId);
        var first = empty.Module(OtherShip, 100);
        var second = first.Module(OtherShip + 1, 100).Attacked(OtherShip, 100);
        Assert.Equal(new(0, 0, 0), empty.Offensive);
        Assert.Equal(new(1, OtherShip, 100), first.Offensive);
        Assert.Equal(new(2, OtherShip + 1, 100), second.Offensive);
        Assert.Equal(new(1, OtherShip, 100), second.Incoming);
        Assert.Equal(new(0, 0, 0), first.Incoming);
        Assert.Equal(second, second.Module(OtherShip, 99).Attacked(OtherShip + 1, 99));
        Assert.Throws<OverflowException>(() => new DroneCombatTrigger(long.MaxValue).Record(OtherShip, 100));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void InvalidActionsCannotChangeThePriorState(long other, long time)
    {
        var empty = DroneCombatTriggers.Empty(Fixture.ShipId);
        Assert.Throws<InvalidDataException>(() => empty.Module(other, time));
        Assert.Throws<InvalidDataException>(() => empty.Attacked(other, time));
        Assert.Equal(0, empty.Offensive.Revision);
        Assert.Equal(0, empty.Incoming.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaidTurretsAndNpcShotsAdvanceOnlyTheirOwnTriggerAndRejectDuplicateCallbacks(bool resident)
    {
        using Fixture fixture = new(liveWorld: resident, useTestClock: true);
        Assert.Equal(DroneCombatTriggers.Empty(Fixture.ShipId), Read(fixture.Db.Path, Fixture.ShipId));
        fixture.Aggress();
        var offensive = Read(fixture.Db.Path, Fixture.ShipId);
        Assert.Equal(new(1, fixture.Npc.Id, fixture.Clock.Now), offensive.Offensive);
        Assert.Equal(0, offensive.Incoming.Revision);
        Assert.Throws<InvalidDataException>(fixture.Aggress);
        Assert.Equal(offensive, Read(fixture.Db.Path, Fixture.ShipId));
        Assert.Null(fixture.Fire());
        Assert.Equal(offensive, Read(fixture.Db.Path, Fixture.ShipId));
        fixture.Clock.Milliseconds = 7500;
        Assert.Equal(Fixture.ShipId, Assert.IsType<Eden.Gameplay.Combat.NpcShot>(fixture.Fire()).Health.ShipId);
        var attacked = Read(fixture.Db.Path, Fixture.ShipId);
        Assert.Equal(offensive.Offensive, attacked.Offensive);
        Assert.Equal(new(1, fixture.Npc.Id, fixture.Clock.Now), attacked.Incoming);
        Assert.Null(fixture.Fire());
        Assert.Equal(attacked, Read(fixture.Db.Path, Fixture.ShipId));
    }

    [Fact]
    public void SqlCommitInvalidatesCachedAbsenceAndRollbackPublishesNeitherDirection()
    {
        using Fixture fixture = new(useTestClock: true);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var runtime = LiveCombatRuntime.Find(fixture.Db.Path)!;
        var empty = runtime.DroneTriggers(Fixture.ShipId);
        Assert.Equal(0, empty.Offensive.Revision);
        using (WorldDbContext world = new(fixture.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            DroneCombatPersistence.Module(world, Fixture.ShipId, OtherShip, true, fixture.Clock.Now);
            world.SaveChanges();
            transaction.Rollback();
        }
        Assert.Equal(empty, runtime.DroneTriggers(Fixture.ShipId));
        Assert.Equal(0, runtime.DroneTriggers(OtherShip).Incoming.Revision);
        using (WorldDbContext world = new(fixture.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            DroneCombatPersistence.Module(world, Fixture.ShipId, OtherShip, true, fixture.Clock.Now);
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.Equal(new(1, OtherShip, fixture.Clock.Now), runtime.DroneTriggers(Fixture.ShipId).Offensive);
        Assert.Equal(new(1, Fixture.ShipId, fixture.Clock.Now), runtime.DroneTriggers(OtherShip).Incoming);
        using var metrics = SessionPerformance.Begin("cached combat triggers", _ => { }, force: true);
        for (int observer = 0; observer < 10; observer++)
        {
            Assert.Equal(1, runtime.DroneTriggers(Fixture.ShipId).Offensive.Revision);
            Assert.Equal(1, runtime.DroneTriggers(OtherShip).Incoming.Revision);
        }
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void IncomingDroneAttacksCannotBecomeOffensiveModuleActionsAndStorageDoesNotKeepPairHistory()
    {
        using Fixture fixture = new(useTestClock: true);
        using WorldDbContext world = new(fixture.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        const int actions = 1000;
        for (int action = 0; action < actions; action++)
        {
            DroneCombatPersistence.Module(world, Fixture.ShipId, fixture.Npc.Id + action, false, fixture.Clock.Now);
            DroneCombatPersistence.Incoming(world, OtherShip, Fixture.ShipId + action, fixture.Clock.Now);
        }
        world.SaveChanges();
        transaction.Commit();
        Assert.Equal(2, world.Set<WorldDroneCombatTriggers>().Count());
        var source = DroneCombatPersistence.Read(world, Fixture.ShipId);
        var target = DroneCombatPersistence.Read(world, OtherShip);
        Assert.Equal(actions, source.Offensive.Revision);
        Assert.Equal(fixture.Npc.Id + actions - 1, source.Offensive.OtherId);
        Assert.Equal(0, source.Incoming.Revision);
        Assert.Equal(actions, target.Incoming.Revision);
        Assert.Equal(0, target.Offensive.Revision);
    }

    [Fact]
    public async Task CheckpointRetainsLatestTriggersAndStartupNeverReplaysThem()
    {
        using Fixture fixture = new(useTestClock: true);
        DroneCombatTriggers expected;
        using (LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { }))
        {
            fixture.Aggress();
            var runtime = LiveCombatRuntime.Find(fixture.Db.Path)!;
            var captured = runtime.CaptureCheckpoint();
            var prior = Assert.Single(captured.DroneTriggers);
            fixture.Clock.Seconds += 10;
            Assert.NotNull(fixture.Fire());
            fixture.Aggress();
            expected = runtime.DroneTriggers(Fixture.ShipId);
            Assert.Equal(1, prior.Offensive.Revision);
            Assert.Equal(2, expected.Offensive.Revision);
            Assert.Equal(1, expected.Incoming.Revision);
            await live.FlushAsync();
        }
        Assert.Equal(expected, Read(fixture.Db.Path, Fixture.ShipId));
        WorldInitialization.Initialize(new(fixture.Db.Path), fixture.Clock);
        Assert.Equal(DroneCombatTriggers.Empty(Fixture.ShipId), Read(fixture.Db.Path, Fixture.ShipId));
        Assert.Null(fixture.Fire());
    }

    [Fact]
    public async Task WarmModuleAndIncomingActionsRemainSqlFreeAndCheckpointEveryRevision()
    {
        using Fixture fixture = new(useTestClock: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000 WHERE AttributeId=263");
        }
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Set<WorldNpc>().ExecuteUpdate(set => set.SetProperty(n => n.ShieldCharge, 1000000d));
        }
        var database = fixture.Db.Services.GetRequiredService<WorldDatabase>();
        DroneCombatTriggers expected;
        using (LiveWorld live = new(database, fixture.Clock, _ => { }))
        {
            var runtime = LiveCombatRuntime.Find(fixture.Db.Path)!;
            void Frame()
            {
                fixture.Aggress();
                fixture.Clock.Seconds += 10;
                database.MotionStamps[fixture.Npc.SystemId] = 1 + checked((int)(fixture.Clock.Milliseconds / 1000));
                Assert.NotNull(fixture.Fire());
            }
            Frame();
            Frame();
            using (var metrics = SessionPerformance.Begin("warm combat triggers", _ => { }, force: true))
            {
                for (int frame = 0; frame < 12; frame++)
                {
                    Frame();
                    var value = runtime.DroneTriggers(Fixture.ShipId);
                    Assert.Equal(frame + 3, value.Offensive.Revision);
                    Assert.Equal(frame + 3, value.Incoming.Revision);
                    Assert.Equal(fixture.Npc.Id, value.Offensive.OtherId);
                    Assert.Equal(fixture.Npc.Id, value.Incoming.OtherId);
                }
                Assert.Equal(0, metrics!.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            expected = runtime.DroneTriggers(Fixture.ShipId);
            await live.FlushAsync();
        }
        Assert.Equal(expected, Read(fixture.Db.Path, Fixture.ShipId));
    }

    [Fact]
    public void Version100WorldsUpgradeWithoutInventingOffensiveHistory()
    {
        using Fixture fixture = new(useTestClock: true);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldDroneCombatTriggers; PRAGMA user_version=100");
        }
        WorldInitialization.Initialize(new(fixture.Db.Path), fixture.Clock);
        using WorldDbContext reopened = new(fixture.Db.Path);
        Assert.Empty(reopened.Set<WorldDroneCombatTriggers>());
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        fixture.Aggress();
        Assert.Equal(1, Read(fixture.Db.Path, Fixture.ShipId).Offensive.Revision);
    }
}
