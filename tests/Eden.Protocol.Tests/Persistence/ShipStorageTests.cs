using System.Text.Json;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ShipStorageTests
{
    private sealed class FixedLootRoll(bool survives) : IShipLootRoll
    {
        public bool Survives() => survives;
    }

    private sealed record StoredFixture(Fixture F, long Carrier, long Child, long? Module, InventoryFlag Flag) : IDisposable
    {
        public void Dispose() => F.Dispose();
    }

    private static StoredFixture Create(int carrierType, HullClass hullClass, InventoryFlag flag, int childType = 587)
    {
        var f = HullHoldTests.Prepare(carrierType, hullClass);
        PassiveCatalogFixture.LoadUtilityHulls(f.CatalogPath);
        SmallHullTests.TrainCatalog(f);
        long carrier = f.ShipId;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        long child = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant(childType)], "Stored trial").ShipIds);
        fleet.Board(f.PilotId, child, carrier);
        long? module = null;
        if (childType == 587)
        {
            module = f.Fit(561);
            long ammunition = f.Grant(215, 250, cargo: true);
            f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, child, [module.Value], [ammunition], child, f.Clock.Now);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = child,
                    ShieldCharge = 10,
                    ArmorDamage = 2,
                    StructureDamage = 3,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        fleet.Board(f.PilotId, carrier, child);
        f.Inventory.Move(f.PilotId, [child], f.StationId, carrier, (int)flag, null);
        return new(f, carrier, child, module, flag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDronePanelReadsOnlyTheActiveShipsStoredDroneLayerState(bool bound)
    {
        using var f = HullHoldTests.Prepare(28606, HullClass.IndustrialCommand);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES({0},{1},{2})",
                2488,
                (int)DogmaAttributeId.ShieldRechargeTime,
                125000d
            );
        }
        long drone = f.Grant(2488, 5);
        f.Inventory.Move(f.PilotId, [drone], f.StationId, f.ShipId, (int)InventoryFlag.DroneBay, null);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        object?[] args = [new HashSet<long> { drone }];
        object? reply = bound
            ? router.CallWire(
                context,
                "dogmaIM",
                "MachoBindObject",
                new object?[] { pilot.SystemId, new object?[] { "GetLayerDamageValuesByItems", args } },
                _ => { }
            )
            : router.CallWire(context, "dogmaIM", "GetLayerDamageValuesByItems", args, _ => { });
        if (bound)
        {
            reply = Assert.IsType<object?[]>(reply)[1];
        }
        var records = Assert.IsType<Dictionary<long, object?>>(reply);
        var state = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(Assert.Single(records).Value).State);
        Assert.Equal(new object?[] { 1d, 25000d, f.Clock.Now }, Assert.IsType<object?[]>(state["shieldInfo"]));
        Assert.Equal(0d, state["armorDamage"]);
        Assert.Equal(0d, state["hullDamage"]);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(
                context,
                "dogmaIM",
                "GetLayerDamageValuesByItems",
                new object?[]
                {
                    new HashSet<long> { drone, f.ShipId },
                },
                _ => { }
            )
        );
        f.Inventory.Move(f.PilotId, [drone], f.ShipId, f.StationId, (int)InventoryFlag.Hangar, null);
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "dogmaIM", "GetLayerDamageValuesByItems", args, _ => { }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NativeDronePanelInitializesPreferencesWithoutEnablingDroneOperations(bool bound, bool floatingDefaults)
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new SharedSpace(), db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        object?[] preferences =
        [
            new Dictionary<long, object?>
            {
                [(long)DogmaAttributeId.DroneIsAggressive] = floatingDefaults ? 1d : (object)false,
                [(long)DogmaAttributeId.DroneFocusFire] = floatingDefaults ? 0d : (object)1L,
            },
        ];
        object? reply = bound
            ? router.CallWire(
                context,
                "dogmaIM",
                "MachoBindObject",
                new object?[] { pilot.SystemId, new object?[] { "ChangeDroneSettings", preferences } },
                _ => { }
            )
            : router.CallWire(context, "dogmaIM", "ChangeDroneSettings", preferences, _ => { });
        if (bound)
        {
            var tuple = Assert.IsType<object?[]>(reply);
            Assert.IsType<BlueObjectReference>(tuple[0]);
            Assert.Null(tuple[1]);
            Assert.NotEmpty(BlueMarshal.Encode(reply));
        }
        else
        {
            Assert.Null(reply);
        }
        foreach (
            var invalid in new object?[][]
            {
                [],
                [new Dictionary<long, object?> { [(long)DogmaAttributeId.DroneCapacity] = true }],
                [new Dictionary<long, object?> { [(long)DogmaAttributeId.DroneFocusFire] = 2L }],
                [new Dictionary<long, object?> { [(long)DogmaAttributeId.DroneFocusFire] = 0.5d }],
                [new Dictionary<long, object?> { [(long)DogmaAttributeId.DroneFocusFire] = double.NaN }],
                [new Dictionary<long, object?> { [(long)DogmaAttributeId.DroneIsAggressive] = "true" }],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(router.Call(context, "dogmaIM", "ChangeDroneSettings", invalid, _ => { }));
        }
        Assert.Equal(
            PilotCallResult.Unsupported,
            router.Call(context, "dogmaIM", "LaunchDrones", new object?[] { pilot.ShipId }, _ => { })
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCarriedShipMenusCanReadZeroInsuranceQuotesWhenTheCatalogHasNoValuation(bool bound)
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new SharedSpace(), db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        object? reply = bound
            ? router.CallWire(
                context,
                "insuranceSvc",
                "MachoBindObject",
                new object?[]
                {
                    new object?[] { (long)pilot.StationId, (long)pilot.StationId, 15L },
                    new object?[] { "GetInsurancePrice", new object?[] { 587L } },
                },
                _ => { }
            )
            : router.CallWire(context, "insuranceSvc", "GetInsurancePrice", new object?[] { 587L }, _ => { });
        if (bound)
        {
            var tuple = Assert.IsType<object?[]>(reply);
            Assert.IsType<BlueObjectReference>(tuple[0]);
            Assert.Equal(0d, Assert.IsType<double>(tuple[1]));
            Assert.NotEmpty(BlueMarshal.Encode(reply));
        }
        else
        {
            Assert.Equal(0d, Assert.IsType<double>(reply));
        }
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "insuranceSvc", "InsureShip", new object?[] { pilot.ShipId }, _ => { })
        );
        Assert.Empty(context.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeHoldAccessButtonsReadClosedSharingAndCannotGrantForeignFleetOrCorpAccess(bool inSpace)
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        if (inSpace)
        {
            db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
            pilot = db.Store.FindPilot(1000001)!;
        }
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new SharedSpace(), db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var flags = Assert.IsType<Dictionary<string, object?>>(
            router.CallWire(context, "ship", "GetShipConfiguration", new object?[] { pilot.ShipId }, _ => { })
        );
        Assert.Equal(
            new[] { "FleetHangar_AllowCorpAccess", "FleetHangar_AllowFleetAccess", "SMB_AllowCorpAccess", "SMB_AllowFleetAccess" },
            flags.Keys.Order()
        );
        Assert.All(flags.Values, value => Assert.False(Assert.IsType<bool>(value)));
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "ship", "GetShipConfiguration", new object?[] { db.Store.FindPilot(1000002)!.ShipId }, _ => { })
        );
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "ship", "GetShipConfiguration", Array.Empty<object?>(), _ => { }));
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(
                context,
                "ship",
                "ConfigureShip",
                new object?[] { new Dictionary<string, object?> { ["FleetHangar_AllowFleetAccess"] = true } },
                _ => { }
            )
        );
        var after = Assert.IsType<Dictionary<string, object?>>(
            router.CallWire(context, "ship", "GetShipConfiguration", new object?[] { pilot.ShipId }, _ => { })
        );
        Assert.All(after.Values, value => Assert.False(Assert.IsType<bool>(value)));
    }

    [Theory]
    [InlineData(638, HullClass.Battleship, InventoryFlag.FrigateEscapeBay, 587)]
    [InlineData(28606, HullClass.IndustrialCommand, InventoryFlag.ShipMaintenanceBay, 587)]
    [InlineData(28606, HullClass.IndustrialCommand, InventoryFlag.FleetHangar, 587)]
    [InlineData(34328, HullClass.Freighter, InventoryFlag.ShipMaintenanceBay, 587)]
    [InlineData(89607, HullClass.ExpeditionCommand, InventoryFlag.ShipMaintenanceBay, 587)]
    [InlineData(33472, HullClass.Battleship, InventoryFlag.ShipMaintenanceBay, 33513)]
    public void StoredShipRetainsItsIdentityFitMagazineDamageAndNameAcrossHaulingAndRestart(
        int carrierType,
        HullClass hullClass,
        InventoryFlag flag,
        int childType
    )
    {
        using var stored = Create(carrierType, hullClass, flag, childType);
        var f = stored.F;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        var items = f.Inventory.List(f.PilotId, stored.Child);
        var magazine = stored.Module is long module ? f.Inventory.List(f.PilotId, module) : [];
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(stored.Carrier, world.Ships.Find(stored.Child)!.StoredInId);
        }
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(items, f.Inventory.List(f.PilotId, stored.Child));
        Assert.Throws<InvalidDataException>(() => fleet.Assemble(f.PilotId, [stored.Child], null));
        Assert.Throws<InvalidDataException>(() => fleet.Board(f.PilotId, stored.Child, stored.Carrier));
        long home = f.StationId;
        const int destinationId = 60003761;
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z) "
                    + "SELECT 60003761,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,"
                    + "X+1000000,Y,Z FROM Station WHERE Id=60003760"
            );
            var destination = catalog.Set<Station>().Single(s => s.Id == destinationId);
            f.Db.Store.SetPosition(stored.Carrier, new(destination.X, destination.Y, destination.Z), destination.SolarSystemId);
        }
        f.Db.Store.Dock(f.PilotId, stored.Carrier, destinationId);
        Assert.DoesNotContain(fleet.Ships(f.PilotId, checked((int)home)), item => item.Id == stored.Child);
        f.Inventory.Move(f.PilotId, [stored.Child], stored.Carrier, destinationId, (int)InventoryFlag.Hangar, null);
        fleet.Board(f.PilotId, stored.Child, stored.Carrier);
        Assert.Equal(destinationId, f.StationId);
        Assert.Equal("Stored trial", fleet.Name(f.PilotId, stored.Child));
        Assert.Equal(items, f.Inventory.List(f.PilotId, stored.Child));
        if (stored.Module is long savedModule)
        {
            Assert.Equal(magazine, f.Inventory.List(f.PilotId, savedModule));
        }
        using WorldDbContext saved = new(f.Db.Path);
        var health = saved.Set<ShipHealthState>().Find(stored.Child)!;
        Assert.Equal(2, health.ArmorDamage);
        Assert.Equal(3, health.StructureDamage);
        Assert.Null(saved.Ships.Find(stored.Child)!.StoredInId);
    }

    [Theory]
    [InlineData(InventoryFlag.ShipMaintenanceBay)]
    [InlineData(InventoryFlag.FleetHangar)]
    public void StockBindingsReadCarriedContentsButCannotWriteOrKeepAccessAfterTheCarrierMovesAway(InventoryFlag flag)
    {
        using var stored = Create(28606, HullClass.IndustrialCommand, flag);
        var f = stored.F;
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        LocationTable Names() =>
            Assert.IsType<LocationTable>(
                Assert
                    .IsType<PilotCallResult.Success>(
                        router.Call(
                            context,
                            "config",
                            "GetMultiLocationsEx",
                            new object?[] { new List<object?> { stored.Child } },
                            _ => { }
                        )
                    )
                    .Reply
            );
        Assert.Contains(Names().Locations, location => location.Id == stored.Child && location.Name == "Stored trial");
        var reference = Assert.IsType<BlueObjectReference>(
            router.CallWire(context, "invbroker", "GetInventoryFromId", new object?[] { stored.Child, 1L }, _ => { })
        );
        string container = context.ResolveReference(reference.Id)!;
        Assert.IsType<BlueConstructor>(router.CallWire(context, container, "GetSelfInvItem", Array.Empty<object?>(), _ => { }));
        Assert.NotEmpty(Assert.IsType<List<object?>>(router.CallWire(context, container, "List", Array.Empty<object?>(), _ => { })));
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, container, "Add", new object?[] { f.Grant(215), f.StationId }, _ => { })
        );
        long foreign = f.Db.Store.FindPilot(1000002)!.ShipId;
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, $"@ship:{foreign}", "List", Array.Empty<object?>(), _ => { }));
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        context.ChangeShip(fleet.Board(f.PilotId, 1000000000001, stored.Carrier).Pilot);
        Assert.Contains(Names().Locations, location => location.Id == stored.Child && location.Name == "Stored trial");
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z) "
                    + "SELECT 60003761,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X+1000000,Y,Z FROM Station WHERE Id=60003760"
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(stored.Carrier)!.StationId = 60003761;
            world.SaveChanges();
        }
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, container, "List", Array.Empty<object?>(), _ => { }));
        Assert.Empty(Names().Locations);
    }

    [Fact]
    public void InvalidMixedBatchCapacityAndDuplicateRequestsNeverMoveAStoredShip()
    {
        using var stored = Create(33472, HullClass.Battleship, InventoryFlag.ShipMaintenanceBay, 33513);
        var f = stored.F;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        long second = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant(33513)], "Second shuttle").ShipIds);
        Assert.Throws<CargoCapacityException>(() =>
            f.Inventory.Move(f.PilotId, [second], f.StationId, stored.Carrier, (int)InventoryFlag.ShipMaintenanceBay, null)
        );
        f.Inventory.Move(f.PilotId, [stored.Child], stored.Carrier, f.StationId, (int)InventoryFlag.Hangar, null);
        long module = f.Grant(3651);
        Assert.Throws<InvalidDataException>(() =>
            f.Inventory.Move(f.PilotId, [stored.Child, module], f.StationId, stored.Carrier, (int)InventoryFlag.ShipMaintenanceBay, null)
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Inventory.Move(
                f.PilotId,
                [stored.Child, stored.Child],
                f.StationId,
                stored.Carrier,
                (int)InventoryFlag.ShipMaintenanceBay,
                null
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Inventory.Move(f.PilotId, [stored.Child], f.StationId, stored.Carrier, (int)InventoryFlag.Cargo, null)
        );
        Assert.Equal(f.StationId, fleet.Find(f.PilotId, stored.Child)!.LocationId);
        Assert.Equal(f.StationId, Assert.Single(f.Inventory.List(f.PilotId, f.StationId), item => item.Id == module).LocationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryCargoAndNestedShipsMustBeUnloadedBeforeStorage(bool nested)
    {
        int childType = nested ? 89607 : 587;
        using var stored = Create(34328, HullClass.Freighter, InventoryFlag.ShipMaintenanceBay, childType);
        var f = stored.F;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        f.Inventory.Move(f.PilotId, [stored.Child], stored.Carrier, f.StationId, (int)InventoryFlag.Hangar, null);
        fleet.Board(f.PilotId, stored.Child, stored.Carrier);
        if (nested)
        {
            long shuttle = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant(33513)], "Nested shuttle").ShipIds);
            f.Inventory.Move(f.PilotId, [shuttle], f.StationId, stored.Child, (int)InventoryFlag.ShipMaintenanceBay, null);
        }
        else
        {
            f.Grant(34, cargo: true);
        }
        var contents = f.Inventory.List(f.PilotId, stored.Child);
        fleet.Board(f.PilotId, stored.Carrier, stored.Child);
        Assert.Throws<InvalidDataException>(() =>
            f.Inventory.Move(f.PilotId, [stored.Child], f.StationId, stored.Carrier, (int)InventoryFlag.ShipMaintenanceBay, null)
        );
        Assert.Equal(contents, f.Inventory.List(f.PilotId, stored.Child));
        Assert.Equal(f.StationId, fleet.Find(f.PilotId, stored.Child)!.LocationId);
        _ = WorldInitialization.Open(f.Db.Path);
    }

    [Theory]
    [InlineData(638, HullClass.Battleship, InventoryFlag.FrigateEscapeBay)]
    [InlineData(28606, HullClass.IndustrialCommand, InventoryFlag.ShipMaintenanceBay)]
    [InlineData(28606, HullClass.IndustrialCommand, InventoryFlag.FleetHangar)]
    public void StoredShipsRetainTheCatalogConsumablesAndSubsystemsInTheirCargo(int carrier, HullClass hullClass, InventoryFlag flag)
    {
        using var stored = Create(carrier, hullClass, flag);
        var f = stored.F;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        f.Inventory.Move(f.PilotId, [stored.Child], stored.Carrier, f.StationId, (int)InventoryFlag.Hangar, null);
        fleet.Board(f.PilotId, stored.Child, stored.Carrier);
        foreach (int type in new[] { 3898, 16273, 16275, 28668, 45586 })
        {
            f.Grant(type, cargo: true);
        }
        var contents = f.Inventory.List(f.PilotId, stored.Child);
        fleet.Board(f.PilotId, stored.Carrier, stored.Child);
        f.Inventory.Move(f.PilotId, [stored.Child], f.StationId, stored.Carrier, (int)flag, null);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(contents, f.Inventory.List(f.PilotId, stored.Child));
        f.Inventory.Move(f.PilotId, [stored.Child], stored.Carrier, f.StationId, (int)InventoryFlag.Hangar, null);
        fleet.Board(f.PilotId, stored.Child, stored.Carrier);
        Assert.Equal(contents, f.Inventory.List(f.PilotId, stored.Child));
    }

    [Fact]
    public void StoredHaulerRetainsAmmunitionInItsSpecializedHold()
    {
        using var stored = Create(34328, HullClass.Freighter, InventoryFlag.ShipMaintenanceBay, 651);
        var f = stored.F;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        f.Inventory.Move(f.PilotId, [stored.Child], stored.Carrier, f.StationId, (int)InventoryFlag.Hangar, null);
        fleet.Board(f.PilotId, stored.Child, stored.Carrier);
        long ammunition = f.Grant(215, 1000);
        f.Inventory.Move(f.PilotId, [ammunition], f.StationId, stored.Child, (int)InventoryFlag.AmmoHold, null);
        var contents = f.Inventory.List(f.PilotId, stored.Child);
        fleet.Board(f.PilotId, stored.Carrier, stored.Child);
        f.Inventory.Move(f.PilotId, [stored.Child], f.StationId, stored.Carrier, (int)InventoryFlag.ShipMaintenanceBay, null);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(contents, f.Inventory.List(f.PilotId, stored.Child));
    }

    [Fact]
    public void EscapeBayRequiresTheFrigateSkillsButMaintenanceCanTransportAnUnqualifiedHull()
    {
        using var stored = Create(638, HullClass.Battleship, InventoryFlag.FrigateEscapeBay);
        var f = stored.F;
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        f.Inventory.Move(f.PilotId, [stored.Child], stored.Carrier, f.StationId, (int)InventoryFlag.Hangar, null);
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var skills = HullCatalog
                .Skills(catalog, 587)
                .Except(HullCatalog.Skills(catalog, 638))
                .Except(HullCatalog.Skills(catalog, 28606))
                .ToArray();
            Assert.NotEmpty(skills);
            foreach (int skill in skills)
            {
                f.Train(skill, 0);
            }
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Inventory.Move(f.PilotId, [stored.Child], f.StationId, stored.Carrier, (int)InventoryFlag.FrigateEscapeBay, null)
        );
        Assert.Equal(f.StationId, fleet.Find(f.PilotId, stored.Child)!.LocationId);
        long carrier = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant(28606)], "Transport Orca").ShipIds);
        fleet.Board(f.PilotId, carrier, stored.Carrier);
        f.Inventory.Move(f.PilotId, [stored.Child], f.StationId, carrier, (int)InventoryFlag.ShipMaintenanceBay, null);
        Assert.Equal(carrier, fleet.Find(f.PilotId, stored.Child)!.LocationId);
        _ = WorldInitialization.Open(f.Db.Path);
        f.Inventory.Move(f.PilotId, [stored.Child], carrier, f.StationId, (int)InventoryFlag.Hangar, null);
        Assert.Throws<InvalidDataException>(() => fleet.Board(f.PilotId, stored.Child, carrier));
    }

    [Fact]
    public void VersionThirtySevenAddsEmptyStorageWithoutChangingShipCargoNamesOrDamage()
    {
        using Fixture f = new();
        long ship = f.ShipId;
        long cargo = f.Grant(3651, cargo: true);
        var items = f.Inventory.List(f.PilotId, ship);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(ship)!.Name = "Retained Ibis";
            world.Add(
                new ShipHealthState
                {
                    ShipId = ship,
                    ShieldCharge = 100,
                    ArmorDamage = 5,
                    StructureDamage = 3,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(TestWorld.DropShipStorageSchema + "PRAGMA user_version=37");
        }
        _ = WorldInitialization.Open(f.Db.Path);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(items, f.Inventory.List(f.PilotId, ship));
        Assert.Equal("Retained Ibis", f.Db.Store.FindPilot(1000001)!.ShipName);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Ships.Find(ship)!.StoredInId);
        Assert.Equal((int)InventoryFlag.Hangar, saved.Ships.Find(ship)!.StorageFlagId);
        Assert.Equal(5, saved.Set<ShipHealthState>().Find(ship)!.ArmorDamage);
        Assert.Equal(3, saved.Set<ShipHealthState>().Find(ship)!.StructureDamage);
        Assert.Equal(ship, saved.Set<OwnedItem>().Find(cargo)!.LocationId);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    private static WreckContact LoseCarrier(StoredFixture stored, bool survives)
    {
        var f = stored.F;
        NpcEncounterTests.Prepare(f.Db);
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(stored.Carrier, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        var attributes = f.Fitting.ShipAttributes(f.PilotId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = stored.Carrier,
                    ShieldCharge = 0,
                    ArmorDamage = attributes[DogmaAttributeId.ArmorHitPoints],
                    StructureDamage = attributes[DogmaAttributeId.StructureHitPoints] - 1,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.Add(NpcAggression.Start(npc.Id, stored.Carrier, 0, f.Clock.Now));
            world.SaveChanges();
        }
        var combat = new NpcCombatStore(new(f.Db.Path), f.Db.Store, new FixedLootRoll(survives));
        var shot = Assert.IsType<NpcShot>(combat.Fire(f.PilotId, stored.Carrier, npc.Id, SpacePosition.Zero, f.Clock.Now, 0, 1));
        Assert.NotNull(shot.Capsule);
        using WorldDbContext saved = new(f.Db.Path);
        var loss = saved.Set<ShipLoss>().Single(l => l.ShipId == stored.Carrier);
        using var manifest = JsonDocument.Parse(loss.StoredShipsJson);
        Assert.Equal(stored.Child, manifest.RootElement[0].GetProperty("Id").GetInt64());
        Assert.Equal("Stored trial", manifest.RootElement[0].GetProperty("Name").GetString());
        return Assert.Single(
            f.Db.Services.GetRequiredService<IWrecks>().List(npc.SystemId, f.Clock.Now),
            wreck => wreck.SourceId == stored.Carrier
        );
    }

    [Theory]
    [InlineData(true, InventoryFlag.ShipMaintenanceBay)]
    [InlineData(false, InventoryFlag.ShipMaintenanceBay)]
    [InlineData(true, InventoryFlag.FleetHangar)]
    public void CarrierLossRollsTheAssembledChildOnceAndPreservesOrRemovesItsWholeContents(bool survives, InventoryFlag destination)
    {
        using var stored = Create(638, HullClass.Battleship, InventoryFlag.FrigateEscapeBay);
        var f = stored.F;
        var items = f.Inventory.List(f.PilotId, stored.Child);
        var magazine = f.Inventory.List(f.PilotId, stored.Module!.Value);
        var wreck = LoseCarrier(stored, survives);
        var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
        Assert.Equal(!survives, wreck.Empty);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            if (!survives)
            {
                Assert.Null(saved.Ships.Find(stored.Child));
                Assert.Empty(saved.Set<OwnedItem>().Where(i => i.LocationId == stored.Child || i.LocationId == stored.Module));
                Assert.Null(saved.Set<ShipHealthState>().Find(stored.Child));
                Assert.Null(saved.Set<ModuleCooldown>().Find(stored.Module.Value));
                return;
            }
            Assert.Equal(wreck.Id, saved.Ships.Find(stored.Child)!.StoredInId);
        }
        _ = WorldInitialization.Open(f.Db.Path);
        using (var scope = f.Db.Services.CreateScope())
        {
            var pilot = f.Db.Store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(f.Db.Store, p, f.Clock, new SharedSpace(), f.Db.Objects, wrecks: wrecks),
                1,
                SignedSettings.Empty
            );
            context.Select(pilot);
            context.Flight.Snapshot();
            var reply = Assert.IsType<PilotCallResult.Success>(
                scope
                    .ServiceProvider.GetRequiredService<PilotCallRouter>()
                    .Call(context, "config", "GetMultiLocationsEx", new object?[] { new List<object?> { stored.Child } }, _ => { })
            );
            Assert.Contains(
                Assert.IsType<LocationTable>(reply.Reply).Locations,
                location => location.Id == stored.Child && location.Name == "Stored trial"
            );
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            var reference = Assert.IsType<BlueObjectReference>(
                router.CallWire(context, "invbroker", "GetInventoryFromId", new object?[] { stored.Child }, _ => { })
            );
            string container = context.ResolveReference(reference.Id)!;
            Assert.Equal(
                items.Count,
                Assert.IsType<List<object?>>(router.CallWire(context, container, "List", Array.Empty<object?>(), _ => { })).Count
            );
            Assert.IsType<PilotCallResult.Failure>(
                router.Call(context, container, "Add", new object?[] { f.Grant(215), f.StationId }, _ => { })
            );
        }
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var station = catalog.Set<Station>().Single(s => s.Id == f.StationId);
            f.Db.Store.SetPosition(f.ShipId, new(station.X, station.Y, station.Z), station.SolarSystemId);
        }
        f.Db.Store.Dock(f.PilotId, f.ShipId, checked((int)f.StationId));
        long rescue = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant(28606)], "Rescue Orca").ShipIds);
        fleet.Board(f.PilotId, rescue, f.ShipId);
        f.Db.Store.SetPosition(rescue, wreck.Position, wreck.SystemId);
        Assert.Contains(wrecks.Read(f.PilotId, wreck.Id, f.Clock.Now).Items, item => item.Id == stored.Child);
        Assert.Throws<InvalidDataException>(() => wrecks.Take(f.PilotId, rescue, wreck.Id, [stored.Child], null, f.Clock.Now));
        wrecks.Take(f.PilotId, rescue, wreck.Id, [stored.Child], null, f.Clock.Now, destination);
        Assert.Equal(items, f.Inventory.List(f.PilotId, stored.Child));
        Assert.Equal(magazine, f.Inventory.List(f.PilotId, stored.Module.Value));
        Assert.True(Assert.Single(wrecks.List(wreck.SystemId, f.Clock.Now), contact => contact.Id == wreck.Id).Empty);
        _ = WorldInitialization.Open(f.Db.Path);
    }

    [Fact]
    public void ExpiredWreckRemovesStoredHullItsMagazineAndItsDamageWithoutOrphans()
    {
        using var stored = Create(638, HullClass.Battleship, InventoryFlag.FrigateEscapeBay);
        var f = stored.F;
        var wreck = LoseCarrier(stored, true);
        f.Db.Services.GetRequiredService<IWrecks>()
            .List(wreck.SystemId, f.Clock.Now + (WreckDefinitions.LifetimeSeconds + 1) * TimeSpan.TicksPerSecond);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Ships.Find(stored.Child));
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.LocationId == stored.Child || i.LocationId == stored.Module));
        Assert.Null(saved.Set<ShipHealthState>().Find(stored.Child));
        Assert.Null(saved.Set<ModuleCooldown>().Find(stored.Module!.Value));
        _ = WorldInitialization.Open(f.Db.Path);
    }
}
