using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Ammunition;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fittings;

public sealed class StripFittingTests
{
    private const int MerlinTypeId = 603;

    [Fact]
    public void NativeStripReturnsLoadedAmmunitionAndLeavesCargoInPlace()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        _ = f.Load();
        var pilot = f.Pilot;
        int loaded = Assert.Single(f.Ammo.Read(pilot.PilotId, pilot.ShipId)).Quantity;
        long cargo = f.Buy(215, 10);
        f.Inventory.Move(pilot.PilotId, pilot.ShipId, new([cargo], pilot.DockedLocationId, (int)InventoryFlag.Cargo, null));
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using var context = f.Context();
        string service = Bind(router, context, pilot.ShipId);

        Assert.Equal(true, router.CallWire(context, service, "StripFitting", Array.Empty<object?>(), _ => { }));
        var hangar = f.Inventory.List(pilot.PilotId, pilot.DockedLocationId);
        Assert.False(Assert.Single(hangar, item => item.Id == f.Gun).Online);
        Assert.Equal(250, hangar.Where(item => item.TypeId == 222).Sum(item => item.Quantity));
        Assert.Equal(cargo, Assert.Single(f.Inventory.List(pilot.PilotId, pilot.ShipId)).Id);
        Assert.Empty(f.Ammo.Read(pilot.PilotId, pilot.ShipId));
        Assert.Contains(context.Notifications.OfType<ItemChanged>(), notice => notice.Item.Id == f.Gun);
        Assert.Contains(context.Notifications.OfType<ItemChanged>(), notice => notice.Item.TypeId == 222 && notice.Item.Quantity == loaded);
        Assert.Contains(context.Notifications, notice => notice is ShipEffectChanged { Active: false });

        context.Notifications.Clear();
        Assert.Same(PilotCallResult.None, router.Call(context, service, "StripFitting", Array.Empty<object?>(), _ => { }));
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public async Task InactiveLocalShipStripsOnlyRemovableSlotsAndCheckpoints()
    {
        using PassiveFittingTests.Fixture f = new();
        PassiveCatalogFixture.LoadRigs(f.CatalogPath);
        long ship = CreateLocalMerlin(f);
        long module = f.Fit(3651);
        long rig = FitRig(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.LocationId = ship;
            world.Set<OwnedItem>().Find(rig)!.LocationId = ship;
            world.SaveChanges();
        }

        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            using var scope = f.Db.Services.CreateScope();
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            using var context = Context(f.Db, f.Clock);
            string service = Bind(router, context, ship);

            Assert.IsType<PilotCallResult.Failure>(
                router.Call(context, service, "StripFitting", new object?[] { context.Pilot.ShipId }, _ => { })
            );
            Assert.Empty(context.Notifications);
            Assert.Equal(true, router.CallWire(context, service, "StripFitting", Array.Empty<object?>(), _ => { }));
            Assert.Equal(rig, Assert.Single(f.Inventory.List(f.PilotId, ship)).Id);
            Assert.Equal(module, Assert.Single(context.Notifications.OfType<ItemChanged>()).Item.Id);
            Assert.Contains(context.Notifications, notice => notice is ShipEffectChanged { Active: false });
            Assert.DoesNotContain(context.Notifications, notice => notice is ModuleAttributesChanged);
            await live.FlushAsync();
        }

