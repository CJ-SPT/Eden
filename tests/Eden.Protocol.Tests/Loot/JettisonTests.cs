using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Loot;

public sealed partial class JettisonTests
{
    private const int SystemId = 30000142;
    private const long AccountId = 1000001;
    private static readonly DateTimeOffset s_start = DateTimeOffset.UtcNow;
    private static long Now => s_start.UtcDateTime.ToFileTimeUtc();
    private static readonly SpacePosition s_position = new(100_000_000, 0, 0);

    [Fact]
    public void BoundShipJettisonPublishesTheContainerAndSupportsNativeDepositAndLootAll()
    {
        Clock clock = new(s_start);
        using TestWorld db = NewWorld(liveWorld: true, clock);
        var pilot = db.Store.FindPilot(AccountId)!;
        long cargo = Seed(db, pilot.ShipId, InventoryFlag.Cargo, 200);
        long ore = Seed(db, pilot.ShipId, InventoryFlag.GeneralMiningHold, 100, 1230);
        long fitted = Seed(db, pilot.ShipId, InventoryFlag.HighSlot7, 1, 3651);
        db.Store.SetPosition(pilot.ShipId, s_position, SystemId);
        pilot = db.Store.FindPilot(AccountId)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var wrecks = scope.ServiceProvider.GetRequiredService<IWrecks>();
        using PilotCallContext context = new(
            AccountId,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, clock, new(), [], wrecks: wrecks),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        Assert.Empty(wrecks.List(SystemId, Now));

        var wing = db.Store.FindPilot(1000002)!;
        db.Store.SetPosition(wing.ShipId, s_position + new SpacePosition(0, 4000, 0), SystemId);
        using FlightSession observer = new(db.Store, db.Store.FindPilot(1000002)!, clock, new(), [], wrecks: wrecks);
        observer.Snapshot();
        observer.Updates.Clear();
        Assert.Empty(observer.VisibleWrecks);

        object? Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.CallWire(context, service, method, args, _ => { }, keywords);

        var bound = Call(
            "ship",
            "MachoBindObject",
            [
                new object?[] { (long)SystemId, 5L },
                new object?[]
                {
                    "Jettison",
                    new object?[]
                    {
                        new List<object?> { ore, fitted },
                    },
                    null,
                },
            ]
        );
        Assert.False(bound is PilotCallResult.Failure, bound?.ToString());
        var bind = Assert.IsType<object?[]>(bound);
        Assert.IsType<BlueObjectReference>(bind[0]);
        var response = Assert.IsType<object?[]>(bind[1]);
        Assert.Equal(ore, Assert.Single(Assert.IsType<List<object?>>(response[0])));
        Assert.Empty(Assert.IsType<List<object?>>(response[1]));

        var can = Assert.Single(context.Flight.VisibleWrecks);
        Assert.True(JetcanRules.IsJetcan(can.Id));
        Assert.Equal(JetcanRules.TypeId, can.TypeId);
        Assert.Equal(JetcanRules.GroupId, can.GroupId);
        Assert.Equal(pilot.ShipId, can.SourceId);
        Assert.InRange(can.Position.DistanceTo(s_position) - can.Radius - pilot.Flight.ShipRadius, 0, WreckDefinitions.TransferRange);
        Assert.Contains(context.Flight.Updates, u => u.Command is DestinyCommand.AddBalls);
        observer.RefreshWrecks();
        Assert.Equal(can, Assert.Single(observer.VisibleWrecks));
        Assert.Contains(observer.Updates, u => u.Command is DestinyCommand.AddBalls);
        var cooldown = Assert.IsType<PilotCallResult.NamedFailure>(Call("ship", "Jettison", [new List<object?> { cargo }]));
        Assert.Equal("ShpJettisonPending", cooldown.Name);
        var moved = Assert.Single(context.Notifications, n => n.Name == "OnItemChange");
        var previous = Assert.IsType<Dictionary<long, object?>>(moved.Arguments[1]);
        Assert.Equal(pilot.ShipId, previous[(long)InventoryColumn.Location]);
        Assert.Equal((long)InventoryFlag.GeneralMiningHold, Convert.ToInt64(previous[(long)InventoryColumn.Flag]));

        var reference = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", [can.Id, false]));
        string inventory = context.ResolveReference(reference.Id)!;
        var capacity = Assert.IsType<BlueInstance>(Call(inventory, "GetCapacity", []));
        Assert.Equal(55000d, Assert.IsType<Dictionary<string, object?>>(capacity.State)["capacity"]);
        Assert.Single(Assert.IsType<List<object?>>(Call(inventory, "List", [(long)InventoryFlag.None])));
        Assert.Null(Call(inventory, "Add", [cargo, pilot.ShipId], new() { ["qty"] = 50L, ["flag"] = 0L }));
        var contents = wrecks.Read(pilot.PilotId, can.Id, Now).Items;
        Assert.Equal(150, contents.Sum(i => i.Quantity));
        Assert.All(contents, i => Assert.Equal((int)InventoryFlag.None, i.FlagId));

        context.Flight.Updates.Clear();
        Assert.Null(
            Call(
                "@ship",
                "MultiAdd",
                [contents.Select(i => (object?)i.Id).ToList(), can.Id],
                new() { ["flag"] = (long)InventoryFlag.Cargo }
            )
        );
        Assert.Empty(context.Flight.VisibleWrecks);
        Assert.Contains(context.Flight.Updates, u => u.Command is DestinyCommand.RemoveBall remove && remove.Id == can.Id);
        observer.RefreshWrecks();
        Assert.Empty(observer.VisibleWrecks);
        Assert.Contains(observer.Updates, u => u.Command is DestinyCommand.RemoveBall remove && remove.Id == can.Id);
        using WorldDbContext world = new(db.Path);
        Assert.Null(world.Set<SavedJetcan>().Find(can.Id));
        Assert.Equal(
            300,
            world.Set<OwnedItem>().Where(i => i.LocationId == pilot.ShipId && i.FlagId == (int)InventoryFlag.Cargo).Sum(i => i.Quantity)
        );
        Assert.Equal(pilot.ShipId, world.Set<OwnedItem>().Find(fitted)!.LocationId);
    }

