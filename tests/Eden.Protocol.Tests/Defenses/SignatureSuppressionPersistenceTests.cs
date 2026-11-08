using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Defenses;

public sealed partial class SignatureSuppressionOperationTests
{
    [Fact]
    public void SqlFallbackSignatureReadsCommitExpiryInsteadOfRevivingItAfterRollback()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        double passive = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius];
        var result = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(result.Schedule.ExpiresAt - f.Clock.Now));
        Assert.Equal(passive, f.Fitting.SignatureRadius(f.ShipId, Raven), 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Equal(result.Schedule.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void DeadlineInvalidationStillWorksWhenOptionalCombatAndReadViewsAreDisabled()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }, combatObjects: false, residentReads: false);
        double passive = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius];
        var result = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        Assert.True(f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius] < passive);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(result.Schedule.ExpiresAt - f.Clock.Now));
        Assert.Equal(passive, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
    }

    [Fact]
    public void QuantityUpdatesDoNotInvalidateTimedMembershipOrTriggerUnrelatedTackleReads()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        long cargo = f.Grant(Basic, 2, cargo: true);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        Store(f).Activate(f.PilotId, module, f.Clock.Now);
        long ship = f.ShipId;
        var tackle = f.Db.Services.GetRequiredService<IPilotTackle>();
        Assert.True(Store(f).Active(f.PilotId, ship, module, f.Clock.Now));
        tackle.Sweep(f.Clock.Now);
        _ = f.Fitting.ShipAttributes(f.PilotId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(cargo)!.Quantity--;
            world.SaveChanges();
        }
        using var metrics = SessionPerformance.Begin("unchanged timed membership", _ => { }, force: true);
        Assert.True(Store(f).Active(f.PilotId, ship, module, f.Clock.Now));
        tackle.Sweep(f.Clock.Now);
        _ = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void ActiveGroupLimitRejectsAnInconsistentStoredFitWithoutSpendingAgain()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        long duplicate = f.Grant(Basic);
        Space(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(duplicate)!;
            item.LocationId = f.ShipId;
            item.FlagId = (int)InventoryFlag.MediumSlot0 + 1;
            item.Singleton = true;
            item.Online = true;
            world.Add(
                new ActiveFittedModule
                {
                    ModuleId = duplicate,
                    ShipId = f.ShipId,
                    ExpiresAt = f.Clock.Now + TimeSpan.TicksPerSecond,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(5000, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(duplicate, Assert.Single(saved.Set<ActiveFittedModule>()).ModuleId);
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public void Version104AddsIndefiniteDefaultWithoutChangingPaidCooldownsOrInventory()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        var inventory = f.Inventory.List(f.PilotId, f.ShipId);
        long ready = f.Clock.Now + TimeSpan.FromMinutes(3).Ticks;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new ActiveFittedModule { ModuleId = module, ShipId = f.ShipId });
            world.Add(new ModuleCooldown { ModuleId = module, ReadyAt = ready });
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(
                "DROP INDEX IX_WorldActiveModule_ExpiresAt; ALTER TABLE WorldActiveModule DROP COLUMN ExpiresAt; PRAGMA user_version=104"
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            WorldMigration.Open(world, catalog, f.Clock);
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<ActiveFittedModule>().Find(module)!.ExpiresAt);
        Assert.Equal(ready, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(inventory, f.Inventory.List(f.PilotId, f.ShipId));
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void ExpiryWorksWithinTheSameOwnerLeaseAndNeverRevivesAfterClockRewind()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var ownership = live.Enter();
        double passive = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius];
        var activation = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        Assert.True(f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius] < passive);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(activation.Schedule.ExpiresAt - f.Clock.Now));
        Assert.Equal(passive, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        f.Clock.AdvanceWallTime(-TimeSpan.FromSeconds(1));
        Assert.Equal(passive, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        Assert.False(Store(f).Active(f.PilotId, f.ShipId, module, f.Clock.Now));
    }

    [Fact]
    public void RolledBackActivationMarkersDoNotPublishADeadlineOrPermanentBonus()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        double passive = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius];
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            world.Add(
                new ActiveFittedModule
                {
                    ModuleId = module,
                    ShipId = f.ShipId,
                    ExpiresAt = f.Clock.Now + TimeSpan.TicksPerSecond,
                }
            );
            world.SaveChanges();
            Assert.True(PilotFittingStore.ShipAttributes(world, catalog, f.PilotId)[DogmaAttributeId.SignatureRadius] < passive);
            transaction.Rollback();
        }
        Assert.Equal(passive, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        f.Clock.Milliseconds += 2000;
        Assert.Equal(passive, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<ActiveFittedModule>());
        Assert.Empty(check.Set<ModuleCooldown>());
    }

    [Fact]
    public void TimedExpiryPreservesOrdinaryIndefiniteActivations()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        long mwd = f.Fit(434);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new ActiveFittedModule { ModuleId = mwd, ShipId = f.ShipId });
            world.SaveChanges();
        }
        var result = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(result.Schedule.ExpiresAt - f.Clock.Now));
        _ = f.Fitting.ShipAttributes(f.PilotId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(mwd, Assert.Single(saved.Set<ActiveFittedModule>()).ModuleId);
        Assert.Equal(0, saved.Set<ActiveFittedModule>().Find(mwd)!.ExpiresAt);
    }
}
