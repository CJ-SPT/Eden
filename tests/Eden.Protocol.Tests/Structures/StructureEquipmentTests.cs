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
using Eden.Protocol.Structures;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureEquipmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerTransitionsPreserveDamageFractionsAcrossLegacyRowsAndReopenedCheckpoints(bool legacy)
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long service = f.Item(35899);
        f.Structures.FitServices(f.PilotId, structure.Id, [new(service)]);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 2000));
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        using (var action = StructureAttackTests.Scope(f))
        {
            var row = action.World.Set<SavedStructure>().Find(structure.Id)!;
            var defense = StructureBattlePersistence.Profile(action.World, catalog, row).Defense;
            action.World.Add(
                new SavedStructureBattle
                {
                    StructureId = row.Id,
                    Shield = defense.Shield / 2,
                    Armor = defense.Armor,
                    Hull = defense.Structure,
                    ShieldCapacity = legacy ? 0 : defense.Shield,
                    ArmorCapacity = legacy ? 0 : defense.Armor,
                    HullCapacity = legacy ? 0 : defense.Structure,
                }
            );
            action.Commit();
        }
        var powered = f.Structures.SetServiceOnline(f.PilotId, structure.Id, service, true).Structure;
        Assert.Equal(0.5, powered.DamageFractions!.Shield, 6);
        Assert.Equal(structure.Profile.Shield * 4, powered.Profile.Shield);
        using (var action = StructureAttackTests.Scope(f))
        {
            var row = action.World.Set<SavedStructure>().Find(structure.Id)!;
            StructureBattlePersistence.Apply(action.World, catalog, row, new(100, 0, 0, 0), f.Clock.Now);
            Assert.Equal(powered.Profile.Shield, action.World.Set<SavedStructureBattle>().Find(row.Id)!.ShieldCapacity);
            action.Commit();
        }
        var damaged = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.InRange(damaged.DamageFractions!.Shield, 0.49, 0.5 - double.Epsilon);
        using (var services = f.Db.Services.CreateScope())
        {
            var prime = services.ServiceProvider.GetRequiredService<StructureDogmaBuilder>().Prime(damaged);
            Assert.Equal(damaged.Battle!.Health.Shield, prime.Attributes[DogmaAttributeId.ShieldCharge]);
        }
        var low = f.Structures.SetServiceOnline(f.PilotId, structure.Id, service, false).Structure;
        Assert.Equal(structure.Profile.Shield, low.Profile.Shield);
        Assert.Equal(damaged.DamageFractions!.Shield, low.DamageFractions!.Shield, 12);
        powered = f.Structures.SetServiceOnline(f.PilotId, structure.Id, service, true).Structure;
        Assert.Equal(damaged.DamageFractions!.Shield, powered.DamageFractions!.Shield, 12);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-capacity.sqlite");
        File.Copy(f.Db.Path, copy);
        using LiveWorld reopened = new(new(copy), f.Clock);
        IPilotStructures restored = new PilotStructureStore(new(copy), f.Clock);
        var saved = restored.Read(f.PilotId, structure.Id)!;
        Assert.Equal(powered.Profile.Shield, saved.Profile.Shield);
        Assert.Equal(damaged.DamageFractions!.Shield, saved.DamageFractions!.Shield, 12);
        Assert.Equal(
            damaged.DamageFractions!.Shield,
            restored.SetServiceOnline(f.PilotId, structure.Id, service, false).Structure.DamageFractions!.Shield,
            12
        );
    }

    [Fact]
    public void NativeInventoryPrimesDefensiveEquipmentAndRoutesOnlineChangesAndRemoval()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        var docked = f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long launcher = f.Item(35921);
        long processor = f.Item(35963);
        using var scope = f.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, docked.Account, [docked], factory.Create, 71, SignedSettings.Empty);
        context.Select(docked);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Call("structureControl", "TakeControl", [structure.Id]);
        var reference = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", [structure.Id]).Reply.ToBlue());
        string inventory = context.ResolveReference(reference.Id)!;
        Call(
            inventory,
            "MultiAdd",
            [new List<object?> { launcher, processor }, f.ShipId],
            new() { ["flag"] = (long)InventoryFlag.AutoFit }
        );
        var updates = Assert.Single(context.Notifications.OfType<ModuleAttributesChanged>());
        Assert.Contains(
            updates.Changes,
            change =>
                change.Item == new DogmaItemKey.Item(structure.Id)
                && change.Attribute == DogmaAttributeId.CpuOutput
                && Math.Abs(change.Value - structure.Profile.Cpu * 1.08) < 0.000001
        );
        Assert.Contains(updates.Changes, change => change.Item == new DogmaItemKey.Item(launcher));
        var info = Assert.IsType<DogmaAllInfo>(Call("dogmaIM", "GetAllInfo", [true, true, true]).Reply);
        Assert.Equal(structure.Id, info.ActiveShipId);
        Assert.Contains(launcher, info.ShipInfo.Keys);
        Assert.Contains(processor, info.ShipInfo.Keys);
        Assert.Equal(structure.Profile.Cpu * 1.08, info.ShipInfo[structure.Id].Attributes[DogmaAttributeId.CpuOutput], 6);
        Assert.Equal(structure.Id, info.ShipInfo[launcher].Item!.LocationId);
        context.Notifications.Clear();
        Call("dogmaIM", "TakeModuleOffline", [structure.Id, processor]);
        updates = Assert.Single(context.Notifications.OfType<ModuleAttributesChanged>());
        Assert.Contains(
            updates.Changes,
            change =>
                change.Item == new DogmaItemKey.Item(structure.Id)
                && change.Attribute == DogmaAttributeId.CpuOutput
                && change.Value == structure.Profile.Cpu
        );
        info = Assert.IsType<DogmaAllInfo>(Call("dogmaIM", "GetAllInfo", [true, true, true]).Reply);
        Assert.Equal(structure.Profile.Cpu, info.ShipInfo[structure.Id].Attributes[DogmaAttributeId.CpuOutput]);
        Call("@hangar", "Add", [launcher, structure.Id], new() { ["flag"] = (long)InventoryFlag.Hangar });
        using var check = StructureAttackTests.Scope(f);
        Assert.Equal(f.PilotId, check.World.Set<OwnedItem>().Find(launcher)!.OwnerId);
        Assert.Equal((int)InventoryFlag.Hangar, check.World.Set<OwnedItem>().Find(launcher)!.FlagId);

        PilotCallResult.Success Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Assert.IsType<PilotCallResult.Success>(router.Call(context, service, method, args, _ => { }, keywords));
    }

    [Fact]
    public void ArmorEquipmentChangesTheAuthoritativeDefenseAndDamagePreventsRefitting()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long armor = f.Item(47360);
        long processor = f.Item(35963);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        var fitted = fitting.Fit(f.PilotId, structure.Id, [new(armor)]).Structure;
        Assert.Equal(structure.Profile.Armor, fitted.Profile.Armor);
        long service = f.Item(35899);
        f.Structures.FitServices(f.PilotId, structure.Id, [new(service)]);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(4051, 1000));
        fitted = f.Structures.SetServiceOnline(f.PilotId, structure.Id, service, true).Structure;
        Assert.Equal(structure.Profile.Shield * 4, fitted.Profile.Shield);
        Assert.Equal(structure.Profile.Armor * 4 * 1.1, fitted.Profile.Armor, 6);
        Assert.Equal(structure.Profile.Hull, fitted.Profile.Hull);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        using (var action = StructureAttackTests.Scope(f))
        {
            var row = action.World.Set<SavedStructure>().Find(structure.Id)!;
            var defense = StructureBattlePersistence.Profile(action.World, catalog, row).Defense;
            row.State = StructureState.ArmorVulnerable;
            action.World.Add(
                new SavedStructureBattle
                {
                    StructureId = row.Id,
                    Shield = 0,
                    Armor = defense.Armor,
                    Hull = defense.Structure,
                    ShieldCapacity = defense.Shield,
                    ArmorCapacity = defense.Armor,
                    HullCapacity = defense.Structure,
                }
            );
            var hit = StructureBattlePersistence.Apply(action.World, catalog, row, new(100, 0, 0, 0), f.Clock.Now);
            Assert.InRange(hit.Battle.Health.Armor, structure.Profile.Armor + 1, defense.Armor - 1);
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => fitting.Fit(f.PilotId, structure.Id, [new(processor)]));
        using var check = StructureAttackTests.Scope(f);
        Assert.Equal(f.PilotId, check.World.Set<OwnedItem>().Find(processor)!.OwnerId);
        Assert.Equal(f.ShipId, check.World.Set<OwnedItem>().Find(processor)!.LocationId);
    }

    [Fact]
    public async Task DefensiveFittingUsesStructureBudgetsAndPassiveModifiersAndSurvivesCheckpoint()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long launcher = f.Item(35921);
        long processor = f.Item(35963);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        fitting.Fit(f.PilotId, structure.Id, [new(launcher), new(processor)]);
        var stats = fitting.Read(f.PilotId, structure.Id);
        Assert.Equal(structure.Profile.Cpu * 1.08, stats.Attributes[DogmaAttributeId.CpuOutput], 6);
        Assert.Equal(1500, stats.Attributes[DogmaAttributeId.CpuLoad]);
        Assert.Equal(155000, stats.Attributes[DogmaAttributeId.PowerLoad]);
        using (var warm = SessionPerformance.Begin("warm structure fitting", _ => { }, force: true)!)
        {
            Assert.Same(stats, fitting.Read(f.PilotId, structure.Id));
            Assert.Equal(0, warm.Commands);
            Assert.Equal(0, warm.Connections);
            Assert.Equal(0, warm.FittingCalculations);
        }
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.Equal(f.CorporationId, action.World.Set<OwnedItem>().Find(launcher)!.OwnerId);
            Assert.Equal((int)InventoryFlag.HighSlot0, action.World.Set<OwnedItem>().Find(launcher)!.FlagId);
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(LiveWorld.Find(f.Db.Path)!.CaptureSnapshot());
        snapshot.Project();
        using WorldDbContext saved = new(snapshot.Database);
        Assert.True(saved.Set<OwnedItem>().Find(launcher)!.Online);
        Assert.Equal(structure.Id, saved.Set<OwnedItem>().Find(processor)!.LocationId);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-fitting.sqlite");
        File.Copy(f.Db.Path, copy);
        using LiveWorld reopened = new(new(copy), f.Clock);
        IPilotStructureFitting restored = new PilotStructureStore(new(copy), f.Clock);
        Assert.Equal(
            stats.Attributes[DogmaAttributeId.CpuOutput],
            restored.Read(f.PilotId, structure.Id).Attributes[DogmaAttributeId.CpuOutput]
        );
    }

    [Fact]
    public void RejectedBatchPreservesTheOriginalStackAndCommittedFitting()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long stack = f.Item(35921, 3);
        long other = f.Item(35921);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        Assert.Throws<InvalidDataException>(() =>
            fitting.Fit(f.PilotId, structure.Id, [new(stack), new(other, (int)InventoryFlag.LowSlot0)])
        );
        using var action = StructureAttackTests.Scope(f);
        Assert.Equal(3, action.World.Set<OwnedItem>().Find(stack)!.Quantity);
        Assert.Equal(f.PilotId, action.World.Set<OwnedItem>().Find(stack)!.OwnerId);
        Assert.Equal(f.ShipId, action.World.Set<OwnedItem>().Find(other)!.LocationId);
        Assert.Empty(
            action.World.Filter<OwnedItem>(item => item.LocationId == structure.Id && InventoryDefinitions.IsFittedSlot(item.FlagId))
        );
    }

    [Fact]
    public void RigSizeVariationFamilyCalibrationAndDestructiveRemovalUseThePinnedCatalog()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long rig = f.Item(37216);
        long variant = f.Item(37217);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        fitting.Fit(f.PilotId, structure.Id, [new(rig)]);
        var stats = fitting.Read(f.PilotId, structure.Id);
        Assert.Equal(100, stats.Attributes[DogmaAttributeId.CalibrationLoad]);
        Assert.Equal(45, stats.Equipment[rig][(DogmaAttributeId)2438], 6);
        Assert.Throws<InvalidDataException>(() => fitting.Fit(f.PilotId, structure.Id, [new(variant)]));
        fitting.Remove(f.PilotId, structure.Id, rig);
        using var action = StructureAttackTests.Scope(f);
        Assert.Null(action.World.Set<OwnedItem>().Find(rig));
        Assert.Equal(f.PilotId, action.World.Set<OwnedItem>().Find(variant)!.OwnerId);
    }

    [Fact]
    public void PublishedEquipmentProfilesResolveTheirTypedPassiveModifiers()
    {
        using var f = StructureAttackTests.Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        int count = 0;
        foreach (var type in catalog.Definitions.Items.Values.Where(type => type.CategoryId == StructureRules.ServiceCategoryId))
        {
            var effects = FittingCatalog.Effects(catalog, type.Id);
            if (
                !effects.Any(effect =>
                    effect is DogmaEffect.HighPower or DogmaEffect.MediumPower or DogmaEffect.LowPower or DogmaEffect.RigSlot
                )
            )
            {
                continue;
            }
            var profile = StructureEquipmentCatalog.Require(catalog, type.Id);
            Assert.Equal(type.Id, profile.TypeId);
            count++;
        }
        Assert.True(count > 250);
    }
}