    [Fact]
    public void SavedContainersAndCooldownSurviveReopenAndExpiryDestroysOnlyRemainingCargo()
    {
        using TestWorld db = NewWorld();
        var pilot = db.Store.FindPilot(AccountId)!;
        var wing = db.Store.FindPilot(1000002)!;
        long first = Seed(db, pilot.ShipId, InventoryFlag.Cargo, 100);
        long second = Seed(db, pilot.ShipId, InventoryFlag.Cargo, 80);
        var jettison = db.Services.GetRequiredService<ICargoJettison>();
        var wrecks = db.Services.GetRequiredService<IWrecks>();
        Assert.Throws<InvalidDataException>(() => jettison.Jettison(pilot.PilotId, pilot.ShipId, [first], Now));
        db.Store.SetPosition(pilot.ShipId, s_position, SystemId);
        Assert.Throws<InvalidDataException>(() => jettison.Jettison(pilot.PilotId, wing.ShipId, [first], Now));
        var change = Assert.Single(jettison.Jettison(pilot.PilotId, pilot.ShipId, [first, first, long.MaxValue], Now).Changes);
        long canId = change.Item.LocationId;
        Assert.Equal(first, change.Item.Id);
        WorldInitialization.Open(db.Path);

        var can = Assert.Single(wrecks.List(SystemId, Now));
        Assert.Equal(canId, can.Id);
        db.Store.SetPosition(wing.ShipId, can.Position, SystemId);
        Assert.Single(wrecks.Read(wing.PilotId, canId, Now).Items);
        db.Store.SetPosition(pilot.ShipId, can.Position + new SpacePosition(5000, 0, 0), SystemId);
        Assert.Throws<InvalidDataException>(() => wrecks.Read(pilot.PilotId, canId, Now));
        db.Store.SetPosition(pilot.ShipId, s_position, SystemId);

        var early = jettison.Jettison(pilot.PilotId, pilot.ShipId, [second], Now + JetcanRules.Cooldown - 1);
        Assert.Equal(1, early.RetryAfter);
        Assert.Empty(early.Changes);
        var next = Assert.Single(jettison.Jettison(pilot.PilotId, pilot.ShipId, [second], Now + JetcanRules.Cooldown).Changes);
        Assert.NotEqual(canId, next.Item.LocationId);
        var split = wrecks.Take(pilot.PilotId, pilot.ShipId, canId, [first], 25, Now);
        Assert.Equal(25, split.Single(c => c.Previous is null).Item.Quantity);
        Assert.Equal(75, Assert.Single(wrecks.Read(pilot.PilotId, canId, Now).Items).Quantity);

        long expiry = Now + JetcanRules.Lifetime;
        Assert.Equal(2, wrecks.List(SystemId, expiry - 1).Count);
        Assert.Throws<InvalidDataException>(() => wrecks.Read(pilot.PilotId, canId, expiry));
        Assert.Equal(next.Item.LocationId, Assert.Single(wrecks.List(SystemId, expiry)).Id);
        using WorldDbContext saved = new(db.Path);
        Assert.Null(saved.Set<OwnedItem>().Find(first));
        Assert.Equal(25, saved.Set<OwnedItem>().Find(split.Single(c => c.Previous is null).Item.Id)!.Quantity);
        Assert.Equal(next.Item.LocationId, saved.Set<OwnedItem>().Find(second)!.LocationId);
    }

