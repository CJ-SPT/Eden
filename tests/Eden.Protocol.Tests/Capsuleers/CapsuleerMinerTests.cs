using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerMinerTests
{
    private const int SchoolStation = 60015157;
    private const int Kisogo = 30000141;
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();

    [Fact]
    public void TwoObserversWatchOnePaidCycleSurvivePauseAndRestartAndUnload()
    {
        var directory = TestDirectory.Create("capsuleer-mining-");
        var database = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
        var clock = new TestClock();
        try
        {
            CreateWorld(database, clock);
            long actor;
            long deposit;
            long before;
            int paidUntil;
            int stamp;
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                var worker = host.GetRequiredService<CapsuleerMaintenance>();
                Assert.Null(worker.Tick());
                var chat = host.GetRequiredService<ChatRuntime>();
                var world = host.GetRequiredService<SqliteWorld>();
                var a = world.FindPilot(1000001)!;
                var b = world.FindPilot(1000002)!;
                chat.Update(First, new(a.PilotId, a.Name, a.CorpId, Kisogo, 0));
                chat.Update(Second, new(b.PilotId, b.Name, b.CorpId, Kisogo, 0));
                var room = new ChatRoom(ChatRoomKind.Local, Kisogo);
                var firstChat = chat.Authenticate(
                    a.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    chat.IssueToken(First)
                );
                chat.Join(firstChat.Id, room, null);
                var work = Until(MinerActivity.Mining, worker, clock);
                actor = work.Pilot.PilotId;
                deposit = work.Work.TargetId;
                paidUntil = work.Work.DueStamp;
                stamp = work.Work.Stamp;
                var secondChat = chat.Authenticate(
                    b.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    chat.IssueToken(Second)
                );
                chat.Join(secondChat.Id, room, null);
                Assert.Equal(actor, chat.User(actor)!.Id);
                Assert.Contains(chat.Members(firstChat.Id, room), member => member.Id == actor);
                Assert.Contains(chat.Members(secondChat.Id, room), member => member.Id == actor);
                List<ChatEvent> delivered = [];
                while (firstChat.Events.TryRead(out var delivery))
                {
                    delivered.Add(delivery.Event);
                }
                Assert.Contains(delivered, e => e is ChatEvent.Presence { Available: true } presence && presence.Pilot.Id == actor);
                var space = host.GetRequiredService<SharedSpace>();
                foreach (var observer in new[] { a, b })
                {
                    var sameGrid = observer with { Position = work.Pilot.Position, CurrentSystem = work.Pilot.Home.System };
                    var contact = Assert.Single(space.Peers(sameGrid), p => p.Pilot.PilotId == actor);
                    Assert.Equal(deposit, Assert.Single(contact.Mining!).Target);
                    Assert.Equal(work.Pilot.ShipId, contact.Pilot.ShipId);
                }
                Assert.Null(worker.Tick());
                chat.Update(First, null);
                clock.Seconds++;
                using (var metrics = SessionPerformance.Begin("paid miner waiting", _ => { }, force: true)!)
                {
                    Assert.NotNull(worker.Tick());
                    Assert.Equal(0, metrics.Commands);
                    Assert.Equal(0, metrics.Connections);
                    Assert.Equal(0, metrics.FittingCalculations);
                }
                stamp++;
                chat.Update(Second, null);
                clock.Seconds += 3600;
                Assert.Null(worker.Tick());
                using WorldDbContext saved = new(database.Path);
                before = saved.Set<WorldAsteroid>().Find(deposit)!.Quantity;
            }
            clock.Seconds += 3600;
            WorldInitialization.Initialize(database, clock);
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                var chat = host.GetRequiredService<ChatRuntime>();
                var worker = host.GetRequiredService<CapsuleerMaintenance>();
                Assert.Null(worker.Tick());
                chat.Update(First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
                clock.Seconds++;
                var resumed = Assert.IsType<MinerObservation>(worker.Tick());
                Assert.Equal(
                    (actor, stamp + 1, paidUntil, deposit),
                    (resumed.Pilot.PilotId, resumed.Work.Stamp, resumed.Work.DueStamp, resumed.Work.TargetId)
                );
                using WorldDbContext saved = new(database.Path);
                Assert.Equal(before, saved.Set<WorldAsteroid>().AsNoTracking().Single(a => a.Id == deposit).Quantity);
                var docked = Until(MinerActivity.Docked, worker, clock);
                Assert.Null(docked.Pilot.Position);
                clock.Seconds++;
                worker.Tick();
                var ore = host.GetRequiredService<IPilotInventory>().List(actor, SchoolStation).Single(i => i.TypeId == 1230);
                Assert.True(ore.Quantity > 0);
                long after = saved.Set<WorldAsteroid>().AsNoTracking().Single(a => a.Id == deposit).Quantity;
                Assert.Equal(before - after, ore.Quantity);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WarpResumesAtSavedProgressAndFailedCollectionCanBeRetriedOnce()
    {
        var directory = TestDirectory.Create("capsuleer-replay-");
        var database = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
        var clock = new TestClock();
        try
        {
            CreateWorld(database, clock);
            MinerObservation flying;
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                host.GetRequiredService<ChatRuntime>().Update(First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
                var worker = host.GetRequiredService<CapsuleerMaintenance>();
                do
                {
                    flying = Until(MinerActivity.FlyingToBelt, worker, clock);
                } while (flying.Work.Navigation.Warp is null);
            }
            clock.Seconds += 86400;
            WorldInitialization.Initialize(database, clock);
            using var reopened = ServerServices.Build(database, clock, liveWorld: true);
            reopened.GetRequiredService<ChatRuntime>().Update(First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
            var resumed = reopened.GetRequiredService<CapsuleerMaintenance>();
            var warp = Until(MinerActivity.FlyingToBelt, resumed, clock);
            Assert.Equal(flying.Work.Stamp + 1, warp.Work.Stamp);
            Assert.Equal(flying.Work.Navigation.Warp, warp.Work.Navigation.Warp);
            Assert.NotEqual(flying.Pilot.Position, warp.Pilot.Position);
            var paid = Until(MinerActivity.Mining, resumed, clock);
            using WorldDbContext saved = new(database.Path);
            long Quantity() => saved.Set<WorldAsteroid>().AsNoTracking().Single(a => a.Id == paid.Work.TargetId).Quantity;
            long before = Quantity();
            saved.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_collection BEFORE UPDATE OF Progress ON WorldCapsuleerCommunity
                BEGIN SELECT RAISE(ABORT,'test failed collection'); END
                """
            );
            for (int i = paid.Work.Stamp + 1; i < paid.Work.DueStamp; i++)
            {
                clock.Seconds++;
                Assert.Equal(MinerActivity.Mining, resumed.Tick()!.Work.Activity);
            }
            clock.Seconds++;
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => resumed.Tick());
            Assert.Equal(before, Quantity());
            saved.Database.ExecuteSqlRaw("DROP TRIGGER reject_collection");
            clock.Seconds++;
            Assert.Equal(1, resumed.Tick()!.Work.Cycles);
            long after = Quantity();
            Assert.True(after < before);
            Assert.Null(resumed.Tick());
            Assert.Equal(after, Quantity());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static MinerObservation Until(MinerActivity activity, CapsuleerMaintenance worker, TestClock clock)
    {
        MinerObservation? last = null;
        for (int i = 0; i < 1500; i++)
        {
            clock.Seconds++;
            last = worker.Tick();
            Assert.NotNull(last);
            Assert.Null(last.WaitingReason);
            if (last.Work.Activity == activity)
            {
                return last;
            }
        }
        throw new InvalidOperationException(
            $"Miner never reached {activity}, last work {last?.Work.Activity} at {last?.Pilot.Position?.X}."
        );
    }

    private static void CreateWorld(WorldDatabase database, TestClock clock)
    {
        TestCatalog.Create(database.CatalogPath);
        TestCatalog.AddSchool(database.CatalogPath, 17, 1, 1, Kisogo, SchoolStation, 601);
        PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        // Extract of the school, station, belt and ordinary Caldari mining skills from build 3662168.
        catalog.Database.ExecuteSqlRaw(
            """
            UPDATE SourceRecord SET Data='{{"shipTypeID":601,"skills":{{"3327":3,"3386":3}}}}'
                WHERE Dataset='client/races' AND Key='1';
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/dogmaattributes','552','{{"defaultValue":100}}');
            UPDATE Station SET X=983459251064,Y=-119222082939,Z=-1157779559490 WHERE Id=60015157;
            INSERT OR IGNORE INTO ItemCategory VALUES(2,'Celestial');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES(9,'Asteroid Belt',2);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES(15,'Asteroid Belt',9);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                VALUES(40009051,30000141,15,9,-37717647360,849715200,-55091896320);
            INSERT INTO SourceRecord VALUES('client/asteroidcountbytypeidbeltid','40009051','{{"1230":39}}');
            """
        );
        WorldInitialization.Initialize(database, clock, economy: new(false, false, CapsuleerSeed: 42));
    }
}
