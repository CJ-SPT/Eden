using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotFittingTests
{
    [Theory]
    [InlineData(FittingSkill.CpuManagement, DogmaAttributeId.CpuOutput, DogmaAttributeId.Cpu, 0)]
    [InlineData(FittingSkill.CpuManagement, DogmaAttributeId.CpuOutput, DogmaAttributeId.Cpu, 1)]
    [InlineData(FittingSkill.CpuManagement, DogmaAttributeId.CpuOutput, DogmaAttributeId.Cpu, 5)]
    [InlineData(FittingSkill.PowerGridManagement, DogmaAttributeId.PowerOutput, DogmaAttributeId.Power, 0)]
    [InlineData(FittingSkill.PowerGridManagement, DogmaAttributeId.PowerOutput, DogmaAttributeId.Power, 1)]
    [InlineData(FittingSkill.PowerGridManagement, DogmaAttributeId.PowerOutput, DogmaAttributeId.Power, 5)]
    public void NativeFittingBudgetAllowsEarnedCapacityAndRejectsRealOverload(
        FittingSkill skill,
        DogmaAttributeId output,
        DogmaAttributeId cost,
        int level
    )
    {
        using TestWorld db = new();
        Prepare(db);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"INSERT INTO ItemType(Id,Name,GroupId) VALUES ({(int)skill},'Fitting skill',257)");
            catalog.Database.ExecuteSql($"INSERT INTO TypeAttribute VALUES ({(int)skill},275,1)");
            catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value=178 WHERE ItemTypeId=601 AND AttributeId={(int)output}");
            catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value=94 WHERE ItemTypeId=3651 AND AttributeId={(int)cost}");
        }
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql(
                $"INSERT INTO WorldPilotSkill VALUES ({pilot.PilotId},{(int)skill},{TrainedSkill.PointsForLevel(1, level)})"
            );
        }
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var fitting = db.Services.GetRequiredService<IPilotFitting>();
        long stack = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), item => item.TypeId == 3651).Id;
        long first = fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.AutoFit)[^1].Item.Id;
        fitting.SetOnline(pilot.PilotId, pilot.ShipId, first, true);
        long second = fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.AutoFit)[^1].Item.Id;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(db.Store, selected, TimeProvider.System, new(), db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var all = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(router.CallWire(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { })).State
        );
        var charInfo = Assert.IsType<object?[]>(all["charInfo"]);
        var brain = Assert.IsType<object?[]>(charInfo[1]);
        var effects = Assert
            .IsType<List<object?>>(brain[2])
            .Select(effect => Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueNewObject>(effect).State))
            .Where(effect => Equals(effect["toAttribID"], (int)output))
            .ToArray();
        Assert.Equal(level == 0 ? 0 : 1, effects.Length);
        double localOutput = 178 * (1 + (level == 0 ? 0 : Assert.IsType<double>(Assert.Single(effects)["value"])) / 100);
        var shipInfo = Assert.IsType<Dictionary<long, object?>>(all["shipInfo"]);
        var ship = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(shipInfo[pilot.ShipId]).State);
        Assert.Equal(localOutput, Assert.IsType<Dictionary<long, object?>>(ship["attributes"])[(long)output]);
        Assert.Equal(level == 5, localOutput >= 188);
        context.Notifications.Clear();
        var result = router.Call(context, "dogmaIM", "SetModuleOnline", new object?[] { pilot.ShipId, second }, _ => { });
        if (level == 5)
        {
            Assert.IsType<PilotCallResult.Success>(result);
            var changes = Assert.Single(context.Notifications.OfType<ModuleAttributesChanged>()).Changes;
            Assert.Equal(
                localOutput,
                Assert.Single(changes, change => change.Item == new DogmaItemKey.Item(pilot.ShipId) && change.Attribute == output).Value
            );
        }
        else
        {
            Assert.IsType<PilotCallResult.Failure>(result);
            Assert.Empty(context.Notifications);
        }
        _ = WorldInitialization.Open(db.Path);
        var modules = inventory.List(pilot.PilotId, pilot.ShipId);
        Assert.True(modules.Single(item => item.Id == first).Online);
        Assert.Equal(level == 5, modules.Single(item => item.Id == second).Online);
        Assert.Equal(
            level == 5 ? 188 : 94,
            fitting.ShipAttributes(pilot.PilotId)[cost == DogmaAttributeId.Cpu ? DogmaAttributeId.CpuLoad : DogmaAttributeId.PowerLoad]
        );
    }

    private static void Prepare(TestWorld db)
    {
        using Eden.World.Catalog.CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO DogmaAttribute(Id,Name) VALUES (14,'hiSlots'),(48,'cpuOutput'),(11,'powerOutput'),(50,'cpu'),(30,'power'),
                (182,'requiredSkill1'),(277,'requiredSkill1Level');
            INSERT INTO TypeAttribute VALUES (601,14,2),(601,48,135),(601,11,23),(3651,50,65),(3651,30,2),(3651,182,3386),(3651,277,1);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (3386,'Mining',257);
            INSERT INTO TypeAttribute VALUES (3386,275,1);
            """
        );
        using WorldDbContext world = new(db.Path);
        world.Database.ExecuteSqlRaw(
            "INSERT INTO WorldPilotSkill VALUES (90000001,3386,250); UPDATE WorldItem SET Quantity=3 WHERE OwnerId=90000001 AND TypeId=3651"
        );
    }

    [Fact]
    public void FitsOneFromStackChecksSlotsAndPersistsOnlineState()
    {
        using TestWorld db = new();
        Prepare(db);
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var fitting = db.Services.GetRequiredService<IPilotFitting>();
        var pilot = db.Store.FindPilot(1000001)!;
        var stack = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        var changes = fitting.Fit(pilot.PilotId, [stack.Id], pilot.StationId, (int)InventoryFlag.AutoFit);
        Assert.Equal(2, changes.Count);
        var module = Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal((int)InventoryFlag.HighSlot0, module.FlagId);
        Assert.True(module.Singleton);
        Assert.False(module.Online);
        Assert.Equal(
            2,
            Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType).Quantity
        );
        fitting.SetOnline(pilot.PilotId, pilot.ShipId, module.Id, true);
        fitting.SetOnline(pilot.PilotId, pilot.ShipId, module.Id, true);
        Assert.Equal(65, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.CpuLoad]);
        Assert.Equal(2, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.PowerLoad]);
        _ = WorldInitialization.Open(db.Path);
        Assert.True(Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId)).Online);
        fitting.Fit(pilot.PilotId, [stack.Id], pilot.StationId, (int)InventoryFlag.AutoFit);
        Assert.Throws<InvalidDataException>(() => fitting.Fit(pilot.PilotId, [stack.Id], pilot.StationId, (int)InventoryFlag.AutoFit));
        Assert.Equal(
            1,
            Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType).Quantity
        );
        fitting.SetOnline(pilot.PilotId, pilot.ShipId, module.Id, false);
        Assert.Equal(0, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.CpuLoad]);
        inventory.Move(pilot.PilotId, [module.Id], pilot.ShipId, pilot.ShipId, (int)InventoryFlag.Cargo, null);
        var unfit = inventory.List(pilot.PilotId, pilot.ShipId).Single(i => i.Id == module.Id);
        Assert.Equal((int)InventoryFlag.Cargo, unfit.FlagId);
        Assert.True(unfit.Singleton);
        Assert.False(unfit.Online);
        Assert.Equal(2, TaskTrackingTestData.Count(db, TaskEvents.Fitted));
        Assert.Equal(2, TaskTrackingTestData.Count(db, TaskEvents.FittedHigh));
    }

    [Fact]
    public void RejectsMissingSkillsForeignItemsSpaceFitsAndResourceOverload()
    {
        using TestWorld db = new();
        Prepare(db);
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var fitting = db.Services.GetRequiredService<IPilotFitting>();
        var pilot = db.Store.FindPilot(1000001)!;
        long stack = Assert
            .Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType)
            .Id;
        long foreign = Assert.Single(inventory.List(90000002, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType).Id;
        Assert.Throws<InvalidDataException>(() => fitting.Fit(pilot.PilotId, [foreign], pilot.StationId, (int)InventoryFlag.AutoFit));
        Assert.Throws<InvalidDataException>(() => fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.HighSlot7));
        Assert.Throws<InvalidDataException>(() => fitting.Fit(pilot.PilotId, [stack, stack], pilot.StationId, (int)InventoryFlag.AutoFit));
        db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.AutoFit));
        db.Store.SetPosition(pilot.ShipId, null);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DELETE FROM WorldPilotSkill WHERE TypeId=3386");
        }
        Assert.Throws<InvalidDataException>(() => fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.AutoFit));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("INSERT INTO WorldPilotSkill VALUES (90000001,3386,250)");
        }
        var module = fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.AutoFit)[^1].Item;
        using (Eden.World.Catalog.CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=64 WHERE ItemTypeId=601 AND AttributeId=48");
        }
        Assert.Throws<InvalidDataException>(() => fitting.SetOnline(pilot.PilotId, pilot.ShipId, module.Id, true));
        Assert.False(Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId)).Online);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FittingNotificationsKeepTheNativeModuleRowAndRestoreOnlineEffects(bool native)
    {
        using TestWorld db = new();
        Prepare(db);
        var pilot = db.Store.FindPilot(1000001)!;
        using var scope = db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var inventory = scope.ServiceProvider.GetRequiredService<IPilotInventory>();
        var stack = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(db.Store, selected, TimeProvider.System, new(), db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        Assert.Null(
            router.CallWire(
                context,
                "@ship",
                "MultiAdd",
                new object?[]
                {
                    new List<object?> { stack.Id },
                    (long)pilot.StationId,
                },
                _ => { },
                new() { ["flag"] = (long)InventoryFlag.AutoFit }
            )
        );
        var module = Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal(new[] { "OnItemChange", "OnItemChange", "OnModuleAttributeChanges" }, context.Notifications.Select(n => n.Name));
        var row = Assert.IsType<object?[]>(Assert.IsType<BlueConstructor>(context.Notifications[1].Arguments[0]).Arguments[1]);
        Assert.Equal(InventoryWireDefinitions.AssembledQuantity, row[(int)InventoryColumn.Quantity]);
        Assert.Equal(9, row.Length);
        var descriptor = Assert.IsType<BlueConstructor>(Assert.IsType<BlueConstructor>(context.Notifications[1].Arguments[0]).Arguments[0]);
        Assert.True(BlueRowDescriptor.IsInventoryVirtuals(descriptor.State));
        foreach (bool online in new[] { true, false, true })
        {
            context.Notifications.Clear();
            string method = native
                ? online
                    ? "Activate"
                    : "Deactivate"
                : online
                    ? "SetModuleOnline"
                    : "TakeModuleOffline";
            object?[] args = native
                ? online
                    ? [module.Id, InventoryWireDefinitions.OnlineEffectName, null, 0L]
                    : [module.Id, InventoryWireDefinitions.OnlineEffectName]
                : [pilot.ShipId, module.Id];
            Assert.Null(router.CallWire(context, "dogmaIM", method, args, _ => { }));
            Assert.Equal(new[] { "OnGodmaShipEffect", "OnModuleAttributeChanges" }, context.Notifications.Select(n => n.Name));
            var effect = context.Notifications[0].Arguments;
            Assert.Equal(module.Id, effect[0]);
            Assert.Equal((int)DogmaEffect.Online, effect[1]);
            Assert.Equal(online, effect[3]);
            var info = Assert.IsType<Dictionary<string, object?>>(
                Assert.IsType<BlueInstance>(router.CallWire(context, "dogmaIM", "ItemGetInfo", new object?[] { module.Id }, _ => { })).State
            );
            var attributes = Assert.IsType<Dictionary<long, object?>>(info["attributes"]);
            Assert.Equal(online ? 1d : 0d, attributes[(long)DogmaAttributeId.IsOnline]);
            Assert.NotEmpty(BlueMarshal.Encode(info));
        }
        _ = WorldInitialization.Open(db.Path);
        var all = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(router.CallWire(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { })).State
        );
        var shipInfo = Assert.IsType<Dictionary<long, object?>>(all["shipInfo"]);
        var restored = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(shipInfo[module.Id]).State);
        Assert.Single(Assert.IsType<Dictionary<long, object?>>(restored["activeEffects"]));
        Assert.Null(
            router.CallWire(context, "dogmaIM", "UnloadAmmo", new object?[] { pilot.ShipId, module.Id, (long)pilot.StationId }, _ => { })
        );
        Assert.Null(router.CallWire(context, "@hangar", "Add", new object?[] { module.Id, pilot.ShipId }, _ => { }));
        Assert.Empty(inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.False(inventory.List(pilot.PilotId, pilot.StationId).Single(i => i.Id == module.Id).Online);
    }

    [Fact]
    public void VersionThirteenMigrationPreservesCargoWithoutRepeatingTheGrant()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        var moved = Assert
            .Single(inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, (int)InventoryFlag.Cargo, null))
            .Item;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                TestWorld.DropTestingMarketSchema
                    + "ALTER TABLE WorldItem DROP COLUMN Singleton; ALTER TABLE WorldItem DROP COLUMN Online; DROP TABLE "
                    + "WorldAsteroid; DROP TABLE WorldShipEnergy; DROP TABLE WorldMarketTrade; DROP TABLE "
                    + "WorldMarketOrder; DROP TABLE WorldMarketStation; DROP TABLE WorldWallet; DROP TABLE "
                    + "WorldShipHealth; DROP TABLE WorldWreck; DROP TABLE WorldRepairPayment; DROP TABLE "
                    + "WorldNpcAggression; DROP TABLE WorldShipLoss; DROP TABLE WorldNpc; DROP TABLE WorldNpcBounty; DROP "
                    + "TABLE WorldWeaponCooldown; DELETE FROM WorldItem WHERE TypeId=3638; PRAGMA user_version=13"
            );
        }
        _ = WorldInitialization.Open(db.Path);
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(moved, Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId)));
        Assert.DoesNotContain(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        Assert.Single(inventory.List(90000002, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
    }
}
