using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Ammunition;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fittings;

public sealed class ApplyFittingTests
{
    private const int Miner = InventoryDefinitions.CivilianMinerType;
    private const int Railgun = 561;
    private const int Antimatter = 222;
    private const int Iron = 215;
    private const int Drone = 2454;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppliesNativeSimulationFitAndReportsShortages(bool liveWorld)
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld);
        using var scope = fixture.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using var context = fixture.Context();
        var pilot = context.Pilot;
        _ = fixture.Load(Iron);
        var miner = Assert.Single(fixture.Inventory.List(pilot.PilotId, pilot.StationId), item => item.TypeId == Miner);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Set<OwnedItem>().Find(miner.Id)!.Quantity = 3;
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemCategory VALUES (18,'Drone');
                INSERT INTO ItemGroup VALUES (100,'Combat Drone',18);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (2454,'Hobgoblin I',100);
                INSERT INTO SourceRecord VALUES ('client/types','2454','{{"volume":5.0}}');
                INSERT INTO DogmaAttribute(Id,Name) VALUES (283,'droneCapacity');
                INSERT INTO TypeAttribute VALUES (601,283,5);
                """
            );
        }
        long drone = Grant(fixture, Drone, 1);
        long ammo = fixture.Buy(Antimatter, 30);
        var service = Bind(router, context);
        var args = Request(
            pilot,
            new()
            {
                [Miner] = [miner.Id],
                [Antimatter] = [ammo],
                [Drone] = [drone],
            },
            new() { [(long)InventoryFlag.HighSlot0] = (long)Miner, [(long)InventoryFlag.HighSlot1] = (long)Miner },
            charges: new() { [Antimatter] = 50L },
            drones: new() { [Drone] = 1L }
        );
        var reply = Assert.IsType<List<object?>>(FitReply(router, context, service, args));
        Assert.Equal(new object?[] { (long)Antimatter, 20L }, Assert.IsType<object?[]>(Assert.Single(reply)));

        var items = fixture.Inventory.List(pilot.PilotId, pilot.ShipId);
        var modules = items.Where(item => item.TypeId == Miner).OrderBy(item => item.FlagId).ToArray();
        Assert.Equal(new[] { (int)InventoryFlag.HighSlot0, (int)InventoryFlag.HighSlot1 }, modules.Select(item => item.FlagId));
        Assert.All(modules, item => Assert.True(item.Online && item.Singleton));
        Assert.Equal((int)InventoryFlag.DroneBay, Assert.Single(items, item => item.TypeId == Drone).FlagId);
        Assert.Equal(30, Assert.Single(items, item => item.TypeId == Antimatter).Quantity);
        var hangar = fixture.Inventory.List(pilot.PilotId, pilot.StationId);
        Assert.Equal(1, Assert.Single(hangar, item => item.Id == miner.Id).Quantity);
        Assert.Equal((int)InventoryFlag.Hangar, Assert.Single(hangar, item => item.Id == fixture.Gun).FlagId);
        Assert.Equal(250, hangar.Where(item => item.TypeId == Iron).Sum(item => item.Quantity));
        var changes = context.Notifications.OfType<ItemChanged>().ToArray();
        Assert.Equal(changes.Length, changes.Select(change => change.Item.Id).Distinct().Count());
        Assert.Equal(3, Assert.Single(changes, change => change.Item.Id == miner.Id).Previous[InventoryColumn.Quantity]);
        Assert.All(modules, module => Assert.Contains(changes, change => change.Item.Id == module.Id));
        Assert.Contains(context.Notifications, notification => notification is ModuleAttributesChanged);
        Assert.Empty(fixture.Ammo.Read(pilot.PilotId, pilot.ShipId));

        context.Notifications.Clear();
        Assert.Equal(
            new object?[] { (long)Antimatter, 20L },
            Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(FitReply(router, context, service, args))))
        );
        Assert.Empty(context.Notifications);
        Assert.Null(CallWire(router, context, "invbroker", "SetLabel", [pilot.ShipId, "Simulation fit"]));
        Assert.Equal("Simulation fit", context.Pilot.ShipName);
        Assert.Single(context.Notifications.OfType<ShipLabelChanged>());

        scope.Dispose();
        fixture.Db.Services.Dispose();
        _ = WorldInitialization.Open(fixture.Db.Path);
        using WorldDbContext reopened = new(fixture.Db.Path);
        Assert.Equal("Simulation fit", reopened.Ships.Find(pilot.ShipId)!.Name);
        Assert.Equal(2, reopened.Set<OwnedItem>().Count(item => item.LocationId == pilot.ShipId && item.Online && item.TypeId == Miner));
        Assert.Equal(30, reopened.Set<OwnedItem>().Find(ammo)!.Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsWrongHullAndRollsBackLaterCapacityFailure(bool liveWorld)
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld);
        using var scope = fixture.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using var context = fixture.Context();
        var pilot = context.Pilot;
        var service = Bind(router, context);
        var miner = Assert.Single(fixture.Inventory.List(pilot.PilotId, pilot.StationId), item => item.TypeId == Miner);
        long foreign = Assert.Single(fixture.Inventory.List(90000002, pilot.StationId), item => item.TypeId == Miner).Id;
        var slots = new Dictionary<long, object?> { [(long)InventoryFlag.HighSlot0] = (long)Miner };
        var initialShip = fixture.Inventory.List(pilot.PilotId, pilot.ShipId);
        var initialHangar = fixture.Inventory.List(pilot.PilotId, pilot.StationId);

        var foreignArgs = Request(pilot, new() { [Miner] = [foreign] }, slots);
        var missing = Assert.IsType<List<object?>>(FitReply(router, context, service, foreignArgs));
        Assert.Equal(new object?[] { (long)Miner, 1L }, Assert.IsType<object?[]>(Assert.Single(missing)));

        var wrongHull = Request(pilot, new() { [Miner] = [miner.Id] }, slots);
        wrongHull[1] = (long)pilot.ShipTypeId + 1;
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, service, "FitFitting", wrongHull, _ => { }));
        Assert.Equal(initialShip, fixture.Inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal(initialHangar, fixture.Inventory.List(pilot.PilotId, pilot.StationId));
        Assert.Empty(context.Notifications);

        long ammo = Grant(fixture, Antimatter, 1, volume: 10000);
        var invalid = Request(pilot, new() { [Miner] = [miner.Id], [Antimatter] = [ammo] }, slots, charges: new() { [Antimatter] = 1L });
        var failure = Assert.IsType<PilotCallResult.NamedFailure>(router.Call(context, service, "FitFitting", invalid, _ => { }));
        Assert.Equal("NotEnoughCargoSpace", failure.Name);
        Assert.Equal(initialShip, fixture.Inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal(initialHangar, fixture.Inventory.List(pilot.PilotId, pilot.StationId).Where(item => item.Id != ammo));
        Assert.Equal(1, Assert.Single(fixture.Inventory.List(pilot.PilotId, pilot.StationId), item => item.Id == ammo).Quantity);
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void AcceptsTheSimulationAmmoSetAndContinuesWithTheNativeLoadCall()
    {
        using AmmunitionTests.Fixture fixture = new();
        using var scope = fixture.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using var context = fixture.Context();
        var pilot = context.Pilot;
        long iron = fixture.Buy(Iron, 250);
        var clock = fixture.Db.Services.GetRequiredService<TimeProvider>();
        long reloadTime = TimeSpan.FromMilliseconds(fixture.Fitting.Attributes(Railgun)[DogmaAttributeId.ReloadTime]).Ticks;
        fixture.Ammo.Load(
            pilot.PilotId,
            pilot.ShipId,
            [fixture.Gun],
            [iron],
            pilot.StationId,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() - reloadTime
        );
        var destination = new object?[] { pilot.DockedLocationId, pilot.PilotId, (long)InventoryFlag.Hangar };
        Assert.Null(CallWire(router, context, "dogmaIM", "UnloadAmmo", [pilot.ShipId, ItemSet(fixture.Gun), destination]));
        Assert.Empty(fixture.Ammo.Read(pilot.PilotId, pilot.ShipId));
        Assert.Null(CallWire(router, context, "dogmaIM", "UnloadAmmo", [pilot.ShipId, ItemSet(), destination]));
        long ammo = fixture.Buy(Antimatter, 200);
        Assert.Null(
            CallWire(
                router,
                context,
                "dogmaIM",
                "LoadAmmo",
                [pilot.ShipId, fixture.Gun, new List<object?> { ammo }, pilot.DockedLocationId]
            )
        );
        var magazine = Assert.Single(fixture.Ammo.Read(pilot.PilotId, pilot.ShipId));
        Assert.Equal(Antimatter, magazine.TypeId);
        Assert.Equal(200, magazine.Quantity);
    }

    [Fact]
    public void AppliesConfirmedRigsAndPreservesAlreadyFittedRigs()
    {
        const int extender = 31788;
        const int otherExtender = 31794;
        using PassiveFittingTests.Fixture fixture = new();
        PassiveCatalogFixture.LoadRigs(fixture.CatalogPath);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Ships.Find(fixture.ShipId)!.TypeId = 603; // Merlin
            world.SaveChanges();
        }
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using var scope = fixture.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(fixture.Db.Store, p, fixture.Clock, new(), fixture.Db.Objects),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var service = Bind(router, context);
        long stack = fixture.Grant(extender, 2);
        var slots = new Dictionary<long, object?> { [(long)InventoryFlag.RigSlot0] = (long)extender };
        var args = Request(pilot, new() { [extender] = [stack] }, slots);
        Assert.Empty(Assert.IsType<List<object?>>(FitReply(router, context, service, args)));
        Assert.Empty(fixture.Inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Empty(context.Notifications);

        args[6] = true;
        Assert.Empty(Assert.IsType<List<object?>>(FitReply(router, context, service, args)));
        var rig = Assert.Single(fixture.Inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal(extender, rig.TypeId);
        Assert.Equal((int)InventoryFlag.RigSlot0, rig.FlagId);
        Assert.False(rig.Online);

        context.Notifications.Clear();
        long replacement = fixture.Grant(otherExtender);
        slots[(long)InventoryFlag.RigSlot0] = (long)otherExtender;
        var replace = Request(pilot, new() { [otherExtender] = [replacement] }, slots);
        replace[6] = true;
        Assert.Empty(Assert.IsType<List<object?>>(FitReply(router, context, service, replace)));
        Assert.Equal(rig, Assert.Single(fixture.Inventory.List(pilot.PilotId, pilot.ShipId)));
        Assert.Empty(context.Notifications);
    }

    private static long Grant(AmmunitionTests.Fixture fixture, int type, int quantity, double? volume = null)
    {
        using WorldDbContext world = new(fixture.Db.Path);
        var pilot = fixture.Pilot;
        using var transaction = world.Database.BeginTransaction();
        var item = world
            .Add(
                new OwnedItem
                {
                    OwnerId = pilot.PilotId,
                    TypeId = type,
                    LocationId = pilot.DockedLocationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = quantity,
                    VolumeOverride = volume,
                }
            )
            .Entity;
        world.SaveChanges();
        transaction.Commit();
        return item.Id;
    }

    private static string Bind(PilotCallRouter router, PilotCallContext context) =>
        context.ResolveReference(
            Assert.IsType<BlueObjectReference>(CallWire(router, context, "invbroker", "GetInventoryFromId", [context.Pilot.ShipId])).Id
        )!;

    private static object?[] Request(
        DockedPilot pilot,
        Dictionary<long, long[]> items,
        Dictionary<long, object?> slots,
        Dictionary<long, object?>? charges = null,
        Dictionary<long, object?>? drones = null
    )
    {
        var plan = new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["modulesByFlag"] = slots,
                ["chargesByType"] = charges ?? [],
                ["iceByType"] = new Dictionary<long, object?>(),
                ["implantsByTypeID"] = new Dictionary<long, object?>(),
                ["dronesByType"] = drones ?? [],
                ["fightersByTypeID"] = new Dictionary<long, object?>(),
            }
        );

        // Carbon REDUCE sends the defaultdict factory, an empty list iterator,
        // then key/value pairs. Each value is itself a Python set reduction.
        var dictionary = BlueMarshal.Encode(new BlueConstructor("collections.defaultdict", [new BlueGlobal("__builtin__.set")]));
        List<byte> request = [126, 0, 0, 0, 0, 20, 7];
        request.AddRange(Value(pilot.ShipId));
        request.AddRange(Value((long)pilot.ShipTypeId));
        request.AddRange(dictionary[5..^1]);
        foreach (var (type, ids) in items)
        {
            request.AddRange(Value(type));
            request.AddRange(Value(new BlueConstructor("__builtin__.set", [ids.Cast<object?>().ToList()])));
        }
        request.Add(45);
        request.AddRange(Value(pilot.DockedLocationId));
        request.AddRange(Value(plan));
        request.AddRange(Value(new Dictionary<long, object?>()));
        request.AddRange(Value(false));
        return Assert.IsType<object?[]>(BlueMarshal.Decode(request.ToArray()));
    }

    private static HashSet<long> ItemSet(params long[] ids) =>
        Assert.IsType<HashSet<long>>(
            BlueMarshal.Decode(BlueMarshal.Encode(new BlueConstructor("__builtin__.set", [ids.Cast<object?>().ToList()])))
        );

    private static byte[] Value(object? value) => BlueMarshal.Encode(value)[5..];

    private static object? FitReply(PilotCallRouter router, PilotCallContext context, string service, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(CallWire(router, context, service, "FitFitting", args)));

    private static object? CallWire(PilotCallRouter router, PilotCallContext context, string service, string method, object?[] args) =>
        router.CallWire(context, service, method, args, _ => { });
}