    [Fact]
    public async Task ConcurrentJettisonCannotDuplicateCargoOrCreateTwoContainers()
    {
        using TestWorld db = NewWorld(liveWorld: true);
        var pilot = db.Store.FindPilot(AccountId)!;
        long first = Seed(db, pilot.ShipId, InventoryFlag.Cargo, 100);
        long second = Seed(db, pilot.ShipId, InventoryFlag.Cargo, 200);
        long oversized = Seed(db, pilot.ShipId, InventoryFlag.Cargo, 5_500_001);
        db.Store.SetPosition(pilot.ShipId, s_position, SystemId);
        var jettison = db.Services.GetRequiredService<ICargoJettison>();
        var wrecks = db.Services.GetRequiredService<IWrecks>();

        Assert.Empty(jettison.Jettison(pilot.PilotId, pilot.ShipId, [oversized], Now).Changes);
        Assert.Empty(wrecks.List(SystemId, Now));
        var results = await Task.WhenAll(
            Task.Run(() => jettison.Jettison(pilot.PilotId, pilot.ShipId, [first], Now)),
            Task.Run(() => jettison.Jettison(pilot.PilotId, pilot.ShipId, [second], Now))
        );
        var moved = Assert.Single(results.SelectMany(r => r.Changes));
        Assert.Equal(JetcanRules.Cooldown, Assert.Single(results, r => r.RetryAfter > 0).RetryAfter);
        var can = Assert.Single(wrecks.List(SystemId, Now));
        Assert.Equal(moved.Item.Id, Assert.Single(wrecks.Read(pilot.PilotId, can.Id, Now).Items).Id);
        Assert.Throws<CargoCapacityException>(() =>
            jettison.Deposit(pilot.PilotId, can.Id, new([oversized], pilot.ShipId, (int)InventoryFlag.None, null), Now)
        );
        await LiveWorld.Find(db.Path)!.FlushAsync();
        using WorldDbContext saved = new(WorldConnections.FileConnection(db.Path));
        Assert.Equal(can.Id, saved.Set<OwnedItem>().Find(moved.Item.Id)!.LocationId);
        Assert.Equal(Now + JetcanRules.Lifetime, saved.Set<SavedJetcan>().Find(can.Id)!.ExpiresAt);
        Assert.Equal(Now + JetcanRules.Cooldown, saved.Set<SavedJettisonCooldown>().Find(pilot.PilotId)!.ReadyAt);
        Assert.Equal(pilot.ShipId, saved.Set<OwnedItem>().Find(oversized)!.LocationId);
        Assert.Equal(pilot.ShipId, saved.Set<OwnedItem>().Find(moved.Item.Id == first ? second : first)!.LocationId);
    }

    private static TestWorld NewWorld(bool liveWorld = false, TimeProvider? clock = null)
    {
        TestWorld db = new(liveWorld: liveWorld, clock: clock);
        using var catalog = new CatalogDbContext(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (4,'Material'),(25,'Asteroid');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (18,'Mineral',4),(462,'Veldspar',25);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (34,'Tritanium',18),(1230,'Veldspar',462);
            INSERT OR IGNORE INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','34','{{"volume":0.01}}'),
                ('client/types','1230','{{"volume":0.1}}');
            """
        );
        return db;
    }

    private static long Seed(TestWorld db, long shipId, InventoryFlag flag, int quantity, int typeId = WreckDefinitions.StarterLootType)
    {
        using WorldDbContext world = new(db.Path);
        long id = InventoryPersistence.NextId(world);
        world.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = db.Store.FindPilot(AccountId)!.PilotId,
                TypeId = typeId,
                LocationId = shipId,
                FlagId = (int)flag,
                Quantity = quantity,
            }
        );
        world.SaveChanges();
        return id;
    }
}
