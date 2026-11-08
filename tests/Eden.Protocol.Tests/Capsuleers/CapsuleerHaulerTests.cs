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
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Chat;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerHaulerTests(ITestOutputHelper output)
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
    public async Task PurchasedOreCrossesGateSurvivesPauseAndReopenAndIsListedAtDestination()
    {
        using Fixture f = new();
        long miner;
        long hauler;
        long originalShip;
        long price;
        HaulerObservation departure;
        int cargoUnits;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var founder = f.Founder();
            hauler = founder.PilotId;
            originalShip = founder.FounderShipId;
            miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
            long available;
            using (WorldDbContext world = new(f.Database.Path))
            {
                available = world.Set<WorldAsteroid>().Sum(a => a.Quantity);
            }
            MinerObservation? mining = null;
            for (int i = 0; i < 30000; i++)
            {
                f.Clock.Seconds++;
                mining = host.GetRequiredService<ICapsuleerSimulation>().Advance(checked((int)f.Clock.Seconds))!;
                Assert.Null(mining.WaitingReason);
                if (mining.Work.Activity == MinerActivity.Docked && mining.Work.Trips > 0)
                {
                    break;
                }
            }
            Assert.Equal(MinerActivity.Docked, mining!.Work.Activity);
            f.Clock.Seconds++;
            mining = host.GetRequiredService<ICapsuleerSimulation>().Advance(checked((int)f.Clock.Seconds))!;
            using (WorldDbContext world = new(f.Database.Path))
            {
                Assert.True(world.Set<WorldAsteroid>().Sum(a => a.Quantity) < available);
            }
            var order = Assert.Single(host.GetRequiredService<IPersonalMarket>().PersonalOrders(miner));
            price = order.PriceCents;
            long before = host.GetRequiredService<IPilotMarket>().Balance(hauler);
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(-1, hauler)!;
            cargoUnits = HaulingRules.PurchaseQuantity(order.Remaining, price, before, pilot.CargoCapacity, 0.1);
            Assert.True(cargoUnits > 0);
            var trade = host.GetRequiredService<ICapsuleerHauling>();
            var loaded = trade.Advance(1)!;
            Assert.Equal(HaulerActivity.Flying, loaded.Work.Activity);
            Assert.Equal(
                cargoUnits,
                host.GetRequiredService<IPilotInventory>().List(hauler, originalShip).Sum(i => i.TypeId == Ore ? i.Quantity : 0)
            );
            Assert.Equal(before - cargoUnits * price, host.GetRequiredService<IPilotMarket>().Balance(hauler));
            Assert.Equal(
                order.Remaining - cargoUnits,
                host.GetRequiredService<IPersonalMarket>().PersonalOrders(miner).Sum(o => o.Remaining)
            );
            var sale = Assert.Single(host.GetRequiredService<IPilotMarket>().Transactions(miner));
            Assert.Equal((hauler, cargoUnits, price), (sale.CounterpartyId, sale.Quantity, sale.PriceCents));
            var scene = host.GetRequiredService<ICapsuleerScene>();
            scene.Publish(mining);
            scene.Publish(loaded);
            var human = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
            Assert.Contains(
                host.GetRequiredService<SharedSpace>()
                    .Peers(human with { Position = loaded.Pilot.Position, CurrentSystem = loaded.Pilot.CurrentSystem }),
                peer => peer.Pilot.PilotId == hauler
            );
            var cruising = Until(host, f.Clock, state => state.Work.Navigation.Warp is not null);
            host.GetRequiredService<SharedSpace>()
                .Peers(human with { Position = cruising.Pilot.Position, CurrentSystem = cruising.Pilot.CurrentSystem });
            f.Clock.Seconds++;
            using (var metrics = SessionPerformance.Begin("hauler in-flight observer frame", output.WriteLine, force: true)!)
            {
                var flying = trade.Advance(checked((int)f.Clock.Seconds))!;
                scene.Publish(flying);
                Assert.Contains(
                    host.GetRequiredService<SharedSpace>()
                        .Peers(human with { Position = flying.Pilot.Position, CurrentSystem = flying.Pilot.CurrentSystem }),
                    peer => peer.Pilot.PilotId == hauler
                );
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            departure = Until(host, f.Clock, state => state.Work.Activity == HaulerActivity.Jumping);
            Assert.Equal(SourceSystem, departure.Pilot.SystemId);
            Assert.Equal(GateTravelRules.DurationTicks, departure.Work.DueStamp - departure.Work.Stamp);
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            await live.FlushAsync();
            var checkpoint = Assert.IsType<WorldCheckpoint>(live.LastCheckpoint);
            output.WriteLine(
                $"Hauler checkpoint capture {checkpoint.Capture.TotalMilliseconds:F2} ms, "
                    + $"projection {checkpoint.Projection.TotalMilliseconds:F2} ms, write {checkpoint.Write.TotalMilliseconds:F2} ms."
            );
        }
        f.Clock.Seconds += 3600;
        WorldInitialization.Initialize(f.Database, f.Clock);
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            Assert.Equal((hauler, originalShip), (f.Founder().PilotId, f.Founder().FounderShipId));
            var simulation = host.GetRequiredService<ICapsuleerHauling>();
            f.Clock.Seconds++;
            var resumed = simulation.Advance(1)!;
            Assert.Equal(departure.Work.Stamp + 1, resumed.Work.Stamp);
            Assert.Equal(HaulerActivity.Jumping, resumed.Work.Activity);
            Until(host, f.Clock, state => state.Pilot.SystemId == DestinationSystem);
            Until(host, f.Clock, state => state.Pilot.Position is null && state.Pilot.StationId == Destination);
            f.Clock.Seconds++;
            simulation.Advance(1000);
            var order = Assert.Single(host.GetRequiredService<IPersonalMarket>().PersonalOrders(hauler));
            Assert.Equal((Destination, Ore, cargoUnits), (order.StationId, order.TypeId, order.Remaining));
            Assert.Equal(PersonalMarketRules.Round(price * HaulingRules.DestinationMultiplier), order.PriceCents);
            Assert.DoesNotContain(host.GetRequiredService<IPilotInventory>().List(hauler, originalShip), i => i.TypeId == Ore);
            Assert.DoesNotContain(host.GetRequiredService<IPilotInventory>().List(hauler, Destination), i => i.TypeId == Ore);
            Assert.Single(host.GetRequiredService<IPilotMarket>().Transactions(hauler));
            Until(host, f.Clock, state => state.Pilot.Position is null && state.Pilot.StationId == Source);
            Assert.Equal(cargoUnits, Assert.Single(host.GetRequiredService<IPersonalMarket>().PersonalOrders(hauler)).Remaining);
            long unsoldBalance = host.GetRequiredService<IPilotMarket>().Balance(hauler);
            var waitingForBuyers = simulation.Advance(1001)!;
            Assert.Equal(HaulerActivity.Waiting, waitingForBuyers.Work.Activity);
            Assert.Equal(unsoldBalance, host.GetRequiredService<IPilotMarket>().Balance(hauler));
        }
    }

    [Fact]
    public void HumanCanBuyTheMinersOrderBeforeHaulerAndNoReplacementOreIsGranted()
    {
        using Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        long miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
        f.AddStock(miner, Load);
        host.GetRequiredService<CapsuleerTrade>().ListStock(miner, Source);
        var offer = Assert.Single(host.GetRequiredService<IPersonalMarket>().PersonalOrders(miner));
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldWallet(PilotId,Cents) VALUES (90000001,{Load * offer.PriceCents})
                ON CONFLICT(PilotId) DO UPDATE SET Cents=excluded.Cents
                """
            );
        }
        host.GetRequiredService<IPilotMarket>().Buy(90000001, Source, [new(Ore, offer.PriceCents, Load)]);
        long hauler = f.Founder().PilotId;
        var waiting = host.GetRequiredService<ICapsuleerHauling>().Advance(1)!;
        Assert.Equal(HaulerActivity.Waiting, waiting.Work.Activity);
        Assert.Null(waiting.Pilot.Position);
        Assert.Equal(HaulingRules.StartingCents, host.GetRequiredService<IPilotMarket>().Balance(hauler));
        Assert.DoesNotContain(host.GetRequiredService<IPilotInventory>().List(hauler, waiting.Pilot.ShipId), i => i.TypeId == Ore);
    }

    [Fact]
    public void EmptyWorldPausesHaulerAndWarmWaitingFramesUseNoSqlOrFittingWork()
    {
        using Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var worker = host.GetRequiredService<Eden.Server.Maintenance.CapsuleerMaintenance>();
        Assert.Null(worker.Tick());
        var chat = host.GetRequiredService<ChatRuntime>();
        var session = Guid.NewGuid();
        CapsuleerMinerTests.UpdateSelection(host, session, new(90000001, "Eden Pilot", 1000044, DestinationSystem, 0));
        for (int i = 0; i < 5; i++)
        {
            f.Clock.Seconds++;
            worker.Tick();
        }
        var live = host.GetRequiredService<LiveWorld>();
        int stamp = live.HaulerCheckpoint!.Work.Stamp;
        f.Clock.Seconds++;
        using (var metrics = SessionPerformance.Begin("hauler waiting frame", output.WriteLine, force: true)!)
        {
            worker.Tick();
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Equal(stamp + 1, live.HaulerCheckpoint!.Work.Stamp);
        CapsuleerMinerTests.UpdateSelection(host, session, null);
        f.Clock.Seconds += 3600;
        using (var metrics = SessionPerformance.Begin("empty world hauling", output.WriteLine, force: true)!)
        {
            Assert.Null(worker.Tick());
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Equal(stamp + 1, live.HaulerCheckpoint!.Work.Stamp);
    }

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
        Assert.Equal(DestinationSystem, chat.User(hauler)!.SystemId);
        Assert.DoesNotContain(chat.Members(connection.Id, room), p => p.Id == hauler);
        Assert.Contains(chat.Members(connection.Id, room), p => p.Id == miner);
        var ships = host.GetRequiredService<SqliteWorld>();
        var arrival = ships.FindPilot(-1, hauler)!;
        var human = ships.FindPilot(1000001)!;
        var space = host.GetRequiredService<SharedSpace>();
        Assert.Contains(
            space.Peers(human with { Position = arrival.Position, CurrentSystem = arrival.CurrentSystem }),
            peer => peer.Pilot.PilotId == hauler && peer.Arriving
        );
        Assert.DoesNotContain(space.Peers(human with { Position = arrival.Position }), peer => peer.Pilot.PilotId == hauler);
    }

    [Fact]
    public void PurchaseCommittedBeforeFailedLoadingResumesWithoutASecondPayment()
    {
        using Fixture f = new();
        long hauler = f.Founder().PilotId;
        long paidBalance;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            long miner = host.GetRequiredService<ICapsuleerCommunity>().Pilots().Single(p => p.Career == CapsuleerCareer.Mining).PilotId;
            f.AddStock(miner, Load);
            host.GetRequiredService<CapsuleerTrade>().ListStock(miner, Source);
            using (WorldDbContext world = new(f.Database.Path))
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TRIGGER reject_loading BEFORE UPDATE OF LocationId ON WorldItem
                    WHEN NEW.FlagId=5 BEGIN SELECT RAISE(ABORT,'test loading failure'); END
                    """
                );
            }
            var failure = Assert.Throws<DbUpdateException>(() => host.GetRequiredService<ICapsuleerHauling>().Advance(1));
            Assert.Contains("test loading failure", Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(failure.InnerException).Message);
            paidBalance = host.GetRequiredService<IPilotMarket>().Balance(hauler);
            Assert.True(paidBalance < HaulingRules.StartingCents);
            Assert.Single(host.GetRequiredService<IPilotMarket>().Transactions(hauler));
            using WorldDbContext saved = new(f.Database.Path);
            saved.Database.ExecuteSqlRaw("DROP TRIGGER reject_loading");
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var loaded = reopened.GetRequiredService<ICapsuleerHauling>().Advance(1)!;
        Assert.Equal(HaulerActivity.Flying, loaded.Work.Activity);
        Assert.Equal(paidBalance, reopened.GetRequiredService<IPilotMarket>().Balance(hauler));
        Assert.Single(reopened.GetRequiredService<IPilotMarket>().Transactions(hauler));
        Assert.Equal(
            Load,
            reopened
                .GetRequiredService<IPilotInventory>()
                .List(hauler, loaded.Pilot.ShipId)
                .Where(i => i.TypeId == Ore)
                .Sum(i => i.Quantity)
        );
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
            var waiting = host.GetRequiredService<ICapsuleerHauling>().Advance(1000)!;
            Assert.Equal(HaulerActivity.Waiting, waiting.Work.Activity);
            Assert.Single(host.GetRequiredService<IPilotMarket>().Transactions(hauler));
            Assert.Empty(host.GetRequiredService<IPersonalMarket>().PersonalOrders(hauler));
            using WorldDbContext saved = new(f.Database.Path);
            Assert.NotNull(saved.Set<ShipLoss>().Find(founderShip));
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        Assert.Equal((hauler, founderShip), (f.Founder().PilotId, f.Founder().FounderShipId));
        using WorldDbContext reopened = new(f.Database.Path);
        Assert.DoesNotContain(reopened.Ships.Where(s => s.OwnerId == hauler), s => s.TypeId == HaulingRules.BadgerType);
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
            last = host.GetRequiredService<ICapsuleerHauling>().Advance(checked((int)clock.Seconds))!;
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

        internal Fixture()
        {
            Database = new(Path.Combine(_directory.FullName, "world.sqlite"));
            CapsuleerMinerTests.CreateWorld(Database, Clock);
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
            }
            using (CatalogDbContext catalog = new(Database.CatalogPath, writable: true))
            {
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