        using LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { });
        Assert.Equal(rig, Assert.Single(f.Inventory.List(f.PilotId, ship)).Id);
        Assert.Equal(f.StationId, Assert.Single(f.Inventory.List(f.PilotId, f.StationId), item => item.Id == module).LocationId);
    }

    [Fact]
    public async Task NativeDestroyFittingUsesTheBoundLocalInactiveShip()
    {
        using PassiveFittingTests.Fixture f = new();
        PassiveCatalogFixture.LoadRigs(f.CatalogPath);
        long ship = CreateLocalMerlin(f);
        long fittedRig = FitRig(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(fittedRig)!.LocationId = ship;
            world.SaveChanges();
        }
        long activeRig = FitRig(f);

        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            using var scope = f.Db.Services.CreateScope();
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            using var context = Context(f.Db, f.Clock);
            string service = Bind(router, context, ship);

            Assert.Same(PilotCallResult.None, router.Call(context, service, "DestroyFitting", new object?[] { fittedRig }, _ => { }));
            var removed = Assert.Single(context.Notifications.OfType<ItemChanged>());
            Assert.Equal(fittedRig, removed.Item.Id);
            Assert.Equal((long)InventoryLocation.Graveyard, removed.Item.LocationId);
            Assert.DoesNotContain(context.Notifications, notice => notice is ModuleAttributesChanged);
            Assert.Empty(f.Inventory.List(f.PilotId, ship));
            context.Notifications.Clear();
            Assert.IsType<PilotCallResult.Failure>(router.Call(context, service, "DestroyFitting", new object?[] { activeRig }, _ => { }));
            Assert.Empty(context.Notifications);
            Assert.Equal(activeRig, Assert.Single(f.Inventory.List(f.PilotId, context.Pilot.ShipId)).Id);
            await live.FlushAsync();
        }

        using LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { });
        Assert.Empty(f.Inventory.List(f.PilotId, ship));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeUnfitAndRigDestructionRequireAvailableUpwellFittingService(bool destroyRig)
    {
        const int astrahusTypeId = 35832;
        const long structure = StructureRules.FirstStructureId + 100;
        using PassiveFittingTests.Fixture f = new();
        PassiveCatalogFixture.LoadRigs(f.CatalogPath);
        PassiveCatalogFixture.LoadStructures(f.CatalogPath);
        long ship = f.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(ship)!.TypeId = MerlinTypeId;
            world.SaveChanges();
        }
        long item = destroyRig ? FitRig(f) : f.Fit(3651);
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var hull = world.Ships.Find(ship)!;
            world.Add(
                new SavedStructure
                {
                    Id = structure,
                    TypeId = astrahusTypeId,
                    CorporationId = world.Pilots.Find(f.PilotId)!.CorporationId,
                    SystemId = catalog.FindStation(hull.StationId)!.SolarSystemId,
                    State = StructureState.ShieldVulnerable,
                    DeployedAt = f.Clock.Now,
                    StateStartedAt = f.Clock.Now,
                }
            );
            hull.StructureId = structure;
            world.SaveChanges();
        }
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using var context = Context(f.Db, f.Clock);
        string service = Bind(router, context, ship);
        var fitted = Assert.Single(f.Inventory.List(f.PilotId, ship));

        var failure = Assert.IsType<PilotCallResult.Failure>(Call());
        Assert.Contains("Ship fitting requires a quantum core", failure.Message);
        Assert.Equal(fitted, Assert.Single(f.Inventory.List(f.PilotId, ship)));
        Assert.Empty(context.Notifications);

        using (var update = live.Economy.Begin())
        {
            update.Set<SavedStructure>().Find(structure)!.HasCore = true;
            update.Commit();
        }
        Assert.Same(PilotCallResult.None, Call());
        var change = Assert.Single(context.Notifications.OfType<ItemChanged>());
        Assert.Equal(item, change.Item.Id);
        Assert.Equal(destroyRig ? (long)InventoryLocation.Graveyard : structure, change.Item.LocationId);

        PilotCallResult Call() =>
            destroyRig
                ? router.Call(context, service, "DestroyFitting", new object?[] { item }, _ => { })
                : router.Call(context, "@hangar", "Add", new object?[] { item, ship }, _ => { });
    }

    [Fact]
    public void StripRejectsCargoOverflowWithoutMovingAnything()
    {
        using PassiveFittingTests.Fixture f = new();
        _ = f.Fit(1317);
        _ = f.Fit(3651);
        _ = f.Grant(3651, quantity: 28, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using var context = Context(f.Db, f.Clock);
        string service = Bind(router, context, f.ShipId);
        var ship = f.Inventory.List(f.PilotId, f.ShipId);
        var hangar = f.Inventory.List(f.PilotId, f.StationId);

        var failure = Assert.IsType<PilotCallResult.NamedFailure>(
            router.Call(context, service, "StripFitting", Array.Empty<object?>(), _ => { })
        );
        Assert.Equal("NotEnoughCargoSpace", failure.Name);
        Assert.Equal(ship, f.Inventory.List(f.PilotId, f.ShipId));
        Assert.Equal(hangar, f.Inventory.List(f.PilotId, f.StationId));
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void ProtectedEquipmentBlocksTheEntireStrip()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        var pilot = f.Pilot;
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            const string restricted = """{"includedTypeIDs":[561]}""";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/typelist','338',{restricted})");
        }
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using var context = f.Context();
        string service = Bind(router, context, pilot.ShipId);

        Assert.IsType<PilotCallResult.Failure>(router.Call(context, service, "StripFitting", Array.Empty<object?>(), _ => { }));
        Assert.Equal(f.Gun, Assert.Single(f.Inventory.List(pilot.PilotId, pilot.ShipId)).Id);
        Assert.Empty(context.Notifications);
    }

    private static PilotCallContext Context(TestWorld db, TestClock clock)
    {
        var pilot = db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, clock, new(), db.Objects),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    private static long CreateLocalMerlin(PassiveFittingTests.Fixture f)
    {
        long activeId = f.ShipId;
        using WorldDbContext world = new(f.Db.Path);
        var active = world.Ships.Find(activeId)!;
        active.TypeId = MerlinTypeId;
        long ship = InventoryPersistence.NextId(world);
        world.Add(
            new Ship
            {
                Id = ship,
                OwnerId = f.PilotId,
                StationId = active.StationId,
                TypeId = MerlinTypeId,
                Name = "Reserve Merlin",
            }
        );
        world.SaveChanges();
        return ship;
    }

    private static long FitRig(PassiveFittingTests.Fixture f)
    {
        const int rigType = 31788;
        long rig = f.Grant(rigType);
        f.Fitting.Fit(f.PilotId, [rig], f.StationId, (int)InventoryFlag.RigSlot0);
        return rig;
    }

    private static string Bind(PilotCallRouter router, PilotCallContext context, long ship)
    {
        var reference = Assert.IsType<BlueObjectReference>(
            router.CallWire(context, "invbroker", "GetInventoryFromId", new object?[] { ship }, _ => { })
        );
        return context.ResolveReference(reference.Id)!;
    }
}
