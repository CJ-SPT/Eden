using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotFleetTests
{
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
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (14,'hiSlots'),(11,'powerOutput'),(48,'cpuOutput'),
                (1556,'generalMiningHoldCapacity'),(207,'miningYieldMultiplier'),(1842,'miningFrigateYieldBonus'),
                (182,'requiredSkill1'),(277,'requiredSkill1Level');
            INSERT INTO ItemGroup VALUES (25,'Frigate',6);
            INSERT INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES (32880,'Venture',25,50,1200000,41);
            INSERT INTO SourceRecord VALUES ('client/types','32880','{{"volume":29500}}');
            INSERT INTO TypeAttribute SELECT 32880,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
            INSERT OR REPLACE INTO TypeAttribute VALUES (32880,14,3),(32880,102,2),(32880,11,45),(32880,48,240),
                (32880,1556,5000),(32880,207,2),(32880,1842,5),(32880,182,32918),(32880,277,1);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (32918,'Mining Frigate',257);
            INSERT INTO TypeAttribute VALUES (32918,275,2);
            INSERT OR IGNORE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7,17]}}');
            """
        );
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
        var boarded = f.Fleet.Board(original.PilotId, first, original.ShipId);
        Assert.Equal((int)StarterShipType.Venture, boarded.ShipTypeId);
        Assert.Equal("Prospector", boarded.ShipName);
        Assert.Equal(50, boarded.CargoCapacity);
        Assert.Equal(first, f.Fleet.Board(original.PilotId, first, original.ShipId).ShipId);
        Assert.Single(f.Inventory.List(original.PilotId, original.ShipId));
        Assert.Empty(f.Inventory.List(original.PilotId, first));
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(first, f.Pilot.ShipId);
        Assert.Equal(original.ShipId, f.Fleet.Board(original.PilotId, original.ShipId, first).ShipId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.True(PilotInventoryStore.NextId(saved) > Math.Max(first, second));
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
                INSERT INTO SourceRecord VALUES ('client/types','1230','{{"volume":0.1}}');
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
