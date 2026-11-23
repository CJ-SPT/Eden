using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureAmmunitionTests
{
    [Fact]
    public void ExhaustedBatchSupplyLeavesUnreloadedMagazinesLoaded()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long first = f.Item(35922);
        long second = f.Item(35922);
        f.Db.Services.GetRequiredService<IPilotStructureFitting>().Fit(f.PilotId, structure.Id, [new(first), new(second)]);
        var ammunition = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        var original = ammunition.Load(f.PilotId, structure.Id, [first, second], [f.Item(37846, 1000)], f.ShipId);
        int secondQuantity = original.Magazines.Single(change => change.Current!.ModuleId == second).Current!.Quantity;
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(original.ReloadMilliseconds + 1));
        var changed = ammunition.Load(f.PilotId, structure.Id, [first, second], [f.Item(37847)], f.ShipId);
        Assert.Equal(first, Assert.Single(changed.Magazines).Current!.ModuleId);
        var unchanged = Assert.Single(ammunition.Read(f.PilotId, structure.Id), magazine => magazine.ModuleId == second);
        Assert.Equal(37846, unchanged.TypeId);
        Assert.Equal(secondQuantity, unchanged.Quantity);
    }

    [Fact]
    public void CompatiblePublishedChargeProfilesResolveAllPinnedFittedModifiers()
    {
        using var f = StructureAttackTests.Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        HashSet<int> groups = [];
        foreach (var type in catalog.Definitions.Items.Values.Where(type => type.CategoryId == (int)InventoryCategory.StructureModule))
        {
            if (
                !FittingCatalog
                    .Effects(catalog, type.Id)
                    .Any(effect =>
                        effect is DogmaEffect.HighPower or DogmaEffect.MediumPower or DogmaEffect.LowPower or DogmaEffect.RigSlot
                    )
            )
            {
                continue;
            }
            var module = StructureEquipmentCatalog.Require(catalog, type.Id);
            groups.UnionWith(
                Eden.Gameplay.Combat.MissileDefinitions.ChargeGroupAttributes.Select(attribute =>
                        checked((int)module.Attributes.GetValueOrDefault(attribute))
                    )
                    .Where(group => group > 0)
            );
        }
        int count = 0;
        foreach (
            var type in catalog.Definitions.Items.Values.Where(type =>
                type.CategoryId == (int)InventoryCategory.Charge && groups.Contains(type.GroupId)
            )
        )
        {
            Assert.Equal(type.Id, StructureChargeCatalog.Require(catalog, type.Id).TypeId);
            count++;
        }
        Assert.True(count > 10);
    }

    [Fact]
    public void NativeBayLoadTuplePrimingPartialUnloadAndPersonalWithdrawalUseCorporationCustody()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        var pilot = f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long launcher = f.Item(35921);
        f.Db.Services.GetRequiredService<IPilotStructureFitting>().Fit(f.PilotId, structure.Id, [new(launcher)]);
        long first = f.Item(37843, 100);
        long second = f.Item(37843, 50);
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            101,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Success("structureControl", "TakeControl", [structure.Id]);
        var bound = Assert.IsType<BlueObjectReference>(Success("invbroker", "GetInventoryFromId", [structure.Id]).Reply.ToBlue());
        string inventory = context.ResolveReference(bound.Id)!;
        Success(inventory, "MultiAdd", [new List<object?> { first, second }, f.ShipId], new() { ["flag"] = (long)InventoryFlag.Cargo });
        Success("dogmaIM", "LoadAmmo", [structure.Id, new HashSet<long> { launcher }, new List<object?> { first, second }, structure.Id]);
        var info = Assert.IsType<DogmaAllInfo>(Success("dogmaIM", "GetAllInfo", [true, true, true]).Reply);
        var charge = Assert.Single(info.Charges);
        Assert.Equal(structure.Id, charge.Key.ShipId);
        Assert.Equal(30, charge.Value.Attributes[DogmaAttributeId.Quantity]);
        Assert.Equal(30, Assert.Single(info.ShipState.Quantities[structure.Id]).Value.Magazine.Quantity);
        var magazine = Assert.Single(f.Db.Services.GetRequiredService<IPilotStructureAmmunition>().Read(f.PilotId, structure.Id));
        Assert.Equal((double)magazine.ReadyAt, info.ShipInfo[launcher].Attributes[DogmaAttributeId.NextActivationTime]);
        Assert.Contains(context.Notifications.OfType<GodmaItemPrimed>(), notification => notification.Item.Key == charge.Key);
        var tupleInfo = Assert.IsType<DogmaItemInfo<double>>(
            Success("dogmaIM", "ItemGetInfo", [new object?[] { structure.Id, (long)charge.Key.FlagId, (long)charge.Key.TypeId }]).Reply
        );
        Assert.Equal(30, tupleInfo.Attributes[DogmaAttributeId.Quantity]);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        var unload = Success("dogmaIM", "UnloadAmmo", [structure.Id, launcher, structure.Id, 10L]);
        long unloadedId = Assert.IsType<long>(Assert.Single(Assert.IsType<List<object?>>(WireAssertions.Materialize(unload.Reply))));
        info = Assert.IsType<DogmaAllInfo>(Success("dogmaIM", "GetAllInfo", [true, true, true]).Reply);
        Assert.Equal(20, Assert.Single(info.Charges).Value.Attributes[DogmaAttributeId.Quantity]);
        Success("@hangar", "Add", [first, structure.Id], new() { ["flag"] = (long)InventoryFlag.Hangar, ["qty"] = 1L });
        using var action = StructureAttackTests.Scope(f);
        var unloaded = Assert.Single(action.World.Filter<OwnedItem>(item => item.Id == unloadedId));
        Assert.Equal(f.CorporationId, unloaded.OwnerId);
        Assert.Equal(structure.Id, unloaded.LocationId);
        Assert.Equal((int)InventoryFlag.Cargo, unloaded.FlagId);
        Assert.Equal(10, unloaded.Quantity);
        Assert.Contains(
            action.World.Filter<OwnedItem>(item => item.OwnerId == f.PilotId && item.LocationId == structure.Id),
            item => item.TypeId == 37843 && item.FlagId == (int)InventoryFlag.Hangar && item.Quantity == 1
        );
        Assert.Equal(20, Assert.Single(action.World.Filter<OwnedItem>(item => item.LocationId == launcher)).Quantity);

        PilotCallResult.Success Success(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
        {
            var result = router.Call(context, service, method, args, _ => { }, keywords);
            Assert.True(result is PilotCallResult.Success, $"{service}.{method}: {result}");
            return Assert.IsType<PilotCallResult.Success>(result);
        }
    }

    [Fact]
    public async Task BayOwnershipReloadDeadlineChargeBonusesAndPartialUnloadSurviveCheckpoint()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        var ammunition = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        Assert.False(ammunition.Handles(f.ShipId));
        Assert.True(ammunition.Handles(structure.Id));
        long launcher = f.Item(35921);
        fitting.Fit(f.PilotId, structure.Id, [new(launcher), new(f.Item(35959))]);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        using (var action = StructureAttackTests.Scope(f))
        {
            var row = action.World.Set<SavedStructure>().Find(structure.Id)!;
            StructureBattlePersistence.Apply(action.World, catalog, row, new DamageVector(100, 0, 0, 0), f.Clock.Now);
            action.Commit();
        }
        Assert.True(f.Structures.Read(f.PilotId, structure.Id)!.DamageFractions!.Shield < 1);
        long source = f.Item(37843, 100);
        var deposited = ammunition.Deposit(f.PilotId, structure.Id, source, 60);
        long bay = Assert
            .Single(deposited.Changes, change => change.Item.OwnerId == f.CorporationId && change.Item.FlagId == (int)InventoryFlag.Cargo)
            .Item.Id;
        var transfer = ammunition.Load(f.PilotId, structure.Id, [launcher], [bay], structure.Id);
        var magazine = Assert.Single(transfer.Magazines).Current!;
        Assert.Equal(30, magazine.Quantity);
        Assert.Equal(10000, transfer.ReloadMilliseconds);
        Assert.Equal(f.Clock.Now + TimeSpan.TicksPerSecond * 10, magazine.ReadyAt);
        Assert.Throws<InvalidDataException>(() =>
            ammunition.Unload(f.PilotId, structure.Id, [launcher], new(structure.Id, f.CorporationId, (int)InventoryFlag.Cargo), null)
        );
        var attributes = ammunition.ChargeAttributes(f.PilotId, structure.Id, launcher);
        Assert.Equal(12600 * 1.07, attributes[DogmaAttributeId.EmDamage], 6);
        using (var warm = SessionPerformance.Begin("warm structure magazines", _ => { }, force: true)!)
        {
            Assert.Equal(30, Assert.Single(ammunition.Read(f.PilotId, structure.Id)).Quantity);
            Assert.Same(attributes, ammunition.ChargeAttributes(f.PilotId, structure.Id, launcher));
            Assert.Equal(0, warm.Commands);
            Assert.Equal(0, warm.Connections);
            Assert.Equal(0, warm.FittingCalculations);
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidDataException>(() =>
            ammunition.Unload(f.PilotId, structure.Id, [launcher], new(structure.Id, 90000002, (int)InventoryFlag.Hangar), null)
        );
        Assert.Equal(30, Assert.Single(ammunition.Read(f.PilotId, structure.Id)).Quantity);
        transfer = ammunition.Unload(f.PilotId, structure.Id, [launcher], new(structure.Id, f.CorporationId, (int)InventoryFlag.Cargo), 10);
        Assert.Equal(20, Assert.Single(transfer.Magazines).Current!.Quantity);
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.Equal(40, action.World.Set<OwnedItem>().Find(source)!.Quantity);
            Assert.Equal(
                40,
                action
                    .World.Filter<OwnedItem>(item =>
                        item.LocationId == structure.Id && item.OwnerId == f.CorporationId && item.FlagId == (int)InventoryFlag.Cargo
                    )
                    .Sum(item => item.Quantity)
            );
            var loaded = Assert.Single(action.World.Filter<OwnedItem>(item => item.LocationId == launcher));
            Assert.Equal(f.CorporationId, loaded.OwnerId);
            Assert.Equal(20, loaded.Quantity);
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-magazines.sqlite");
        File.Copy(f.Db.Path, copy);
        using LiveWorld reopened = new(new(copy), f.Clock);
        IPilotStructureAmmunition restored = new PilotStructureStore(new(copy), f.Clock);
        var saved = Assert.Single(restored.Read(f.PilotId, structure.Id));
        Assert.Equal(20, saved.Quantity);
        Assert.Equal(magazine.ReadyAt, saved.ReadyAt);
        Assert.Equal(
            attributes[DogmaAttributeId.EmDamage],
            restored.ChargeAttributes(f.PilotId, structure.Id, launcher)[DogmaAttributeId.EmDamage]
        );
    }

    [Fact]
    public void RejectedBatchAndForeignAccessCannotConsumeAmmunitionOrStartReloads()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        var ammunition = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        long launcher = f.Item(35921);
        long ecm = f.Item(35940);
        long source = f.Item(37843, 100);
        fitting.Fit(f.PilotId, structure.Id, [new(launcher), new(ecm)]);
        Assert.Throws<InvalidDataException>(() => ammunition.Load(f.PilotId, structure.Id, [launcher, ecm], [source], f.ShipId));
        Assert.Throws<InvalidDataException>(() => ammunition.Load(90000002, structure.Id, [launcher], [source], f.ShipId));
        long rejected = f.Item(35963);
        Assert.Throws<InvalidDataException>(() => ammunition.Deposit(f.PilotId, structure.Id, [new(source, 1), new(rejected)]));
        using var action = StructureAttackTests.Scope(f);
        Assert.Equal(100, action.World.Set<OwnedItem>().Find(source)!.Quantity);
        Assert.Equal(f.PilotId, action.World.Set<OwnedItem>().Find(source)!.OwnerId);
        Assert.Empty(action.World.Filter<OwnedItem>(item => item.LocationId == launcher || item.LocationId == ecm));
        Assert.Null(action.World.Set<ModuleCooldown>().Find(launcher));
        Assert.Null(action.World.Set<ModuleCooldown>().Find(ecm));
    }

    [Fact]
    public void LoadedScriptsModifyTheirModuleAndUnloadingRestoresTheUnscriptedAttributes()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        var ammunition = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        long module = f.Item(35949);
        fitting.Fit(f.PilotId, structure.Id, [new(module)]);
        double range = fitting.Read(f.PilotId, structure.Id).Equipment[module][DogmaAttributeId.MaxRange];
        ammunition.Load(f.PilotId, structure.Id, [module], [f.Item(47336)], f.ShipId);
        var scripted = fitting.Read(f.PilotId, structure.Id).Equipment[module];
        Assert.Equal(range * 0.3, scripted[DogmaAttributeId.MaxRange], 6);
        Assert.Equal(1, scripted[(DogmaAttributeId)1350]);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
        Assert.Throws<InvalidDataException>(() => fitting.Remove(f.PilotId, structure.Id, module));
        ammunition.Unload(f.PilotId, structure.Id, [module], new(structure.Id, f.CorporationId, (int)InventoryFlag.Cargo), null);
        Assert.Equal(range, fitting.Read(f.PilotId, structure.Id).Equipment[module][DogmaAttributeId.MaxRange]);
        Assert.Empty(fitting.Read(f.PilotId, structure.Id).Charges);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.PositiveInfinity)]
    public void InvalidMagazineCapacitiesRejectBeforeQuantityConversion(double capacity, double volume) =>
        Assert.Throws<InvalidDataException>(() => StructureAmmunitionRules.MaximumRounds(capacity, volume));
}
