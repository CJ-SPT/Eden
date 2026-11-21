using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Maps;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Chat;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerHaulerTests
{
    private const int Source = 60015157;
    private const int Destination = 60003760;
    private const int SourceSystem = 30000141;
    private const int DestinationSystem = 30000142;
    private const int SourceGate = 50009991;
    private const int ExitGate = 50009992;
    private const int Ore = 1230;
    private const int Load = 1000;

    [Fact]
    public void LocalAndSpacePresenceFollowTheHaulerThroughItsGateTransfer()
    {
        using Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        long miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
        long hauler = f.Founder().PilotId;
        f.AddStock(miner, Load);
        host.GetRequiredService<CapsuleerTrade>().ListStock(miner, Source);
        var chat = host.GetRequiredService<ChatRuntime>();
        var session = Guid.NewGuid();
        CapsuleerMinerTests.UpdateSelection(host, session, new(90000001, "Eden Pilot", 1000044, SourceSystem, 0));
        var connection = chat.Authenticate("90000001", chat.IssueToken(session));
        var room = new ChatRoom(ChatRoomKind.Local, SourceSystem);
        chat.Join(connection.Id, room, null);
        var worker = host.GetRequiredService<Eden.Server.Maintenance.CapsuleerMaintenance>();
        f.Clock.Seconds++;
        worker.Tick();
        Assert.Contains(chat.Members(connection.Id, room), p => p.Id == miner);
        Assert.Contains(chat.Members(connection.Id, room), p => p.Id == hauler);
        for (int i = 0; i < 3000 && chat.User(hauler)?.SystemId != DestinationSystem; i++)
        {
            f.Clock.Seconds++;
            worker.Tick();
        }
        Assert.True(
            chat.User(hauler)!.SystemId == DestinationSystem,
            host.GetRequiredService<CapsuleerScheduler>().Hauler(hauler)?.Work.WaitingReason
        );
        Assert.DoesNotContain(chat.Members(connection.Id, room), p => p.Id == hauler);
        Assert.Contains(chat.Members(connection.Id, room), p => p.Id == miner);
        var statistics = host.GetRequiredService<IMapStatistics>();
        Assert.Equal(1, statistics.History(MapHistoryStatistic.Jumps, 1).Rows.Single(r => r.SystemId == DestinationSystem).Counts.Jumps);
        var ships = host.GetRequiredService<SqliteWorld>();
        var arrival = ships.FindPilot(-1, hauler)!;
        var human = ships.FindPilot(1000001)!;
        var space = host.GetRequiredService<SharedSpace>();
        Assert.DoesNotContain(
            space.Peers(human with { Position = arrival.Position, CurrentSystem = arrival.CurrentSystem }),
            peer => peer.Pilot.PilotId == hauler
        );
        using var destinationObserver = host.GetRequiredService<IHumanPresence>().Select(human.PilotId, DestinationSystem);
        host.GetRequiredService<ICapsuleerPresentation>().Refresh();
        Assert.True(space.IsGateCloaked(arrival.ShipId));
        Assert.DoesNotContain(
            space.Peers(human with { Position = arrival.Position, CurrentSystem = arrival.CurrentSystem }),
            peer => peer.Pilot.PilotId == hauler
        );
        Assert.DoesNotContain(space.Peers(human with { Position = arrival.Position }), peer => peer.Pilot.PilotId == hauler);
        var atExitGate = human with { Position = arrival.Position, CurrentSystem = arrival.CurrentSystem };
        for (int turn = 0; turn < 20 && space.Peers(atExitGate).All(peer => peer.Pilot.ShipId != arrival.ShipId); turn++)
        {
            f.Clock.Seconds++;
            worker.Tick();
        }
        Assert.False(space.IsGateCloaked(arrival.ShipId));
        Assert.Contains(space.Peers(atExitGate), peer => peer.Pilot.ShipId == arrival.ShipId);
        destinationObserver.Dispose();
        CapsuleerMinerTests.UpdateSelection(host, session, null);
        var presence = host.GetRequiredService<IHumanPresence>();
        Assert.Empty(presence.Read().ActiveSystems);
        f.Clock.Seconds += 600;
        worker.Tick();
        CapsuleerMinerTests.UpdateSelection(host, session, new(human.PilotId, human.Name, human.CorpId, SourceSystem, 0));
        var rejoined = chat.Authenticate(human.PilotId.ToString(), chat.IssueToken(session));
        chat.Join(rejoined.Id, room, null);
        Assert.Equal(new[] { SourceSystem }, presence.Read().ActiveSystems);
        for (int i = 0; i < 3000 && chat.User(hauler)?.SystemId != SourceSystem; i++)
        {
            f.Clock.Seconds++;
            worker.Tick();
        }
        Assert.Contains(chat.Members(rejoined.Id, room), p => p.Id == hauler);
        Assert.Equal(arrival.ShipId, ships.FindPilot(-1, hauler)!.ShipId);
    }

    [Fact]
    public void DestructionDuringGateDepartureLosesCargoAndWaitsForReplacement()
    {
        using Fixture f = new();
        long hauler;
        long founderShip;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var founder = f.Founder();
            hauler = founder.PilotId;
            founderShip = founder.FounderShipId;
            long miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
            f.AddStock(miner, Load);
            host.GetRequiredService<CapsuleerTrade>().ListStock(miner, Source);
            var departure = Until(host, f.Clock, state => state.Work.Activity == HaulerActivity.Jumping);
            using (WorldDbContext world = new(f.Database.Path))
            {
                using var transaction = world.Database.BeginTransaction();
                using CatalogDbContext catalog = new(f.Database.CatalogPath);
                var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == hauler);
                const long attacker = 700000002;
                world.Add(
                    new WorldNpc
                    {
                        Id = attacker,
                        TypeId = 601,
                        CorporationId = 1000035,
                        HomeX = 0,
                        HomeY = 0,
                        HomeZ = 0,
                        SystemId = SourceSystem,
                    }
                );
                world.SaveChanges();
                var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, hauler));
                PilotHealthStore.Apply(world, founderShip, defense, new DamageVector(1000000, 0, 0, 0), f.Clock.Now);
                ShipLossPersistence.Apply(
                    world,
                    catalog,
                    pilot,
                    attacker,
                    founderShip,
                    SourceSystem,
                    pilot.ActiveShip.Position!,
                    f.Clock.Now,
                    host.GetRequiredService<IShipLootRoll>()
                );
                world.SaveChanges();
                transaction.Commit();
            }
            var docked = Until(host, f.Clock, state => state.Pilot.Position is null);
            Assert.Equal(FleetDefinitions.CapsuleGroup, docked.Pilot.ShipGroupId);
            Assert.Equal(SourceSystem, docked.Pilot.SystemId);
            var waiting = CapsuleerTestDriver.Hauler(host, f.Clock)!;
            Assert.Equal(HaulerActivity.Waiting, waiting.Work.Activity);
            Assert.Empty(host.GetRequiredService<IPilotMarket>().Transactions(hauler));
            Assert.Empty(host.GetRequiredService<IPersonalMarket>().PersonalOrders(hauler));
            using WorldDbContext saved = new(f.Database.Path);
            Assert.NotNull(saved.Set<ShipLoss>().Find(founderShip));
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        Assert.Equal((hauler, founderShip), (f.Founder().PilotId, f.Founder().FounderShipId));
        using WorldDbContext reopened = new(f.Database.Path);
        Assert.DoesNotContain(reopened.Ships.Where(s => s.OwnerId == hauler), s => s.TypeId == HaulingRules.BadgerType);
    }

    [Fact]
    public async Task CapsuleEscapesOutsideItsHaulingRouteAndDocksAfterRestart()
    {
        const int LossSystem = 30000144;
        using Fixture f = new();
        var founder = f.Founder();
        long capsule;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(f.Database.CatalogPath);
            Assert.DoesNotContain(LossSystem, CapsuleerTrafficFlight.Route(founder, catalog).Systems);
            Assert.Empty(catalog.Definitions.StationsInSystem(LossSystem));
            var gate = SystemObjects.Geometry(catalog, LossSystem).First(body => body.DestinationSystemId == DestinationSystem);
            using (var action = live.Economy.Begin(new(systems: [SourceSystem, LossSystem], pilots: [founder.PilotId])))
            {
                var victim = action.Pilots.Find(founder.PilotId)!;
                var attacker = action.Pilots.Find(90000001L)!;
                var start = gate.Position + new SpacePosition(gate.Radius + 1000, 0, 0);
                SqliteWorld.SetPosition(action, catalog, victim.ActiveShipId, start, LossSystem, f.Clock.Now);
                ShipLossPersistence.Apply(
                    action,
                    catalog,
                    victim,
                    null,
                    victim.ActiveShipId,
                    LossSystem,
                    victim.ActiveShip.Position!,
                    f.Clock.Now,
                    host.GetRequiredService<IShipLootRoll>(),
                    PilotInteractionHistoryStore.Player(action, attacker, attacker.ActiveShipId)
                );
                capsule = victim.ActiveShipId;
                action.Commit();
            }
            var statistics = host.GetRequiredService<IMapStatistics>();
            Assert.Equal(
                new MapHistoryRow(LossSystem, new(0, 1, 0, 0)),
                Assert.Single(statistics.History(MapHistoryStatistic.Kills, 1).Rows)
            );
            var escaping = Until(host, f.Clock, state => state.Work.Activity == HaulerActivity.Flying);
            Assert.Equal(capsule, escaping.Pilot.ShipId);
            Assert.Equal(LossSystem, escaping.Pilot.SystemId);
            Assert.Equal(gate.Id, escaping.Work.TargetId);
            Assert.Equal(Destination, escaping.Work.DestinationStationId);
            await live.FlushAsync();
        }
        using (var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var docked = Until(reopened, f.Clock, state => state.Pilot.Position is null);
            Assert.Equal(capsule, docked.Pilot.ShipId);
            Assert.Equal(Destination, docked.Pilot.StationId);
            var live = reopened.GetRequiredService<LiveWorld>();
            live.Economy.Inspect(action =>
            {
                Assert.Single(action.Set<ShipLoss>().Where(loss => loss.PilotId == founder.PilotId));
                Assert.Null(action.Ships.Find(founder.FounderShipId));
                return true;
            });
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void GateRejectionPreservesShipLocationAndWallet(bool wrongShip, bool wrongExit)
    {
        using Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var founder = f.Founder();
        long hauler = founder.PilotId;
        var ships = host.GetRequiredService<SqliteWorld>();
        var pilot = ships.FindPilot(-1, hauler)!;
        ships.SetPosition(pilot.ShipId, new(0, 0, 0), SourceSystem);
        var gate = host.GetRequiredService<IPilotGateTravel>();
        Assert.Throws<InvalidDataException>(() =>
            gate.Transfer(hauler, wrongShip ? pilot.ShipId + 1 : pilot.ShipId, SourceGate, wrongExit ? ExitGate + 1 : ExitGate)
        );
        Assert.Equal(SpacePosition.Zero, ships.FindPilot(-1, hauler)!.Position);
        Assert.Equal(HaulingRules.StartingCents, host.GetRequiredService<IPilotMarket>().Balance(hauler));
    }

    [Theory]
    [InlineData(1000, 100, 1000000, 100, 0.1, 1000)]
    [InlineData(1000, 100, 1000000, 5, 0.1, 50)]
    [InlineData(1000, 100, 0, 100, 0.1, 0)]
    [InlineData(1000, 0, 1000000, 100, 0.1, 0)]
    public void LoadSelectionRespectsCargoAndAvailableFunds(
        int available,
        long price,
        long balance,
        double capacity,
        double volume,
        int expected
    ) => Assert.Equal(expected, HaulingRules.PurchaseQuantity(available, price, balance, capacity, volume));

    private static HaulerObservation Until(ServiceProvider host, TestClock clock, Func<HaulerObservation, bool> ready)
    {
        HaulerObservation? last = null;
        for (int i = 0; i < 10000; i++)
        {
            clock.Seconds++;
            last = CapsuleerTestDriver.Hauler(host, clock);
            host.GetRequiredService<ICapsuleerScene>().Publish(last);
            Assert.Null(last.Work.WaitingReason);
            if (ready(last))
            {
                return last;
            }
        }
        throw new InvalidOperationException($"Hauler did not finish {last?.Work.Activity}, target {last?.Work.TargetId}.");
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = TestDirectory.Create("capsuleer-hauling-");
        internal WorldDatabase Database { get; }
        internal TestClock Clock { get; } = new();

        internal Fixture(
            bool legacyFounders = true,
            bool isolateRoute = false,
            bool migrateRoutineTables = false,
            Action<string>? additionalCatalogData = null
        )
        {
            Database = new(Path.Combine(_directory.FullName, "world.sqlite"));
            CapsuleerMinerTests.CreateWorld(Database, Clock, legacyFounders, additionalCatalogData);
            using (WorldDbContext world = new(Database.Path))
            {
                world.Database.ExecuteSqlRaw(
                    """
                    DROP TABLE WorldCapsuleerJob;
                    DROP TABLE WorldCapsuleerAgent;
                    DROP TABLE WorldCapsuleerPopulation;
                    DROP TABLE WorldCapsuleerClock;
                    DROP TABLE WorldCapsuleerHauler;
                    PRAGMA user_version=114;
                    """
                );
                if (migrateRoutineTables)
                {
                    // Let startup recreate these tables with the foreign keys present in upgraded worlds.
                    world.Database.ExecuteSqlRaw("DROP TABLE WorldCapsuleerAdmission; DROP TABLE WorldCapsuleerCoarsePilot;");
                }
            }
            using (CatalogDbContext catalog = new(Database.CatalogPath, writable: true))
            {
                if (isolateRoute)
                {
                    // The stock fixture also connects Jita to stationless Perimeter.
                    // Single-route scenarios exercise just the Kisogo/Jita pair.
                    catalog.Database.ExecuteSqlRaw("DELETE FROM Stargate WHERE SolarSystemId=30000144 OR Id=50001249");
                }
                catalog.Database.ExecuteSqlRaw(
                    """
                    UPDATE Station SET OperationId=14 WHERE Id IN (60015157,60003760);
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7]}}');
                    INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                        (50009991,30000141,16,10,3532,900000000000,-100000000000,-1100000000000),
                        (50009992,30000142,16,10,3532,900000000000,-100000000000,-1100000000000);
                    INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                        (50009991,30000141,50009992),(50009992,30000142,50009991);
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30000142','{{"security":1.0}}');
                    """
                );
            }
            WorldInitialization.Initialize(Database, Clock);
            if (!legacyFounders)
            {
                using WorldDbContext state = new(Database.Path);
                state.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO WorldCapsuleerProvisioning(Id,WorldSeed) VALUES(1,1)");
            }
            if (legacyFounders)
            {
                using WorldDbContext founders = new(Database.Path);
                using CatalogDbContext source = new(Database.CatalogPath);
                using var transaction = founders.Database.BeginTransaction();
                CapsuleerCommunityStore.Provision(founders, source, Clock);
                CapsuleerPopulationStore.Reconcile(founders, source);
                CapsuleerPopulationStore.ScheduleFounders(founders);
                transaction.Commit();
            }
        }

        internal SavedCapsuleerHauler Founder()
        {
            using WorldDbContext world = new(Database.Path);
            return world.Set<SavedCapsuleerHauler>().AsNoTracking().Single();
        }

        internal void AddStock(long miner, int quantity)
        {
            using WorldDbContext world = new(Database.Path);
            world.Add(
                new OwnedItem
                {
                    Id = 3000000000000,
                    OwnerId = miner,
                    TypeId = Ore,
                    LocationId = Source,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = quantity,
                }
            );
            world.SaveChanges();
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
