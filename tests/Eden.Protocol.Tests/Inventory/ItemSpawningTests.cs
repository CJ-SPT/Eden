using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Inventory;

public sealed class ItemSpawningTests
{
    private const long AccountId = 1000001;
    private const long PilotId = 90000001;
    private const int MinerType = InventoryDefinitions.CivilianMinerType;

    [Fact]
    public void CreatedBlueprintsRemainOriginalsAndInvalidateNativeBlueprintLists()
    {
        using Fixture f = new(blueprint: true);
        long id = f.Create($"/createitem {(int)IndustryBlueprintType.IronChargeSmall} 2");
        var item = Assert.Single(f.Inventory.List(PilotId, f.Context.Pilot.StationId), i => i.Id == id);
        Assert.Equal((int)InventoryCategory.Blueprint, item.CategoryId);
        Assert.Equal(2, item.Quantity);
        Assert.IsType<ItemChanged>(f.Context.Notifications[0]);
        Assert.Equal(PilotId, Assert.IsType<BlueprintsUpdated>(f.Context.Notifications[1]).OwnerId);
        Assert.Equal(2, f.Context.Notifications.Count);
    }

    [Fact]
    public void NativeBrowserCreatesStacksAndPackagedShipsWithInventoryNotifications()
    {
        using Fixture f = new(venture: true);
        var before = Assert.Single(f.Inventory.List(PilotId, f.Context.Pilot.StationId), i => i.TypeId == MinerType);
        long id = f.Create($"/createitem {MinerType} 3");
        Assert.Equal(before.Id, id);
        var change = Assert.IsType<ItemChanged>(Assert.Single(f.Context.Notifications));
        Assert.Equal(1, change.Previous[InventoryColumn.Quantity]);
        var stack = Assert.Single(f.Inventory.List(PilotId, f.Context.Pilot.StationId), i => i.Id == id);
        Assert.Equal(4, stack.Quantity);
        Assert.Equal(PilotId, stack.OwnerId);
        Assert.Equal((int)InventoryFlag.Hangar, stack.FlagId);
        Assert.False(stack.Singleton);
        Assert.Equal(InventoryRow.From(stack), change.Item);
        Assert.Equal(1, change.Previous[InventoryColumn.StackSize]);
        f.Context.Notifications.Clear();
        long ship = f.Create($"/create {(int)StarterShipType.Venture} 2");
        Assert.Single(f.Context.Notifications);
        var inserted = Assert.IsType<ItemChanged>(Assert.Single(f.Context.Notifications));
        Assert.Equal(0, inserted.Previous[InventoryColumn.Location]);
        Assert.Equal(0, inserted.Previous[InventoryColumn.StackSize]);
        var packaged = Assert.Single(f.Inventory.List(PilotId, f.Context.Pilot.StationId), i => i.Id == ship);
        Assert.Equal(2, packaged.Quantity);
        Assert.Equal((int)InventoryCategory.Ship, packaged.CategoryId);
        Assert.False(packaged.Singleton);
        var assembly = f.Db.Services.GetRequiredService<IPilotFleet>().Assemble(PilotId, [ship], null);
        Assert.Single(assembly.ShipIds);
        Assert.Equal(1, Assert.Single(f.Inventory.List(PilotId, f.Context.Pilot.StationId), i => i.Id == ship).Quantity);
        Assert.All(
            assembly.ShipIds,
            assembled => Assert.True(f.Db.Services.GetRequiredService<IPilotFleet>().Find(PilotId, assembled)!.Singleton)
        );
        Assert.Single(f.Inventory.List(90000002, f.Context.Pilot.StationId), i => i.TypeId == MinerType && i.Quantity == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadingCargoHonorsUsedCapacityAndRollsBackRejections(bool inSpace)
    {
        using Fixture f = new(inSpace: inSpace);
        long item = f.Create($"/LoAd Me {MinerType} 2");
        var stack = Assert.Single(f.Inventory.List(PilotId, f.Context.Pilot.ShipId));
        Assert.Equal(item, stack.Id);
        Assert.Equal(2, stack.Quantity);
        Assert.Equal((int)InventoryFlag.Cargo, stack.FlagId);
        string before = f.Items();
        f.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.NamedFailure>(f.Call($"/load me {MinerType} 2147483647"));
        Assert.Equal(before, f.Items());
        Assert.Empty(f.Context.Notifications);
        f.Create($"/load me {MinerType}");
        Assert.Equal(3, Assert.Single(f.Inventory.List(PilotId, f.Context.Pilot.ShipId)).Quantity);
        if (inSpace)
        {
            f.Context.Notifications.Clear();
            f.Reject($"/createitem {MinerType}");
        }
    }

    [Theory]
    [InlineData(PilotRole.Player, "/createitem 3651")]
    [InlineData(PilotRole.Player, "/load me 3651")]
    [InlineData(PilotRole.QualityAssurance, "/create 3651")]
    [InlineData(PilotRole.GameMasterLow, "/create 3651")]
    [InlineData(PilotRole.WorldMod, "/load me 3651")]
    public void ItemCreationChecksCommandRoles(PilotRole roles, string command)
    {
        using Fixture f = new(roles: roles);
        f.Reject(command);
    }

    [Theory]
    [InlineData(PilotRole.WorldMod, "/create 3651")]
    [InlineData(PilotRole.GameMasterLow, "/load me 3651")]
    [InlineData(PilotRole.GameMasterHigh, "/load me 3651")]
    public void EachCommandAcceptsItsCorrespondingRole(PilotRole roles, string command)
    {
        using Fixture f = new(roles: roles);
        Assert.True(f.Create(command) > 0);
    }

    [Fact]
    public void InvalidCommandsUnknownTypesAndOverflowLeaveInventoryUnchanged()
    {
        using Fixture f = new();
        f.Reject($"/createitem {f.Context.Pilot.Home.TypeId}");
        foreach (
            string command in new[]
            {
                "/createitem",
                "/createitem -1",
                "/createitem 3651 0",
                "/createitem 3651 -1",
                "/createitem 3651 2147483648",
                "/createitem 3651 1 extra",
                "/createitem 2147483647",
                "/load 3651",
                "/load 90000002 3651 1",
                "/load me",
                "/createitem 3651; /tr me 1",
                "/createitem 3651\0",
                "/createitem 3651 " + new string('1', 129),
            }
        )
        {
            f.Reject(command);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            var stack = world.Set<OwnedItem>().Single(i => i.OwnerId == PilotId && i.TypeId == MinerType);
            stack.Quantity = int.MaxValue;
            world.SaveChanges();
        }
        f.Reject($"/create {MinerType} 1");
        string before = f.Items();
        Assert.Throws<InvalidDataException>(() =>
            f
                .Db.Services.GetRequiredService<IPilotItemSpawning>()
                .Create(PilotId, f.Context.Pilot.ShipId + 1, new(MinerType, 1, ItemSpawnDestination.ShipCargo))
        );
        Assert.Equal(before, f.Items());
        Assert.IsType<PilotCallResult.Failure>(f.Call($"/create {MinerType}", new() { ["quantity"] = 1L }));
        Assert.Equal(before, f.Items());
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void UnselectedPilotsCannotCreateItems()
    {
        using Fixture f = new(select: false);
        string before = f.Items();
        Assert.Equal(PilotCallResult.Unsupported, f.Call($"/createitem {MinerType}"));
        Assert.Equal(before, f.Items());
        Assert.Empty(f.Context.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatedItemsSurviveReopeningAndResidentCheckpoint(bool resident)
    {
        using Fixture f = new(resident: resident);
        long hangar = f.Create($"/createitem {MinerType} 5");
        long cargo = f.Create($"/load me {MinerType} 2");
        long station = f.Context.Pilot.StationId;
        long ship = f.Context.Pilot.ShipId;
        f.DisposeSession();
        f.Db.Services.Dispose();
        WorldInitialization.Open(f.Db.Path);
        using var reopened = ServerServices.Build(new(f.Db.Path));
        var inventory = reopened.GetRequiredService<IPilotInventory>();
        Assert.Equal(6, Assert.Single(inventory.List(PilotId, station), i => i.Id == hangar).Quantity);
        Assert.Equal(2, Assert.Single(inventory.List(PilotId, ship), i => i.Id == cargo).Quantity);
    }

    [Theory]
    [InlineData(InventoryCategory.Material)]
    [InlineData(InventoryCategory.Ship)]
    [InlineData(InventoryCategory.Module)]
    [InlineData(InventoryCategory.Blueprint)]
    [InlineData(InventoryCategory.Accessories)]
    [InlineData(InventoryCategory.Starbase)]
    [InlineData(InventoryCategory.Reaction)]
    [InlineData(InventoryCategory.Decryptor)]
    [InlineData(InventoryCategory.PlanetaryIndustry)]
    [InlineData(InventoryCategory.SpecialEditionAssets)]
    [InlineData(InventoryCategory.ShipSkin)]
    [InlineData(InventoryCategory.ExpertSystem)]
    [InlineData(InventoryCategory.Personalization)]
    [InlineData(InventoryCategory.ColonyResource)]
    public void PhysicalTypesCanBeCreatedWithoutClaimingGameplaySupport(InventoryCategory category)
    {
        ItemSpawnRules.Check(new(1, 1, ItemSpawnDestination.StationHangar), category, true);
        Assert.Throws<InvalidDataException>(() => ItemSpawnRules.Check(new(1, 1, ItemSpawnDestination.StationHangar), category, false));
        Assert.Throws<InvalidDataException>(() => ItemSpawnRules.Check(new(1, 0, ItemSpawnDestination.ShipCargo), category, true));
        Assert.Throws<InvalidDataException>(() => ItemSpawnRules.Check(new(1, 1, (ItemSpawnDestination)99), category, true));
    }

    [Theory]
    [InlineData(InventoryCategory.Station)]
    [InlineData(InventoryCategory.Celestial)]
    [InlineData((InventoryCategory)0)]
    [InlineData((InventoryCategory)53)]
    [InlineData(InventoryCategory.Entity)]
    public void NonInventoryTypesAreRejected(InventoryCategory category) =>
        Assert.Throws<InvalidDataException>(() => ItemSpawnRules.Check(new(1, 1, ItemSpawnDestination.StationHangar), category, true));

    [Fact]
    public void CapacityBoundaryAllowsExactFitAndRejectsInvalidVolumes()
    {
        ItemSpawnRules.CheckCapacity(5, 2, 10, 20);
        Assert.Throws<CargoCapacityException>(() => ItemSpawnRules.CheckCapacity(5, 2, 10.01, 20));
        Assert.Throws<InvalidDataException>(() => ItemSpawnRules.CheckCapacity(double.NaN, 1, 0, 20));
        Assert.Throws<InvalidDataException>(() => ItemSpawnRules.CheckCapacity(-1, 1, 0, 20));
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; }
        private readonly IServiceScope _scope;
        private bool _sessionDisposed;
        public PilotCallContext Context { get; }
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();

        public Fixture(
            bool inSpace = false,
            PilotRole roles = PilotRole.Developer,
            bool select = true,
            bool resident = false,
            bool venture = false,
            bool blueprint = false
        )
        {
            Db = new(liveWorld: resident);
            if (venture)
            {
                PilotFleetTests.AddVenture(Db);
            }
            if (blueprint)
            {
                using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true);
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemCategory VALUES (9,'Blueprint');
                    INSERT INTO ItemGroup VALUES (167,'Hybrid Charge Blueprint',9);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (1130,'Iron Charge S Blueprint',167);
                    INSERT INTO SourceRecord VALUES ('client/types','1130','{{"volume":0.01}}');
                    """
                );
            }
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(AccountId)!;
            if (inSpace)
            {
                Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
                pilot = Db.Store.FindPilot(AccountId)!;
            }
            Context = new(
                AccountId,
                pilot.Account,
                [pilot],
                _scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            )
            {
                Roles = (long)roles,
            };
            if (select)
            {
                Context.Select(pilot);
            }
        }

        public PilotCallResult Call(string command, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, "slash", "SlashCmd", BlueMarshal.Decode(BlueMarshal.Encode(new object?[] { command })), _ => { }, keywords);

        public long Create(string command) => Assert.IsType<long>(Assert.IsType<PilotCallResult.Success>(Call(command)).Reply.ToBlue());

        public string Items()
        {
            using WorldDbContext world = new(Db.Path);
            return JsonSerializer.Serialize(world.Set<OwnedItem>().OrderBy(i => i.Id).ToArray());
        }

        public void Reject(string command)
        {
            string before = Items();
            Assert.IsType<PilotCallResult.Failure>(Call(command));
            Assert.Equal(before, Items());
            Assert.Empty(Context.Notifications);
        }

        public void DisposeSession()
        {
            if (!_sessionDisposed)
            {
                Context.Dispose();
                _scope.Dispose();
                _sessionDisposed = true;
            }
        }

        public void Dispose()
        {
            DisposeSession();
            Db.Dispose();
        }
    }
}
