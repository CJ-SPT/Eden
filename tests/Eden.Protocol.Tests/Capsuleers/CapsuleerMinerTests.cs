using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerMinerTests(ITestOutputHelper output)
{
    private const int SchoolStation = 60015157;
    private const int Kisogo = 30000141;
    private static readonly Guid First = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();

    [Fact]
    public async Task TwoObserversWatchOnePaidCycleSurvivePauseAndRestartAndUnload()
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
                UpdateSelection(host, First, new(a.PilotId, a.Name, a.CorpId, Kisogo, 0));
                UpdateSelection(host, Second, new(b.PilotId, b.Name, b.CorpId, Kisogo, 0));
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
                UpdateSelection(host, First, null);
                UpdateSelection(host, Second, new(b.PilotId, b.Name, b.CorpId, 30000142, 0));
                clock.Seconds++;
                using (var metrics = SessionPerformance.Begin("paid miner waiting", output.WriteLine, force: true)!)
                {
                    Assert.Null(worker.Tick());
                    Assert.Equal(0, metrics.Commands);
                    Assert.Equal(0, metrics.Connections);
                    Assert.Equal(0, metrics.FittingCalculations);
                }
                UpdateSelection(host, Second, null);
                clock.Seconds += 3600;
                using (var empty = SessionPerformance.Begin("empty server miner", output.WriteLine, force: true)!)
                {
                    Assert.Null(worker.Tick());
                    Assert.Equal(0, empty.Commands);
                    Assert.Equal(0, empty.Connections);
                    Assert.Equal(0, empty.FittingCalculations);
                }
                var live = host.GetRequiredService<LiveWorld>();
                live.MeasureCheckpoints = true;
                await live.FlushAsync();
                var checkpoint = Assert.IsType<WorldCheckpoint>(live.LastCheckpoint);
                output.WriteLine(
                    $"Checkpoint capture {checkpoint.Capture.TotalMilliseconds:F2} ms, "
                        + $"projection {checkpoint.Projection.TotalMilliseconds:F2} ms, write {checkpoint.Write.TotalMilliseconds:F2} ms."
                );
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
                UpdateSelection(host, First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
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
                Assert.True(before - after <= ore.Quantity);
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
                UpdateSelection(host, First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
                var worker = host.GetRequiredService<CapsuleerMaintenance>();
                do
                {
                    flying = Until(MinerActivity.FlyingToBelt, worker, clock);
                } while (flying.Work.Navigation.Warp is null);
            }
            clock.Seconds += 86400;
            WorldInitialization.Initialize(database, clock);
            using var reopened = ServerServices.Build(database, clock, liveWorld: true);
            UpdateSelection(reopened, First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
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

    [Fact]
    public void DefaultFounderAndStartingAssetsSurviveConcurrentCharacterCreationAndReopen()
    {
        var directory = TestDirectory.Create("capsuleer-founder-");
        var database = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
        var clock = new TestClock();
        try
        {
            CreateWorld(database, clock);
            long pilot;
            long ship;
            using (WorldDbContext world = new(database.Path))
            {
                pilot = world.Set<SavedCapsuleerCommunity>().Single().PilotId!.Value;
                ship = world.Pilots.Find(pilot)!.ActiveShipId;
                Assert.Equal(CapsuleerCommunityStore.VentureType, world.Ships.Find(ship)!.TypeId);
                Assert.Equal(SchoolStation, world.Ships.Find(ship)!.StationId);
                var laser = world.Set<OwnedItem>().Single(i => i.OwnerId == pilot);
                Assert.Equal(483, laser.TypeId);
                Assert.Equal(CapsuleerCommunityStore.StartingCents, world.Set<PilotWallet>().Find(pilot)!.Cents);
            }
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                Parallel.For(
                    0,
                    2,
                    index =>
                        host.GetRequiredService<ICharacterCreation>()
                            .Create(
                                1000001,
                                new NewCharacter("New Neighbor " + index, 1, 1, 0, CharacterRules.UnspecifiedAncestry, [], [], 17)
                            )
                );
            }
            WorldInitialization.Initialize(database, clock);
            using WorldDbContext reopened = new(database.Path);
            Assert.Equal(pilot, reopened.Set<SavedCapsuleerCommunity>().Single().PilotId);
            Assert.Equal(ship, reopened.Pilots.Find(pilot)!.ActiveShipId);
            Assert.Single(reopened.Set<OwnedItem>().Where(i => i.OwnerId == pilot));
            Assert.Equal(CapsuleerCommunityStore.StartingCents, reopened.Set<PilotWallet>().Find(pilot)!.Cents);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AggressionRetreatSurvivesRestartWithoutCollectingUnfinishedCycle()
    {
        var directory = TestDirectory.Create("capsuleer-retreat-");
        var database = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
        var clock = new TestClock();
        try
        {
            CreateWorld(database, clock);
            long target;
            long quantity;
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                UpdateSelection(host, First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
                var worker = host.GetRequiredService<CapsuleerMaintenance>();
                var paid = Until(MinerActivity.Mining, worker, clock);
                target = paid.Work.TargetId;
                using WorldDbContext world = new(database.Path);
                quantity = world.Set<WorldAsteroid>().Find(target)!.Quantity;
                host.GetRequiredService<ICapsuleerSimulation>().ObserveAggression(paid.Pilot.ShipId);
                host.GetRequiredService<ICapsuleerSimulation>().ObserveAggression(1);
                clock.Seconds++;
                Assert.Equal(MinerActivity.Returning, worker.Tick()!.Work.Activity);
            }
            WorldInitialization.Initialize(database, clock);
            using var reopened = ServerServices.Build(database, clock, liveWorld: true);
            UpdateSelection(reopened, First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
            Until(MinerActivity.Docked, reopened.GetRequiredService<CapsuleerMaintenance>(), clock);
            using WorldDbContext saved = new(database.Path);
            Assert.Equal(quantity, saved.Set<WorldAsteroid>().Find(target)!.Quantity);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DepletedTargetIsReplacedWithoutGrantingOre()
    {
        var directory = TestDirectory.Create("capsuleer-depleted-");
        var database = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
        var clock = new TestClock();
        try
        {
            CreateWorld(database, clock);
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            UpdateSelection(host, First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            var paid = Until(MinerActivity.Mining, worker, clock);
            using (WorldDbContext world = new(database.Path))
            {
                world.Database.ExecuteSql($"UPDATE WorldAsteroid SET Quantity=0 WHERE Id={paid.Work.TargetId}");
            }
            MinerObservation? next = null;
            for (int i = 0; i < paid.Work.CycleSeconds + 2; i++)
            {
                clock.Seconds++;
                next = worker.Tick();
            }
            Assert.Equal(0, next!.Work.Cycles);
            var replacement = Until(MinerActivity.Mining, worker, clock);
            Assert.NotEqual(paid.Work.TargetId, replacement.Work.TargetId);
            using WorldDbContext saved = new(database.Path);
            Assert.Empty(saved.Set<OwnedItem>().Where(i => i.OwnerId == paid.Pilot.PilotId && i.TypeId == 1230));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void LostVentureUsesNormalLossSettlementAndWaitsDockedForReplacement()
    {
        var directory = TestDirectory.Create("capsuleer-loss-");
        var database = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
        var clock = new TestClock();
        try
        {
            CreateWorld(database, clock);
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            UpdateSelection(host, First, new(90000001, "Eden Pilot", 1000044, Kisogo, 0));
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            var paid = Until(MinerActivity.Mining, worker, clock);
            using (WorldDbContext world = new(database.Path))
            {
                using var transaction = world.Database.BeginTransaction();
                using CatalogDbContext catalog = new(database.CatalogPath);
                var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == paid.Pilot.PilotId);
                const long attacker = 700000001;
                world.Add(
                    new WorldNpc
                    {
                        Id = attacker,
                        TypeId = 601,
                        CorporationId = 1000035,
                        SystemId = Kisogo,
                    }
                );
                world.SaveChanges();
                var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot.Id));
                PilotHealthStore.Apply(world, paid.Pilot.ShipId, defense, new DamageVector(1000000, 0, 0, 0), clock.Now);
                ShipLossPersistence.Apply(
                    world,
                    catalog,
                    pilot,
                    attacker,
                    paid.Pilot.ShipId,
                    Kisogo,
                    pilot.ActiveShip.Position!,
                    clock.Now,
                    host.GetRequiredService<IShipLootRoll>()
                );
                world.SaveChanges();
                transaction.Commit();
            }
            var docked = Until(MinerActivity.Docked, worker, clock);
            Assert.Equal(FleetDefinitions.CapsuleGroup, docked.Pilot.ShipGroupId);
            clock.Seconds++;
            var waiting = worker.Tick()!;
            Assert.Equal(MinerActivity.Waiting, waiting.Work.Activity);
            Assert.Null(waiting.Pilot.Position);
            using WorldDbContext saved = new(database.Path);
            Assert.DoesNotContain(
                saved.Ships.Where(ship => ship.OwnerId == paid.Pilot.PilotId),
                ship => ship.TypeId == CapsuleerCommunityStore.VentureType
            );
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ShowInfoFromPlayerCorporationReadsMinerProfileAndContactDictionary()
    {
        var directory = TestDirectory.Create("capsuleer-show-info-");
        var database = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
        var clock = new TestClock();
        try
        {
            CreateWorld(database, clock);
            using var host = ServerServices.Build(database, clock);
            using var scope = host.CreateScope();
            var human = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
            var member = human with { Identity = human.Identity with { CorporationId = 98000000 } };
            using PilotCallContext context = new(
                1000001,
                human.Account,
                [human],
                scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            context.Select(member);
            long miner = Assert.Single(host.GetRequiredService<ICapsuleerCommunity>().Pilots()).PilotId;
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            object? Read(string service, string method, object?[] args)
            {
                var result = Assert.IsType<PilotCallResult.Success>(router.Call(context, service, method, args, _ => { }));
                return BlueMarshal.Decode(BlueMarshal.Encode(result.Reply));
            }
            var profile = Assert.IsType<BlueInstance>(Read("charMgr", "GetPublicInfo3", [miner]));
            var state = BlueRead.Fields(profile.State);
            Assert.Single(Assert.IsType<List<object?>>(state["lines"]));
            var corporation = BlueRead.Fields(Assert.IsType<BlueInstance>(Read("corpRegistry", "GetInfoWindowDataForChar", [miner])).State);
            Assert.Equal(1000035L, corporation["corpID"]);
            // The native standing service calls corpContacts.get(characterID) after drawing security status.
            var contacts = Assert.IsAssignableFrom<System.Collections.IDictionary>(Read("corpRegistry", "GetCorporateContacts", []));
            Assert.Empty(contacts.Keys);
            Assert.False(contacts.Contains(miner));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static void UpdateSelection(ServiceProvider host, Guid session, ChatPilot? pilot)
    {
        host.GetRequiredService<IHumanPresence>().Update(session, pilot?.SystemId);
        host.GetRequiredService<ChatRuntime>().Update(session, pilot);
    }

    private static MinerObservation Until(MinerActivity activity, CapsuleerMaintenance worker, TestClock clock)
    {
        MinerObservation? last = null;
        for (int i = 0; i < 30000; i++)
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

    internal static void CreateWorld(WorldDatabase database, TestClock clock, bool legacyFounders = true)
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
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30000141','{{"security":1.0}}');
            UPDATE Station SET X=983459251064,Y=-119222082939,Z=-1157779559490 WHERE Id=60015157;
            INSERT OR IGNORE INTO ItemCategory VALUES(2,'Celestial');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES(9,'Asteroid Belt',2);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES(15,'Asteroid Belt',9);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                VALUES(40009051,30000141,15,9,-37717647360,849715200,-55091896320);
            INSERT INTO SourceRecord VALUES('client/asteroidcountbytypeidbeltid','40009051','{{"1230":39}}');
            """
        );
        WorldInitialization.Initialize(database, clock, economy: new(false, false));
        using (WorldDbContext world = new(database.Path))
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldHomeStation SET StationId=60015157");
            world.Database.ExecuteSqlRaw("UPDATE WorldShip SET StationId=60015157");
        }
        WorldInitialization.Initialize(database, clock);
        if (legacyFounders)
        {
            using WorldDbContext founders = new(database.Path);
            using CatalogDbContext source = new(database.CatalogPath);
            using var transaction = founders.Database.BeginTransaction();
            CapsuleerCommunityStore.Provision(founders, source, clock);
            CapsuleerPopulationStore.Reconcile(founders, source);
            CapsuleerPopulationStore.ScheduleFounders(founders);
            transaction.Commit();
        }
    }
}
