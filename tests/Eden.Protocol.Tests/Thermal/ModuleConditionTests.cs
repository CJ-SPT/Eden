using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Repair;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class ModuleConditionTests
{
    // Representative native catalog profiles: local shield/armor repair,
    // active resistance and passive damage control, all with 40 structure HP.
    private const int ShieldBooster = 399;
    private const int ArmorRepairer = 523;
    private const int ShieldHardener = 2293;
    private const int DamageControl = 2048;
    private const int PulseLaser = 450;

    private static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            "INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[13,17]}}')"
        );
        using WorldDbContext world = new(f.Db.Path);
        world.Add(new PilotWallet { PilotId = f.PilotId, Cents = 100000 });
        world.SaveChanges();
        return f;
    }

    private static IPilotRepair Repairs(Fixture f) => f.Db.Services.GetRequiredService<IPilotRepair>();

    private static ModuleConditionChange Damage(Fixture f, long module, double amount)
    {
        using WorldDbContext world = new(f.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var change = ModuleConditionPersistence.ApplyDamage(world, catalog, module, amount);
        world.SaveChanges();
        transaction.Commit();
        return change;
    }

    private static double SavedDamage(Fixture f, long module)
    {
        using WorldDbContext world = new(f.Db.Path);
        return world.Set<SavedModuleCondition>().Find(module)?.Damage ?? 0;
    }

    [Fact]
    public void PaidModuleRepairQuotesActualDamageAndKeepsConditionAfterRestart()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        Assert.Equal(10, Damage(f, module, 10).Current.Damage);
        var quote = Assert.Single(Repairs(f).Quotes(f.PilotId, (int)f.StationId, [module])[module]);
        Assert.Equal(10, quote.Damage);
        Assert.Equal(40, quote.MaxHealth);
        Assert.Empty(Repairs(f).Payments(f.PilotId));
        var partial = Repairs(f).Repair(f.PilotId, (int)f.StationId, [module], 500, f.Clock.Now);
        Assert.Equal(99500, partial.BalanceCents);
        Assert.Empty(partial.Ships);
        Assert.Equal(new ModuleRepair(module, ShieldBooster, 5, 5), Assert.Single(partial.Modules));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(5, SavedDamage(f, module));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var details = JsonSerializer.Deserialize<StationRepairDetails>(world.Set<StationRepairPayment>().Single().RepairsJson)!;
            Assert.Equal(partial.Modules, details.Modules);
            Assert.True(world.Set<OwnedItem>().Find(module)!.Online);
        }
        var full = Repairs(f).Repair(f.PilotId, (int)f.StationId, [module], 100000, f.Clock.Now);
        Assert.Equal(99000, full.BalanceCents);
        Assert.Equal(0, Assert.Single(full.Modules).RemainingDamage);
        Assert.Empty(Repairs(f).Repair(f.PilotId, (int)f.StationId, [module], 100000, f.Clock.Now).Modules);
        Assert.Equal(2, Repairs(f).Payments(f.PilotId).Count);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<SavedModuleCondition>());
    }

    [Fact]
    public void MixedRepairUsesOnePaidFractionWithoutRefillingOtherResources()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        long ship = f.ShipId;
        Damage(f, module, 20);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var health = world.Set<ShipHealthState>().Find(ship) ?? world.Add(new ShipHealthState { ShipId = ship }).Entity;
            health.ShieldCharge = 7;
            health.ArmorDamage = 10;
            health.StructureDamage = 20;
            health.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        var result = Repairs(f).Repair(f.PilotId, (int)f.StationId, [ship, module], 1000, f.Clock.Now);
        Assert.Equal(new ShipRepair(ship, 2, 4), Assert.Single(result.Ships));
        Assert.Equal(new ModuleRepair(module, ShieldBooster, 4, 16), Assert.Single(result.Modules));
        Assert.Equal(99000, result.BalanceCents);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(7, saved.Set<ShipHealthState>().Find(ship)!.ShieldCharge);
        Assert.Equal(8, saved.Set<ShipHealthState>().Find(ship)!.ArmorDamage);
        Assert.Equal(16, saved.Set<ShipHealthState>().Find(ship)!.StructureDamage);
        var details = JsonSerializer.Deserialize<StationRepairDetails>(saved.Set<StationRepairPayment>().Single().RepairsJson)!;
        Assert.Equal(result.Ships, details.Ships);
        Assert.Equal(result.Modules, details.Modules);
    }

    [Theory]
    [InlineData(ShieldBooster, true)]
    [InlineData(ArmorRepairer, false)]
    [InlineData(ShieldHardener, true)]
    [InlineData(DamageControl, true)]
    [InlineData(PulseLaser, true)]
    public void BurnoutOfflinesEveryRackIncludingPassiveAndAlreadyOfflineModules(int type, bool online)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type, online);
        var burned = Damage(f, module, 1000);
        Assert.Equal(40, burned.Current.Damage);
        Assert.True(burned.Current.BurnedOut);
        Assert.Equal(online, burned.WentOffline);
        Assert.False(Damage(f, module, 1).WentOffline);
        Assert.Throws<InvalidDataException>(() => f.Fitting.SetOnline(f.PilotId, f.ShipId, module, true));
        Repairs(f).Repair(f.PilotId, (int)f.StationId, [module], 100000, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.False(world.Set<OwnedItem>().Find(module)!.Online);
        }
        f.Fitting.SetOnline(f.PilotId, f.ShipId, module, true);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.True(saved.Set<OwnedItem>().Find(module)!.Online);
    }

    [Fact]
    public void BurnoutRemovesPassiveAndPaidActiveResistanceFromTheCommittedGraph()
    {
        using Fixture f = Prepare();
        long ship = f.ShipId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var baseline = f.Fitting.ShipAttributes(f.PilotId);
        long passive = f.Fit(DamageControl);
        long active = f.Fit(ShieldHardener);
        Assert.True(
            f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.StructureEmResonance] < baseline[DogmaAttributeId.StructureEmResonance]
        );
        Damage(f, passive, 40);
        Assert.Equal(
            baseline[DogmaAttributeId.StructureEmResonance],
            f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.StructureEmResonance]
        );
        f.Db.Store.SetPosition(ship, SpacePosition.Zero);
        var paid = f
            .Db.Services.GetRequiredService<IPilotHardening>()
            .Activate(f.PilotId, active, DogmaEffect.ActiveShieldResistance, f.Clock.Now);
        Assert.True(paid.ShipAttributes[DogmaAttributeId.ShieldEmResonance] < baseline[DogmaAttributeId.ShieldEmResonance]);
        Damage(f, active, 40);
        Assert.Equal(baseline[DogmaAttributeId.ShieldEmResonance], f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldEmResonance]);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(ship)!.Charge);
        Assert.True(saved.Set<ModuleCooldown>().Find(active)!.ReadyAt > f.Clock.Now);
    }

    [Fact]
    public void BurnoutCancelsThePaidLocalRepairWithoutRefundingCapacitorOrCooldown()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ArmorRepairer);
        long ship = f.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var health = world.Set<ShipHealthState>().Find(ship) ?? world.Add(new ShipHealthState { ShipId = ship }).Entity;
            health.ArmorDamage = 50;
            health.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(ship, SpacePosition.Zero);
        var paid = f.Db.Services.GetRequiredService<IPilotLocalRepairing>().Start(f.PilotId, module, f.Clock.Now);
        Damage(f, module, 40);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<PendingLocalRepair>());
            Assert.Equal(paid.Capacitor, world.Set<ShipEnergy>().Find(ship)!.Charge);
            Assert.Equal(paid.Schedule.CompletesAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        }
        f.Clock.Milliseconds += 10000;
        var current = f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);
        Assert.Equal(50, current.Defense.Armor - current.Remaining.Armor, 8);
        Assert.Throws<InvalidDataException>(() =>
            f.Db.Services.GetRequiredService<IPilotLocalRepairing>().Start(f.PilotId, module, f.Clock.Now)
        );
    }

    [Fact]
    public void RolledBackBurnoutPreservesActivityOnlineStateAndCondition()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldHardener);
        long ship = f.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new ActiveFittedModule { ModuleId = module, ShipId = ship });
            world.Add(new ModuleCooldown { ModuleId = module, ReadyAt = f.Clock.Now + TimeSpan.TicksPerMinute });
            world.SaveChanges();
        }
        using ResidentFitCache cache = new(new(f.Db.Path));
        var item = f.Inventory.List(f.PilotId, ship).Single(i => i.Id == module);
        var before = f.Fitting.ModuleAttributes(f.PilotId, item);
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            ModuleConditionPersistence.ApplyDamage(world, catalog, module, 40);
            world.SaveChanges();
            Assert.Empty(world.Set<ActiveFittedModule>());
            // Disposing the owning transaction rolls back damage and activity.
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedModuleCondition>());
            Assert.True(world.Set<OwnedItem>().Find(module)!.Online);
            Assert.Single(world.Set<ActiveFittedModule>());
            Assert.Single(world.Set<ModuleCooldown>());
        }
        Assert.Equal(before.OrderBy(a => a.Key), f.Fitting.ModuleAttributes(f.PilotId, item).OrderBy(a => a.Key));
    }

    [Fact]
    public void UnfittingAndOwnershipTransferKeepDamageAndRepackagingCannotEraseIt()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        long station = f.StationId;
        Damage(f, module, 15);
        f.Inventory.Move(f.PilotId, station, new([module], f.ShipId, (int)InventoryFlag.Hangar, null));
        Assert.Equal(15, SavedDamage(f, module));
        var repack = f.Db.Services.GetRequiredService<IPilotRepackaging>();
        Assert.Throws<InvalidDataException>(() => repack.Repackage(f.PilotId, (int)station, [new(module, station)]));
        var wing = f.Db.Store.FindPilot(1000002)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.OwnerId = wing.PilotId;
            world.SaveChanges();
            Assert.Equal(0, ModuleConditionPersistence.OwnedDamage(world, f.PilotId, module));
            Assert.Equal(15, ModuleConditionPersistence.OwnedDamage(world, wing.PilotId, module));
            world.Set<OwnedItem>().Find(module)!.OwnerId = f.PilotId;
            world.SaveChanges();
        }
        Repairs(f).Repair(f.PilotId, (int)station, [module], 100000, f.Clock.Now);
        Assert.Single(repack.Repackage(f.PilotId, (int)station, [new(module, station)]));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.False(saved.Set<OwnedItem>().Find(module)!.Singleton);
        Assert.Empty(saved.Set<SavedModuleCondition>());
    }

    [Fact]
    public void RejectedPaymentsAndUnavailableItemsLeaveDamageAndWalletUnchanged()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        int station = (int)f.StationId;
        Damage(f, module, 10);
        Assert.Empty(Repairs(f).Repair(f.PilotId, station, [module], 0, f.Clock.Now).Modules);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(f.PilotId)!.Cents = 1;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Repairs(f).Repair(f.PilotId, station, [module], 1000, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => Repairs(f).Repair(f.PilotId, station + 1, [module], 1000, f.Clock.Now));
        var wing = f.Db.Store.FindPilot(1000002)!;
        Assert.Throws<InvalidDataException>(() => Repairs(f).Quotes(wing.PilotId, station, [module]));
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => Repairs(f).Repair(f.PilotId, station, [module], 1000, f.Clock.Now));
        Assert.Equal(10, SavedDamage(f, module));
        Assert.Empty(Repairs(f).Payments(f.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(1, saved.Set<PilotWallet>().Find(f.PilotId)!.Cents);
    }

    [Fact]
    public void NativePrimeAndStationRepairPublishModuleDamageWithoutRebuildingTheFit()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        long ship = f.ShipId;
        using ResidentFitCache cache = new(new(f.Db.Path));
        var item = f.Inventory.List(f.PilotId, ship).Single(i => i.Id == module);
        Assert.Equal(0, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.Damage]);
        Damage(f, module, 10);
        using (var metrics = SessionPerformance.Begin("condition", _ => { }, force: true)!)
        {
            Assert.Equal(10, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.Damage]);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>();
        Assert.Equal(10, builder.PrimeModule(pilot, item).Attributes[DogmaAttributeId.Damage]);
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), f.Db.Objects),
            2,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var handler = scope.ServiceProvider.GetRequiredService<RepairHandler>();
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "repairSvc", "RepairItemsInStation", [new List<object?> { module }, 5d])
        );
        var change = Assert.Single(Assert.Single(context.Notifications.OfType<ModuleAttributesChanged>()).Changes);
        Assert.Equal(new DogmaItemKey.Item(module), change.Item);
        Assert.Equal(DogmaAttributeId.Damage, change.Attribute);
        Assert.Equal(5, change.Value);
        Assert.Equal(5, builder.PrimeModule(pilot, item).Attributes[DogmaAttributeId.Damage]);
    }

    [Fact]
    public async Task CheckpointAndRestartPreserveConditionAndReconcileBurnedOutOnlineState()
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            Damage(f, module, 40);
            await live.FlushAsync();
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(40, world.Set<SavedModuleCondition>().Find(module)!.Damage);
            // A saved online flag must never resurrect a burned-out module.
            world.Set<OwnedItem>().Find(module)!.Online = true;
            world.SaveChanges();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.False(reopened.Set<OwnedItem>().Find(module)!.Online);
        Assert.Equal(40, reopened.Set<SavedModuleCondition>().Find(module)!.Damage);
        Assert.Equal(0, reopened.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Theory]
    [InlineData(77)]
    [InlineData(105)]
    public void UpgradeCreatesHealthyConditionAndIdentityDeletionCascades(int version)
    {
        using Fixture f = Prepare();
        long module = f.Fit(ShieldBooster);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldModuleCondition");
            world.Database.ExecuteSqlRaw(version == 77 ? "PRAGMA user_version=77" : "PRAGMA user_version=105");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedModuleCondition>());
            Assert.True(world.Set<OwnedItem>().Find(module)!.Online);
            Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        }
        Damage(f, module, 10);
        using WorldDbContext deleted = new(f.Db.Path);
        deleted.Set<OwnedItem>().Where(i => i.Id == module).ExecuteDelete();
        Assert.Empty(deleted.Set<SavedModuleCondition>());
    }

    [Fact]
    public void DamageRequiresTheOwningTransactionAndAFittedAssembledModule()
    {
        using Fixture f = Prepare();
        long packaged = f.Grant(ShieldBooster);
        using WorldDbContext world = new(f.Db.Path);
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.Throws<InvalidOperationException>(() => ModuleConditionPersistence.ApplyDamage(world, catalog, packaged, 1));
        using var transaction = world.Database.BeginTransaction();
        Assert.Throws<InvalidDataException>(() => ModuleConditionPersistence.ApplyDamage(world, catalog, packaged, 1));
        Assert.Empty(world.Set<SavedModuleCondition>());
    }
}
