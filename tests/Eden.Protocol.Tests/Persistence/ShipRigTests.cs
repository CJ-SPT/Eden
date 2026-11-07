using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ShipRigTests
{
    private sealed class Fixture : IDisposable
    {
        internal PassiveFittingTests.Fixture Base { get; } = new();
        internal long Pilot => Base.PilotId;
        internal long Ship => Base.ShipId;
        internal PilotFittingStore Fitting => Base.Fitting;

        internal Fixture()
        {
            PassiveCatalogFixture.LoadRigs(Base.CatalogPath);
            Hull(603); // Merlin: three small rigs, 400 calibration.
        }

        internal void Hull(int type)
        {
            using WorldDbContext world = new(Base.Db.Path);
            world.Ships.Find(Ship)!.TypeId = type;
            world.SaveChanges();
        }

        internal long Fit(int type) => Base.Fit(type, online: false);

        internal InventoryItem Item(long id) => Base.Inventory.List(Pilot, Ship).Single(i => i.Id == id);

        internal IReadOnlyDictionary<DogmaAttributeId, double> Attributes => Fitting.ShipAttributes(Pilot);

        public void Dispose() => Base.Dispose();
    }

    [Fact]
    public void CatalogAdmitsAllPublishedTechOneAndTwoShipRigsWithCompleteEffects()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.Base.CatalogPath);
        var rigs = catalog.Set<ItemType>().Where(t => RigRules.Groups.Contains(t.GroupId)).OrderBy(t => t.Id).ToArray();
        Assert.Equal(642, rigs.Length);
        Assert.Equal(14, rigs.Select(t => t.GroupId).Distinct().Count());
        foreach (var rig in rigs)
        {
            Assert.True(RigCatalog.Supported(catalog, rig.Id, rig.GroupId), $"Unsupported rig {rig.Id}: {rig.Name}");
            var definition = Assert.IsType<ModuleFittingDefinition>(FittingCatalog.Definition(catalog, rig.Id));
            Assert.Equal(ModuleRack.Rig, definition.Rack);
            Assert.Equal((int)InventoryFlag.RigSlot0, definition.FirstFlag);
            Assert.False(definition.Turret || definition.Launcher);
            Assert.NotEmpty(RigCatalog.Modifiers(catalog, rig.Id));
        }
        Assert.False(RigCatalog.Supported(catalog, 377, (int)PassiveModuleGroup.ShieldExtender));
    }

    [Theory]
    [InlineData(31788, 1.15, 50)]
    [InlineData(31794, 1.2, 75)]
    public void ShieldRigsFitFromStacksApplyPermanentlyAndDestroyOnRemoval(int type, double bonus, double cost)
    {
        using Fixture f = new();
        double shield = f.Attributes[DogmaAttributeId.ShieldCapacity];
        double signature = f.Attributes[DogmaAttributeId.SignatureRadius];
        long stack = f.Base.Grant(type, 2);
        var changes = f.Fitting.Fit(f.Pilot, [stack], f.Base.StationId, (int)InventoryFlag.AutoFit);
        var rig = Assert.Single(changes, c => c.Previous is null).Item;
        Assert.Equal((int)InventoryFlag.RigSlot0, rig.FlagId);
        Assert.True(rig.Singleton);
        Assert.False(rig.Online);
        Assert.Equal(shield * bonus, f.Attributes[DogmaAttributeId.ShieldCapacity], 8);
        Assert.Equal(signature * 1.1, f.Attributes[DogmaAttributeId.SignatureRadius], 8);
        Assert.Equal(cost, f.Attributes[DogmaAttributeId.CalibrationLoad]);
        Assert.Equal(2, f.Attributes[DogmaAttributeId.RigSlotsLeft]);
        Assert.Throws<InvalidDataException>(() => f.Fitting.SetOnline(f.Pilot, f.Ship, rig.Id, true));
        Assert.Throws<InvalidDataException>(() => f.Fitting.SetOnline(f.Pilot, f.Ship, rig.Id, false));
        _ = WorldInitialization.Open(f.Base.Db.Path);
        Assert.Equal(shield * bonus, f.Attributes[DogmaAttributeId.ShieldCapacity], 8);
        var removed = f.Fitting.DestroyRig(f.Pilot, f.Ship, rig.Id);
        Assert.Equal(0, Assert.Single(removed).Item.Quantity);
        Assert.Equal(shield, f.Attributes[DogmaAttributeId.ShieldCapacity]);
        Assert.Equal(signature, f.Attributes[DogmaAttributeId.SignatureRadius]);
        Assert.Equal(0, f.Attributes[DogmaAttributeId.CalibrationLoad]);
        Assert.Equal(3, f.Attributes[DogmaAttributeId.RigSlotsLeft]);
        _ = WorldInitialization.Open(f.Base.Db.Path);
        using WorldDbContext check = new(f.Base.Db.Path);
        Assert.Null(check.Set<OwnedItem>().Find(rig.Id));
        Assert.Equal(1, check.Set<OwnedItem>().Find(stack)!.Quantity);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(3, 7)]
    [InlineData(5, 5)]
    public void RiggingSkillsReduceDrawbacksWithoutReducingBonuses(int level, double drawback)
    {
        using Fixture f = new();
        f.Base.Train((int)RiggingSkill.Armor, level);
        double armor = f.Attributes[DogmaAttributeId.ArmorHitPoints];
        double agility = f.Attributes[DogmaAttributeId.Agility];
        long rig = f.Fit(31057); // Small Trimark II.
        Assert.Equal(armor * 1.2, f.Attributes[DogmaAttributeId.ArmorHitPoints], 8);
        Assert.Equal(agility * (1 + drawback / 100), f.Attributes[DogmaAttributeId.Agility], 8);
        Assert.Equal(drawback, f.Fitting.ModuleAttributes(f.Pilot, f.Item(rig))[DogmaAttributeId.RigDrawback], 8);
        if (level > 0)
        {
            var brain = f.Base.Db.Services.GetRequiredService<Eden.Protocol.Skills.PilotBrain>().Read(f.Pilot, f.Ship);
            Assert.Contains(brain.ShipEffects, e => e.Attribute == DogmaAttributeId.RigDrawback && e.Value == -10 * level);
        }
    }

    [Fact]
    public void CalibrationSlotSizeOwnershipAndSpaceRejectionsLeaveInventoryUnchanged()
    {
        using Fixture f = new();
        long medium = f.Base.Grant(31790);
        long heavy = f.Base.Grant(31544, 2); // 300 calibration each.
        long cheap = f.Base.Grant(31788, 4);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [medium], f.Base.StationId, 0));
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [cheap], f.Base.StationId, (int)InventoryFlag.HighSlot0));
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [heavy, cheap], f.Base.StationId, (int)InventoryFlag.RigSlot0));
        var first = f.Fitting.Fit(f.Pilot, [heavy], f.Base.StationId, 0).Single(c => c.Previous is null).Item;
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [heavy], f.Base.StationId, 0));
        Assert.Equal(1, f.Base.Inventory.List(f.Pilot, f.Base.StationId).Single(i => i.Id == heavy).Quantity);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [first.Id], f.Ship, (int)InventoryFlag.RigSlot2));
        Assert.Throws<InvalidDataException>(() => f.Fitting.DestroyRig(f.Pilot + 1, f.Ship, first.Id));
        Assert.Throws<InvalidDataException>(() => f.Fitting.DestroyRig(f.Pilot, f.Ship + 1, first.Id));
        f.Base.Db.Store.SetPosition(f.Ship, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [cheap], f.Base.StationId, 0));
        Assert.Throws<InvalidDataException>(() => f.Fitting.DestroyRig(f.Pilot, f.Ship, first.Id));
        f.Base.Db.Store.SetPosition(f.Ship, null);
        f.Fitting.Fit(f.Pilot, [cheap], f.Base.StationId, 0);
        f.Fitting.Fit(f.Pilot, [cheap], f.Base.StationId, 0);
        Assert.Equal(400, f.Attributes[DogmaAttributeId.CalibrationLoad]);
        Assert.Equal(0, f.Attributes[DogmaAttributeId.RigSlotsLeft]);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [cheap], f.Base.StationId, 0));
        Assert.Equal(2, f.Base.Inventory.List(f.Pilot, f.Base.StationId).Single(i => i.Id == cheap).Quantity);
        Assert.Equal(3, f.Base.Inventory.List(f.Pilot, f.Ship).Count);
        f.Fitting.DestroyRig(f.Pilot, f.Ship, first.Id);
        f.Fitting.Fit(f.Pilot, [cheap], f.Base.StationId, (int)InventoryFlag.RigSlot0);
        Assert.Equal(150, f.Attributes[DogmaAttributeId.CalibrationLoad]);
    }

    [Fact]
    public void FailedBatchIsAtomicAndRiglessHullsAndHiggsLimitsAreEnforced()
    {
        using Fixture f = new();
        long first = f.Base.Grant(31544, 2);
        long second = f.Base.Grant(31544, 2);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [first, second], f.Base.StationId, 0));
        Assert.Empty(f.Base.Inventory.List(f.Pilot, f.Ship));
        Assert.All(f.Base.Inventory.List(f.Pilot, f.Base.StationId).Where(i => i.TypeId == 31544), i => Assert.Equal(2, i.Quantity));
        f.Hull(601); // Corvettes have no rig slots or calibration.
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [first], f.Base.StationId, 0));
        f.Hull(603);
        double mass = f.Attributes[DogmaAttributeId.Mass];
        double speed = f.Attributes[DogmaAttributeId.MaxVelocity];
        long anchor = f.Fit(34266);
        Assert.Equal(mass * 2, f.Attributes[DogmaAttributeId.Mass], 8);
        Assert.Equal(speed * .25, f.Attributes[DogmaAttributeId.MaxVelocity], 8);
        long duplicate = f.Base.Grant(34266);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.Pilot, [duplicate], f.Base.StationId, 0));
        Assert.Equal(1, f.Base.Inventory.List(f.Pilot, f.Base.StationId).Single(i => i.Id == duplicate).Quantity);
        Assert.Equal(25, f.Attributes[DogmaAttributeId.CalibrationLoad]);
        Assert.Equal(anchor, Assert.Single(f.Base.Inventory.List(f.Pilot, f.Ship)).Id);
    }

    [Fact]
    public void CoreRigsApplyTheirTechLevelsWithoutDrawbacksOrStackingPenalties()
    {
        using Fixture f = new();
        double recharge = f.Attributes[DogmaAttributeId.CapacitorRechargeTime];
        double grid = f.Attributes[DogmaAttributeId.PowerOutput];
        double signature = f.Attributes[DogmaAttributeId.SignatureRadius];
        f.Fit(31376); // T2 capacitor circuit, -20% recharge time.
        f.Fit(31370); // T1 capacitor circuit, -15% recharge time.
        f.Fit(31364); // T2 current router, +15% power grid.
        Assert.Equal(recharge * .8 * .85, f.Attributes[DogmaAttributeId.CapacitorRechargeTime], 8);
        Assert.Equal(Math.Round(grid * 1.15, 2), f.Attributes[DogmaAttributeId.PowerOutput]);
        Assert.Equal(signature, f.Attributes[DogmaAttributeId.SignatureRadius]);
        Assert.Equal(400, f.Attributes[DogmaAttributeId.CalibrationLoad]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CargoOrHangarMovesDestroyRigsAndOverfilledRemovalRollsBack(bool cargo)
    {
        using Fixture f = new();
        long rig = f.Fit(31117); // Cargo +15%, armor drawback.
        double expanded = f.Attributes[DogmaAttributeId.CargoCapacity];
        Assert.Equal(150 * 1.15, expanded, 8);
        long load = f.Base.Grant(3651, 34, cargo: true); // 170 m3 in a 150 m3 base hold.
        Assert.Throws<CargoCapacityException>(() => f.Fitting.DestroyRig(f.Pilot, f.Ship, rig));
        Assert.Throws<CargoCapacityException>(() => Remove());
        Assert.Equal(expanded, f.Attributes[DogmaAttributeId.CargoCapacity]);
        Assert.Equal((int)InventoryFlag.RigSlot0, f.Item(rig).FlagId);
        f.Base.Inventory.Move(f.Pilot, f.Base.StationId, new([load], f.Ship, (int)InventoryFlag.Hangar, 4));
        Assert.Equal(0, Assert.Single(Remove()).Item.Quantity);
        using WorldDbContext check = new(f.Base.Db.Path);
        Assert.Null(check.Set<OwnedItem>().Find(rig));
        Assert.Equal(150, f.Attributes[DogmaAttributeId.CargoCapacity]);

        IReadOnlyList<InventoryChange> Remove() =>
            f.Base.Inventory.Move(
                f.Pilot,
                cargo ? f.Ship : f.Base.StationId,
                new([rig], f.Ship, (int)(cargo ? InventoryFlag.Cargo : InventoryFlag.Hangar), null)
            );
    }

    [Fact]
    public void TurretRigsShareStackingPenaltiesAndApplySkillReducedFittingDrawbacks()
    {
        using Fixture f = new();
        f.Base.Train((int)RiggingSkill.HybridWeapon, 5);
        long turret = f.Base.Fit(10678, online: false); // 125mm Railgun I.
        var before = f.Fitting.ModuleAttributes(f.Pilot, f.Item(turret));
        f.Fit(31538);
        f.Fit(31538);
        var after = f.Fitting.ModuleAttributes(f.Pilot, f.Item(turret));
        double second = Math.Exp(-Math.Pow(1 / 2.67, 2));
        Assert.Equal(before[DogmaAttributeId.DamageMultiplier] * 1.1 * (1 + 0.1 * second), after[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(Math.Round(before[DogmaAttributeId.Power] * 1.05 * 1.05, 2), after[DogmaAttributeId.Power]);
        Assert.Equal(before[DogmaAttributeId.Cpu], after[DogmaAttributeId.Cpu]);
        Assert.Equal(400, f.Attributes[DogmaAttributeId.CalibrationLoad]);
    }

    [Fact]
    public void DroneRigsReachProfilesAndControlRangeWithCpuDrawbacks()
    {
        using Fixture f = new();
        f.Hull(16229);
        f.Base.Train((int)RiggingSkill.Drones, 5);
        var drones = f.Base.Db.Services.GetRequiredService<IPilotDrones>();
        var before = drones.Profile(f.Pilot, 2488)!;
        double cpu = f.Attributes[DogmaAttributeId.CpuOutput];
        int range = drones.Control(f.Pilot).Range;
        f.Fit(32039); // Medium durability II +25%.
        f.Fit(32063); // Medium speed II +15%.
        f.Fit(32031); // Medium control range II +20 km.
        var after = drones.Profile(f.Pilot, 2488)!;
        Assert.Equal(before.Defense.Shield * 1.25, after.Defense.Shield, 8);
        Assert.Equal(before.Defense.Armor * 1.25, after.Defense.Armor, 8);
        Assert.Equal(before.Defense.Structure * 1.25, after.Defense.Structure, 8);
        Assert.Equal(before.MaximumSpeed * 1.15f, after.MaximumSpeed, 3);
        Assert.Equal(range + 20000, drones.Control(f.Pilot).Range);
        Assert.Equal(Math.Round(cpu * Math.Pow(.95, 3), 2), f.Attributes[DogmaAttributeId.CpuOutput]);
        Assert.Equal(375, f.Attributes[DogmaAttributeId.CalibrationLoad]);
        using WorldDbContext world = new(f.Base.Db.Path);
        using CatalogDbContext catalog = new(f.Base.CatalogPath);
        var released = DroneCatalog.Skilled(world, catalog, f.Pilot, 2488, controllerShipId: 0);
        Assert.Equal(before.Defense, released.Defense);
        Assert.Equal(before.MaximumSpeed, released.MaximumSpeed);
    }

    [Fact]
    public void RigFitAndRemovalInvalidateResidentShipAndDroneProfiles()
    {
        using Fixture f = new();
        f.Hull(16229);
        using ResidentFitCache cache = new(new(f.Base.Db.Path));
        var before = Read();
        double cpu = f.Attributes[DogmaAttributeId.CpuOutput];
        long rig = f.Fit(32039);
        Assert.Equal(before.Defense.Shield * 1.25, Read().Defense.Shield, 8);
        Assert.Equal(Math.Round(cpu * .9, 2), f.Attributes[DogmaAttributeId.CpuOutput]);
        using (var metric = SessionPerformance.Begin("rig.owner.cache", _ => { }, force: true)!)
        {
            Assert.Equal(before.Defense.Shield * 1.25, Read().Defense.Shield, 8);
            Assert.Equal(0, metric.FittingCalculations);
        }
        f.Fitting.DestroyRig(f.Pilot, f.Ship, rig);
        Assert.Equal(before.Defense, Read().Defense);
        Assert.Equal(cpu, f.Attributes[DogmaAttributeId.CpuOutput]);

        DroneProfile Read()
        {
            using WorldDbContext world = new(f.Base.Db.Path);
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(f.Base.CatalogPath);
            var profile = DroneCatalog.Skilled(world, catalog, f.Pilot, 2488);
            transaction.Commit();
            return profile;
        }
    }

    [Fact]
    public void VersionNinetyFourUpgradeAddsRigRevisionTriggersWithoutChangingItems()
    {
        using TestWorld db = new(freshInitialization: true);
        long[] items;
        using (WorldDbContext world = new(db.Path))
        {
            items = world.Set<OwnedItem>().OrderBy(i => i.Id).Select(i => i.Id).ToArray();
            world.Database.ExecuteSqlRaw(
                """
                DROP TRIGGER Fit_WorldRig_INSERT_NEW;
                DROP TRIGGER Fit_WorldRig_UPDATE_OLD;
                DROP TRIGGER Fit_WorldRig_UPDATE_NEW;
                DROP TRIGGER Fit_WorldRig_DELETE_OLD;
                PRAGMA user_version=94;
                """
            );
        }
        _ = WorldInitialization.Open(db.Path);
        _ = WorldInitialization.Open(db.Path);
        using WorldDbContext reopened = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(items, reopened.Set<OwnedItem>().OrderBy(i => i.Id).Select(i => i.Id).ToArray());
        Assert.Equal(
            4,
            reopened
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_schema WHERE type='trigger'")
                .AsEnumerable()
                .Count(n => n.StartsWith("Fit_WorldRig_", StringComparison.Ordinal))
        );
    }

    [Fact]
    public void MissileRigsModifyLoadedWarheadsAndLauncherCpuWithReducedDrawbacks()
    {
        using Fixture f = new();
        f.Hull(602); // Kestrel, four launchers and small rigs.
        f.Base.Train((int)RiggingSkill.Launcher, 5);
        long launcher = f.Base.Fit(499);
        long ammunition = f.Base.Grant(212, 10);
        f.Base.Db.Services.GetRequiredService<IPilotAmmunition>()
            .Load(f.Pilot, f.Ship, [launcher], [ammunition], f.Base.StationId, f.Base.Clock.Now);
        var before = Read();
        double cpu = f.Fitting.ModuleAttributes(f.Pilot, f.Item(launcher))[DogmaAttributeId.Cpu];
        f.Fit(31598); // Hydraulic Bay Thrusters I, +15% missile speed.
        f.Fit(31626); // Warhead Calefaction Catalyst II, +15% damage.
        var after = Read();
        Assert.Equal(before.Velocity * 1.15, after.Velocity, 8);
        Assert.Equal(before.Damage * 1.15, after.Damage, 8);
        Assert.Equal(Math.Round(cpu * 1.05 * 1.05, 2), f.Fitting.ModuleAttributes(f.Pilot, f.Item(launcher))[DogmaAttributeId.Cpu]);
        Assert.Equal(400, f.Attributes[DogmaAttributeId.CalibrationLoad]);

        (double Velocity, double Damage) Read()
        {
            using WorldDbContext world = new(f.Base.Db.Path);
            using CatalogDbContext catalog = new(f.Base.CatalogPath);
            var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.Pilot);
            var fit = FittingCalculation.Read(world, catalog, pilot);
            return (
                fit.Graph.Read(Assert.Single(fit.Charges).Item.Id, DogmaAttributeId.MaxVelocity),
                fit.Graph.Read(f.Pilot, DogmaAttributeId.MissileDamageMultiplier)
            );
        }
    }

    [Fact]
    public void NativeDestroyFittingRemovesTheRigAndNotifiesCalibrationAndAttributes()
    {
        using Fixture f = new();
        long rig = f.Base.Grant(31794);
        var pilot = f.Base.Db.Store.FindPilot(1000001)!;
        using var scope = f.Base.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(f.Base.Db.Store, selected, f.Base.Clock, new(), f.Base.Db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var handler = scope.ServiceProvider.GetRequiredService<ShipRigHandler>();
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "@ship", "DestroyFitting", [rig]));
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "@ship", "DestroyFitting", [rig, 1L]));
        Assert.Empty(context.Notifications);
        Assert.Null(
            router.CallWire(
                context,
                "@ship",
                "Add",
                new object?[] { rig, f.Base.StationId },
                _ => { },
                new() { ["flag"] = (long)InventoryFlag.RigSlot0 }
            )
        );
        Assert.Equal(75, f.Attributes[DogmaAttributeId.CalibrationLoad]);
        Assert.Contains(context.Notifications, n => n is ItemChanged);
        Assert.Contains(context.Notifications, n => n is ModuleAttributesChanged);
        var info = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(router.CallWire(context, "dogmaIM", "ItemGetInfo", new object?[] { rig }, _ => { })).State
        );
        var attributes = Assert.IsType<Dictionary<long, object?>>(info["attributes"]);
        Assert.Equal(75d, attributes[(long)DogmaAttributeId.CalibrationCost]);
        context.Notifications.Clear();
        Assert.Null(router.CallWire(context, "@ship", "DestroyFitting", new object?[] { rig }, _ => { }));
        var removed = Assert.Single(context.Notifications.OfType<ItemChanged>());
        Assert.Equal((long)InventoryLocation.Graveyard, removed.Item.LocationId);
        Assert.Contains(context.Notifications, n => n is ModuleAttributesChanged);
        Assert.Equal(0, f.Attributes[DogmaAttributeId.CalibrationLoad]);
        Assert.Empty(f.Base.Inventory.List(f.Pilot, f.Ship));
    }

    [Fact]
    public void ShipLossAlwaysDestroysInstalledRigsWhileUnfittedRigsCanDrop()
    {
        using Fixture f = new();
        long installed = f.Fit(31794);
        long cargo = f.Base.Grant(31794, cargo: true);
        f.Base.Db.Store.SetPosition(f.Ship, SpacePosition.Zero, 30000142);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(f.Base.CatalogPath);
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == f.Pilot);
            ShipLossPersistence.Apply(
                world,
                catalog,
                pilot,
                null,
                f.Ship,
                30000142,
                pilot.ActiveShip.Position!,
                f.Base.Clock.Now,
                new SurvivingLoot(),
                new CombatParty(90000002, 90000002, 1000035, null, 601, null, 0, 1000)
            );
            world.SaveChanges();
            transaction.Commit();
        }
        _ = WorldInitialization.Open(f.Base.Db.Path);
        using WorldDbContext check = new(f.Base.Db.Path);
        Assert.Null(check.Set<OwnedItem>().Find(installed));
        var dropped = check.Set<OwnedItem>().Find(cargo)!;
        Assert.Equal((int)InventoryFlag.Cargo, dropped.FlagId);
        Assert.NotEqual(f.Ship, dropped.LocationId);
        var report = check.Set<ShipLoss>().Single(l => l.PilotId == f.Pilot);
        Assert.DoesNotContain(installed, System.Text.Json.JsonSerializer.Deserialize<long[]>(report.DroppedItemIdsJson)!);
        Assert.Contains(cargo, System.Text.Json.JsonSerializer.Deserialize<long[]>(report.DroppedItemIdsJson)!);
    }

    private sealed class SurvivingLoot : IShipLootRoll
    {
        public bool Survives() => true;
    }
}
