using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotFleetTests
{
    [Fact]
    public async Task ResidentShipAssemblyAndBoardingAvoidWorldSqlAndCheckpointTheActiveHull()
    {
        using Fixture fixture = new();
        var original = fixture.Pilot;
        var item = fixture.Buy();
        using (LiveWorld live = new(new(fixture.Db.Path), TimeProvider.System, _ => { }))
        {
            live.Economy.Warm();
            List<string> commands = [];
            using (SessionPerformance.Begin("resident ship assembly and boarding", _ => { }, force: true, command: commands.Add))
            {
                Assert.Equal(item.Id, Assert.Single(fixture.Fleet.Assemble(original.PilotId, [item.Id], "Prospector").ShipIds));
                Assert.Equal(item.Id, fixture.Fleet.Board(original.PilotId, item.Id, original.ShipId).Pilot.ShipId);
                Assert.Equal("Prospector", fixture.Fleet.Name(original.PilotId, item.Id));
                Assert.Empty(fixture.Fleet.Assemble(original.PilotId, [item.Id], "Retry").Changes);
            }
            Assert.DoesNotContain(commands, sql => sql.Contains("World", StringComparison.Ordinal));
            await live.FlushAsync();
        }
        Assert.Equal(item.Id, fixture.Pilot.ShipId);
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Null(saved.Set<OwnedItem>().Find(item.Id));
        Assert.Equal("Prospector", saved.Ships.Find(item.Id)!.Name);
    }

    [Fact]
    public void ResidentHullReadsUseCommittedMembershipWithoutSql()
    {
        using TestWorld fixture = new();
        var pilot = fixture.Store.FindPilot(1000001)!;
        var fleet = fixture.Services.GetRequiredService<IPilotFleet>();
        var docked = Assert.Single(fleet.Ships(pilot.PilotId, pilot.StationId));
        using LiveWorld live = new(new(fixture.Path), TimeProvider.System, _ => { });
        live.Economy.Warm();
        using (var metrics = SessionPerformance.Begin("resident docked hull", _ => { }, force: true)!)
        {
            Assert.Equal(docked, fleet.Find(pilot.PilotId, pilot.ShipId));
            Assert.Equal(docked, Assert.Single(fleet.Ships(pilot.PilotId, pilot.StationId)));
            Assert.Null(fleet.Find(pilot.PilotId + 1, pilot.ShipId));
            Assert.Null(fleet.Find(pilot.PilotId, long.MaxValue));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        fixture.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, pilot.SystemId);
        Assert.Equal(pilot.SystemId, fleet.Find(pilot.PilotId, pilot.ShipId)!.LocationId);
        Assert.Empty(fleet.Ships(pilot.PilotId, pilot.StationId));
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public IPilotFleet Fleet => Db.Services.GetRequiredService<IPilotFleet>();
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();

        public Fixture()
        {
            AddVenture(Db);
            using WorldDbContext world = new(Db.Path);
            world.Add(new PilotWallet { PilotId = Pilot.PilotId, Cents = 2000000 });
            world.SaveChanges();
        }

        public InventoryItem Buy(int quantity = 1) =>
            Assert
                .Single(
                    Db.Services.GetRequiredService<IPilotMarket>()
                        .Buy(Pilot.PilotId, Pilot.StationId, [new((int)StarterShipType.Venture, 1000000, quantity)])
                        .Changes
                )
                .Item;

        public void Dispose() => Db.Dispose();
    }

    internal static void AddVenture(TestWorld db)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (14,'hiSlots'),(11,'powerOutput'),(48,'cpuOutput'),
                    (1556,'generalMiningHoldCapacity'),(207,'miningYieldMultiplier'),(1842,'miningFrigateYieldBonus'),
                    (182,'requiredSkill1'),(277,'requiredSkill1Level');
                INSERT INTO ItemGroup VALUES (25,'Frigate',6);
                INSERT INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES (32880,'Venture',25,50,1200000,41);
                INSERT INTO SourceRecord VALUES ('client/types','32880','{{"published":1,"marketGroupID":1,"volume":29500}}');
                INSERT INTO TypeAttribute SELECT 32880,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
                INSERT OR REPLACE INTO TypeAttribute VALUES (32880,14,3),(32880,102,2),(32880,11,45),(32880,48,240),
                    (32880,1556,5000),(32880,207,2),(32880,1842,5),(32880,182,32918),(32880,277,1);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (32918,'Mining Frigate',257);
                INSERT INTO TypeAttribute VALUES (32918,275,2);
                INSERT OR IGNORE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7,17]}}');
                """
            );
        }
        using WorldDbContext world = new(db.Path);
        world.Add(
            new PilotSkill
            {
                PilotId = db.Store.FindPilot(1000001)!.PilotId,
                TypeId = (int)MiningSkill.Frigate,
                Points = 500,
            }
        );
        world.SaveChanges();
    }

    [Fact]
    public void PurchasedStackAssemblesWithoutLosingUnitsAndBoardsWithoutMovingOldCargo()
    {
        using Fixture f = new();
        var original = f.Pilot;
        var starter = Assert.Single(
            f.Inventory.List(original.PilotId, original.StationId),
            i => i.TypeId == InventoryDefinitions.CivilianMinerType
        );
        f.Inventory.Move(original.PilotId, [starter.Id], original.StationId, original.ShipId, (int)InventoryFlag.Cargo, null);
        var stack = f.Buy(2);
        Assert.Throws<InvalidDataException>(() =>
            f.Fleet.Assemble(original.PilotId, [stack.Id], new string('x', FleetDefinitions.MaximumShipNameLength + 1))
        );
        Assert.Throws<InvalidDataException>(() => f.Fleet.Assemble(original.PilotId, [stack.Id, long.MaxValue], null));
        Assert.Equal(
            2,
            Assert.Single(f.Inventory.List(original.PilotId, original.StationId), i => i.TypeId == (int)StarterShipType.Venture).Quantity
        );
        Assert.Single(f.Fleet.Ships(original.PilotId, original.StationId));
        var result = f.Fleet.Assemble(original.PilotId, [stack.Id], "Prospector");
        long first = Assert.Single(result.ShipIds);
        Assert.NotEqual(stack.Id, first);
        Assert.Equal(
            1,
            Assert.Single(f.Inventory.List(original.PilotId, original.StationId), i => i.TypeId == (int)StarterShipType.Venture).Quantity
        );
        Assert.Empty(f.Fleet.Assemble(original.PilotId, [first], "Ignored retry").Changes);
        long second = Assert.Single(f.Fleet.Assemble(original.PilotId, [stack.Id], null).ShipIds);
        Assert.Equal(stack.Id, second);
        Assert.DoesNotContain(f.Inventory.List(original.PilotId, original.StationId), i => i.CategoryId == (int)InventoryCategory.Ship);
        Assert.Equal(3, f.Fleet.Ships(original.PilotId, original.StationId).Count);
        var boarded = f.Fleet.Board(original.PilotId, first, original.ShipId).Pilot;
        Assert.Equal((int)StarterShipType.Venture, boarded.ShipTypeId);
        Assert.Equal("Prospector", boarded.ShipName);
        Assert.Equal(50, boarded.CargoCapacity);
        Assert.Equal(first, f.Fleet.Board(original.PilotId, first, original.ShipId).Pilot.ShipId);
        Assert.Single(f.Inventory.List(original.PilotId, original.ShipId));
        Assert.Empty(f.Inventory.List(original.PilotId, first));
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(first, f.Pilot.ShipId);
        Assert.Equal(original.ShipId, f.Fleet.Board(original.PilotId, original.ShipId, first).Pilot.ShipId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.True(InventoryPersistence.NextId(saved) > Math.Max(first, second));
        Assert.Equal(2, TaskTrackingTestData.Count(f.Db, TaskEvents.Boarded));
    }

    [Fact]
    public void BoardingRejectsForeignRemotePackagedUntrainedAndInSpaceShips()
    {
        using Fixture f = new();
        var original = f.Pilot;
        var stack = f.Buy();
        Assert.Throws<InvalidDataException>(() => f.Fleet.Board(original.PilotId, stack.Id, original.ShipId));
        var ship = Assert.Single(f.Fleet.Assemble(original.PilotId, [stack.Id], null).ShipIds);
        Assert.Throws<InvalidDataException>(() => f.Fleet.Board(90000002, ship, f.Db.Store.FindPilot(1000002)!.ShipId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(original.PilotId, (int)MiningSkill.Frigate)!.Points = 0;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Fleet.Board(original.PilotId, ship, original.ShipId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotSkill>().Find(original.PilotId, (int)MiningSkill.Frigate)!.Points = 500;
            world.Ships.Find(ship)!.StationId++;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Fleet.Board(original.PilotId, ship, original.ShipId));
        f.Db.Store.SetPosition(original.ShipId, SpacePosition.Zero, original.SystemId);
        Assert.Throws<InvalidDataException>(() => f.Fleet.Board(original.PilotId, original.ShipId, original.ShipId));
        Assert.Throws<InvalidDataException>(() => f.Fleet.Assemble(original.PilotId, [ship], null));
        Assert.Equal(original.ShipId, f.Pilot.ShipId);
    }

    [Fact]
    public void NativeAssemblyBoardingAndBoundInventoriesKeepDistinctShipIdentities()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        var cargo = Assert.Single(
            f.Inventory.List(pilot.PilotId, pilot.StationId),
            i => i.TypeId == InventoryDefinitions.CivilianMinerType
        );
        f.Inventory.Move(pilot.PilotId, [cargo.Id], pilot.StationId, pilot.ShipId, (int)InventoryFlag.Cargo, null);
        var stack = f.Buy();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, TimeProvider.System, new SharedSpace(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        object? Call(string service, string method, params object?[] args) => router.CallWire(context, service, method, args, _ => { });
        Assert.IsType<List<object?>>(Call("ship", "AssembleShip", new List<object?> { stack.Id }));
        var reference = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", pilot.ShipId, 1L));
        string originalContainer = context.ResolveReference(reference.Id)!;
        Assert.IsType<BlueConstructor>(Call(originalContainer, "GetSelfInvItem"));
        var state = Assert.IsType<object?[]>(Call("ship", "Board", stack.Id, pilot.ShipId));
        Assert.Equal(4, state.Length);
        Assert.Equal(stack.Id, context.Pilot.ShipId);
        var brain = Assert.Single(context.Notifications.OfType<ServerBrainUpdated>());
        Assert.NotEmpty(brain.Brain.GrayMatter);
        Assert.IsType<BlueConstructor>(Call(originalContainer, "GetSelfInvItem"));
        var flags = new Dictionary<string, object?> { ["flags"] = new List<object?> { (long)InventoryFlag.Cargo } };
        Assert.Single(
            Assert.IsType<List<object?>>(
                router.CallWire(context, originalContainer, "ListByFlags", Array.Empty<object?>(), _ => { }, flags)
            )
        );
        string newContainer = context.ResolveReference(
            Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", stack.Id, 1L)).Id
        )!;
        Assert.Empty(
            Assert.IsType<List<object?>>(router.CallWire(context, newContainer, "ListByFlags", Array.Empty<object?>(), _ => { }, flags))
        );
        var locations = Assert.IsType<object?[]>(
            Call("config", "GetMultiLocationsEx", new List<object?> { pilot.ShipId, stack.Id, f.Db.Store.FindPilot(1000002)!.ShipId })
        );
        var names = Assert.IsType<List<object?>>(locations[1]).Cast<object?[]>().ToArray();
        Assert.Equal(2, names.Length);
        var previous = Assert.Single(names, row => Equals(row[0], pilot.ShipId));
        Assert.Equal(pilot.ShipName, previous[1]);
        Assert.Equal(pilot.SystemId, previous[2]);
        Assert.NotEmpty(BlueMarshal.Encode(state));
        Assert.Equal(3, Assert.IsType<List<object?>>(Call("@hangar", "List")).Count);
    }

    [Fact]
    public void BoardingFromACapsuleRemovesOnlyThatCapsuleAcrossRepeatedCyclesAndRestart()
    {
        using Fixture f = new();
        HomeStationTestData.Prepare(f.Db);
        var original = f.Pilot;
        long unused = HomeStationTestData.MakeActiveCapsule(f.Db, HomeStationTestData.Other);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Pilots.Find(original.PilotId)!.ActiveShipId = original.ShipId;
            world.SaveChanges();
        }
        var cargo = Assert.Single(
            f.Inventory.List(original.PilotId, original.StationId),
            i => i.TypeId == InventoryDefinitions.CivilianMinerType
        );
        f.Inventory.Move(original.PilotId, [cargo.Id], original.StationId, original.ShipId, (int)InventoryFlag.Cargo, null);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            var capsule = f.Fleet.Leave(original.PilotId, original.ShipId).Pilot;
            Assert.Empty(f.Fleet.Board(original.PilotId, capsule.ShipId, capsule.ShipId).Changes);
            Assert.NotNull(f.Fleet.Find(original.PilotId, capsule.ShipId));
            var boarding = f.Fleet.Board(original.PilotId, original.ShipId, capsule.ShipId);
            Assert.Equal(original.ShipId, boarding.Pilot.ShipId);
            var removed = Assert.Single(boarding.Changes);
            Assert.Equal(capsule.ShipId, removed.Item.Id);
            Assert.Equal(0, removed.Item.Quantity);
            Assert.Equal(original.StationId, removed.Previous!.LocationId);
            Assert.Equal(1, removed.Previous.Quantity);
            Assert.Null(f.Fleet.Find(original.PilotId, capsule.ShipId));
            Assert.Empty(f.Fleet.Board(original.PilotId, original.ShipId, capsule.ShipId).Changes);
            Assert.Equal(original.ShipId, WorldInitialization.Open(f.Db.Path).FindPilot(1000001)!.ShipId);
            Assert.DoesNotContain(f.Fleet.Ships(original.PilotId, original.StationId), s => s.GroupId == FleetDefinitions.CapsuleGroup);
            Assert.NotNull(f.Fleet.Find(original.PilotId, unused));
            Assert.Equal(cargo.Id, Assert.Single(f.Inventory.List(original.PilotId, original.ShipId)).Id);
        }
        Assert.Equal(3, TaskTrackingTestData.Count(f.Db, TaskEvents.Boarded));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("remote")]
    [InlineData("packaged")]
    [InlineData("untrained")]
    [InlineData("stale")]
    [InlineData("space")]
    public void RejectedBoardingPreservesTheActiveCapsuleAndTarget(string rejection)
    {
        using Fixture f = new();
        HomeStationTestData.Prepare(f.Db);
        var original = f.Pilot;
        var stack = f.Buy();
        long target = rejection == "packaged" ? stack.Id : Assert.Single(f.Fleet.Assemble(original.PilotId, [stack.Id], null).ShipIds);
        var capsule = f.Fleet.Leave(original.PilotId, original.ShipId).Pilot;
        long previous = capsule.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            switch (rejection)
            {
                case "foreign":
                    target = f.Db.Store.FindPilot(1000002)!.ShipId;
                    break;
                case "remote":
                    world.Ships.Find(target)!.StationId = HomeStationTestData.Other;
                    break;
                case "untrained":
                    world.Set<PilotSkill>().Find(original.PilotId, (int)MiningSkill.Frigate)!.Points = 0;
                    break;
                case "stale":
                    previous = original.ShipId;
                    break;
                case "space":
                    f.Db.Store.SetPosition(capsule.ShipId, SpacePosition.Zero, original.SystemId);
                    break;
            }
            world.SaveChanges();
        }
        using WorldDbContext saved = new(f.Db.Path);
        var before = saved
            .Ships.AsNoTracking()
            .OrderBy(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.OwnerId,
                s.StationId,
            })
            .ToArray();
        Assert.Throws<InvalidDataException>(() => f.Fleet.Board(original.PilotId, target, previous));
        Assert.Equal(capsule.ShipId, f.Pilot.ShipId);
        Assert.NotNull(f.Fleet.Find(original.PilotId, capsule.ShipId));
        Assert.Equal(
            before,
            saved
                .Ships.AsNoTracking()
                .OrderBy(s => s.Id)
                .Select(s => new
                {
                    s.Id,
                    s.OwnerId,
                    s.StationId,
                })
                .ToArray()
        );
        Assert.Equal(0, TaskTrackingTestData.Count(f.Db, TaskEvents.Boarded));
    }

    [Fact]
    public void CapsuleRemovalFailureRollsBackTheShipSwitchAndTaskProgress()
    {
        using Fixture f = new();
        HomeStationTestData.Prepare(f.Db);
        var original = f.Pilot;
        var capsule = f.Fleet.Leave(original.PilotId, original.ShipId).Pilot;
        using WorldDbContext world = new(f.Db.Path);
        world.Database.ExecuteSqlRaw(
            """
            CREATE TRIGGER RejectCapsuleRemoval BEFORE DELETE ON WorldShip
            WHEN OLD.TypeId = 670 BEGIN SELECT RAISE(ABORT, 'test capsule removal failure'); END;
            """
        );
        Assert.Throws<DbUpdateException>(() => f.Fleet.Board(original.PilotId, original.ShipId, capsule.ShipId));
        Assert.Equal(capsule.ShipId, f.Pilot.ShipId);
        Assert.NotNull(f.Fleet.Find(original.PilotId, capsule.ShipId));
        Assert.NotNull(f.Fleet.Find(original.PilotId, original.ShipId));
        Assert.Equal(0, TaskTrackingTestData.Count(f.Db, TaskEvents.Boarded));
    }

    [Fact]
    public void NativeBoardingRemovesTheCapsuleFromTheCachedHangarAndKeepsTheDogmaReply()
    {
        using Fixture f = new();
        HomeStationTestData.Prepare(f.Db);
        var original = f.Pilot;
        var capsule = f.Fleet.Leave(original.PilotId, original.ShipId).Pilot;
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            capsule.Account,
            [capsule],
            p => new(f.Db.Store, p, TimeProvider.System, new SharedSpace(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(capsule);
        var reply = router.CallWire(context, "ship", "Board", new object?[] { original.ShipId, capsule.ShipId }, _ => { });
        Assert.Equal(4, Assert.IsType<object?[]>(reply).Length);
        Assert.NotEmpty(BlueMarshal.Encode(reply));
        Assert.Equal(original.ShipId, context.Pilot.ShipId);
        var removed = Assert.Single(context.Notifications.OfType<ItemChanged>());
        Assert.Equal(capsule.ShipId, removed.Item.Id);
        Assert.Equal((int)InventoryLocation.Graveyard, removed.Item.LocationId);
        Assert.Equal((int)InventoryFlag.AutoFit, removed.Item.FlagId);
        Assert.Equal(InventoryWireDefinitions.AssembledQuantity, removed.Item.Quantity);
        Assert.Equal(original.StationId, removed.Previous[InventoryColumn.Location]);
        Assert.Equal((int)InventoryFlag.Hangar, removed.Previous[InventoryColumn.Flag]);
        Assert.False(removed.Previous.ContainsKey(InventoryColumn.Quantity));
        Assert.Single(context.Notifications.OfType<ServerBrainUpdated>());
        Assert.NotEmpty(BlueMarshal.Encode(removed.Arguments));
        context.Notifications.Clear();
        router.CallWire(context, "ship", "Board", new object?[] { original.ShipId, capsule.ShipId }, _ => { });
        Assert.Empty(context.Notifications.OfType<ItemChanged>());
    }

    [Fact]
    public void MiningHoldAcceptsOnlyOreAndEnforcesCapacityIndependentlyOfCargo()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        var stack = f.Buy();
        long ship = Assert.Single(f.Fleet.Assemble(pilot.PilotId, [stack.Id], null).ShipIds);
        f.Fleet.Board(pilot.PilotId, ship, pilot.ShipId);
        var starter = Assert.Single(
            f.Inventory.List(pilot.PilotId, pilot.StationId),
            i => i.TypeId == InventoryDefinitions.CivilianMinerType
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Inventory.Move(pilot.PilotId, [starter.Id], pilot.StationId, ship, (int)InventoryFlag.GeneralMiningHold, null)
        );
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemCategory VALUES (25,'Asteroid');
                INSERT INTO ItemGroup VALUES (462,'Veldspar',25);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (1230,'Veldspar',462);
                INSERT INTO SourceRecord VALUES ('client/types','1230','{{"published":1,"marketGroupID":1,"volume":0.1}}');
                """
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = stack.Id + 10,
                    OwnerId = pilot.PilotId,
                    TypeId = 1230,
                    LocationId = pilot.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 50001,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<CargoCapacityException>(() =>
            f.Inventory.Move(pilot.PilotId, [stack.Id + 10], pilot.StationId, ship, (int)InventoryFlag.GeneralMiningHold, null)
        );
        f.Inventory.Move(pilot.PilotId, [stack.Id + 10], pilot.StationId, ship, (int)InventoryFlag.GeneralMiningHold, 50000);
        var ore = Assert.Single(f.Inventory.List(pilot.PilotId, ship));
        Assert.Equal(50000, ore.Quantity);
        f.Inventory.Move(pilot.PilotId, [ore.Id], ship, pilot.StationId, (int)InventoryFlag.Hangar, null);
        Assert.Empty(f.Inventory.List(pilot.PilotId, ship));
    }
}
