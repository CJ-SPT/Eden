using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FittingFixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class ShipHeatTests
{
    private const int Booster = 399;
    private const int Ibis = 601;
    private const int SmallAncillaryBooster = 32774;
    private const int NavyCapBooster25 = 33330;

    private sealed class Fixture : IDisposable
    {
        internal FittingFixture Fit { get; } = new();
        internal long Pilot { get; }
        internal long Ship { get; }
        internal long Module { get; }
        internal LiveWorld? Live { get; }
        internal bool Ancillary { get; }
        internal long Now => Fit.Clock.Now;
        internal IPilotShipHeat Heat => Fit.Db.Services.GetRequiredService<IPilotShipHeat>();
        internal PilotShieldBoostStore Boosting => new(new(Fit.Db.Path));

        internal Fixture(bool live = true, Action<SqliteConnection, string>? persist = null, bool ancillary = false)
        {
            Ancillary = ancillary;
            PassiveCatalogFixture.LoadOverload(Fit.CatalogPath);
            if (ancillary)
            {
                PassiveCatalogFixture.LoadAncillaryRepairs(Fit.CatalogPath, Ibis);
            }
            Fit.Train(OverloadRules.ThermodynamicsSkill, 3);
            Pilot = Fit.PilotId;
            Ship = Fit.ShipId;
            Module = Fit.Fit(ancillary ? SmallAncillaryBooster : Booster);
            if (ancillary)
            {
                long fuel = Fit.Grant(NavyCapBooster25, 50);
                Fit.Db.Services.GetRequiredService<IPilotAmmunition>().Load(Pilot, Ship, [Module], [fuel], Fit.StationId, Now);
                Fit.Clock.Milliseconds += 60000;
            }
            using (WorldDbContext world = new(Fit.Db.Path))
            {
                var health = world.Set<ShipHealthState>().Find(Ship) ?? world.Add(new ShipHealthState { ShipId = Ship }).Entity;
                health.ShieldCharge = 0;
                health.UpdatedAt = Now;
                world.SaveChanges();
            }
            Fit.Db.Store.SetPosition(Ship, SpacePosition.Zero);
            Live = live ? new(new(Fit.Db.Path), Fit.Clock, _ => { }, persist) : null;
        }

        internal ShieldBoost Pay() => Boosting.BoostOverloaded(Pilot, Module, Now, Ancillary);

        internal ShipHeatSnapshot Read() => Heat.Read(Pilot, Ship, Now);

        internal void Advance(double milliseconds) => Fit.Clock.Milliseconds += checked((long)Math.Ceiling(milliseconds));

        public void Dispose()
        {
            Live?.Dispose();
            Fit.Dispose();
        }
    }

    [Fact]
    public void InternalOverloadedBoostPaysAndRegistersItsExactSkilledDeadlineTogether()
    {
        using Fixture f = new();
        var item = f.Fit.Inventory.List(f.Pilot, f.Ship).Single(m => m.Id == f.Module);
        var normal = f.Fit.Fitting.ModuleAttributes(f.Pilot, item);
        double capacitor = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        var paid = f.Pay();
        using CatalogDbContext catalog = new(f.Fit.CatalogPath);
        var profile = OverloadCatalog.Definition(catalog, Booster)!;
        var values = catalog.FittingAttributes(Booster);
        foreach (var bonus in profile.Bonuses)
        {
            double expected = normal[bonus.Attribute] * (1 + values[bonus.SourceAttribute] / 100);
            Assert.Equal(
                expected,
                bonus.Attribute == DogmaAttributeId.Duration ? paid.Booster.DurationMilliseconds : paid.Booster.ShieldAmount,
                8
            );
        }
        Assert.Equal(capacitor - paid.Booster.CapacitorNeed, paid.Capacitor, 8);
        var state = f.Read();
        var cycle = Assert.Single(state.Cycles);
        Assert.Equal(f.Module, cycle.ModuleId);
        Assert.Equal(f.Now, cycle.StartedAt);
        Assert.Equal(f.Now + (long)Math.Ceiling(paid.Booster.DurationMilliseconds * TimeSpan.TicksPerMillisecond), cycle.CompletesAt);
        Assert.Equal(normal[DogmaAttributeId.HeatAbsorption], cycle.Absorption);
        Assert.Equal(0, state.Heat(ModuleRack.Medium));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(cycle.CompletesAt, saved.Set<ModuleCooldown>().Find(f.Module)!.ReadyAt);
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(f.Ship)!.Charge);
        Assert.Empty(saved.Set<SavedModuleCondition>());
    }

    [Fact]
    public void AFailedHeatWriteRollsBackTheBoostCapacitorAndCooldown()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectHeat BEFORE INSERT ON WorldShipHeat BEGIN SELECT RAISE(ABORT,'test heat failure'); END"
            );
        }
        double capacitor = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        var error = Assert.Throws<DbUpdateException>(() => f.Pay());
        Assert.IsType<SqliteException>(error.InnerException);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Equal(0, world.Set<ShipHealthState>().Find(f.Ship)!.ShieldCharge);
            Assert.Empty(world.Set<ModuleCooldown>());
            Assert.Empty(world.Set<SavedShipHeat>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectHeat");
        }
        Assert.Equal(capacitor, f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now), 8);
        Assert.True(f.Read().Idle);
    }

    [Fact]
    public void PaidHeatAdvancesUnderTheWorldOwnerBeforeAnyHeatRead()
    {
        using Fixture f = new();
        f.Pay();
        f.Advance(1000);
        using var metrics = SessionPerformance.Begin("unobserved paid heat", _ => { }, force: true)!;
        using (f.Live!.Enter())
        {
            var snapshot = Assert.IsType<ShipHeatCheckpoint>(f.Live.Heat.Capture());
            var state = Assert.Single(snapshot.Ships).State;
            Assert.True(state.Heat(ModuleRack.Medium) > 0);
            Assert.Single(state.Cycles);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public async Task RestartWarmsPaidHeatWithoutRequiringAClientRead()
    {
        using Fixture f = new();
        f.Pay();
        await f.Live!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Fit.Db.Path)!, "heat-unobserved.sqlite");
        File.Copy(f.Fit.Db.Path, copy);
        WorldInitialization.Initialize(new(copy), f.Fit.Clock);
        using LiveWorld reopened = new(new(copy), f.Fit.Clock, _ => { });
        f.Advance(1000);
        using var metrics = SessionPerformance.Begin("unobserved restored heat", _ => { }, force: true)!;
        using (reopened.Enter())
        {
            var snapshot = Assert.IsType<ShipHeatCheckpoint>(reopened.Heat.Capture());
            var state = Assert.Single(snapshot.Ships).State;
            Assert.True(state.Heat(ModuleRack.Medium) > 0);
            Assert.Single(state.Cycles);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void WarmFramesHeatExpireAndCoolWithoutSqlConnectionsOrFittingCalculations()
    {
        using Fixture f = new();
        var paid = f.Pay();
        var original = f.Read();
        double heat = 0;
        using (var metrics = SessionPerformance.Begin("warm heat frames", _ => { }, force: true)!)
        {
            for (int step = 0; step < 100; step++)
            {
                f.Fit.Clock.Milliseconds++;
                var state = f.Read();
                Assert.True(state.Heat(ModuleRack.Medium) >= heat);
                heat = state.Heat(ModuleRack.Medium);
            }
            f.Advance(paid.Booster.DurationMilliseconds);
            var completed = f.Read();
            Assert.Empty(completed.Cycles);
            Assert.True(completed.Heat(ModuleRack.Medium) > heat);
            f.Fit.Clock.Milliseconds += 10000;
            var cooled = f.Read();
            Assert.Empty(cooled.Cycles);
            Assert.InRange(cooled.Heat(ModuleRack.Medium), 0, completed.Heat(ModuleRack.Medium));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Equal(0, original.Heat(ModuleRack.Medium));
        Assert.Single(original.Cycles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DockingAndBurnoutCancelContributionsAtTheBoundaryWithoutHealingHeatOrRefundingPayment(bool burnout)
    {
        using Fixture f = new();
        var paid = f.Pay();
        f.Read();
        f.Advance(paid.Booster.DurationMilliseconds / 2);
        var hot = f.Read();
        if (burnout)
        {
            using WorldDbContext world = new(f.Fit.Db.Path);
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(f.Fit.CatalogPath);
            ModuleConditionPersistence.ApplyDamage(world, catalog, f.Module, 40);
            world.SaveChanges();
            transaction.Commit();
        }
        else
        {
            f.Fit.Db.Store.SetPosition(f.Ship, null);
        }
        var stopped = f.Read();
        Assert.Empty(stopped.Cycles);
        Assert.Equal(hot.Heat(ModuleRack.Medium), stopped.Heat(ModuleRack.Medium), 8);
        f.Fit.Db.Store.SetPosition(f.Ship, null);
        f.Fit.Clock.Milliseconds += 1000;
        Assert.True(f.Read().Heat(ModuleRack.Medium) < stopped.Heat(ModuleRack.Medium));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(f.Ship)!.Charge);
        Assert.True(saved.Set<ModuleCooldown>().Find(f.Module)!.ReadyAt > hot.UpdatedAt);
        Assert.Equal(!burnout, saved.Set<OwnedItem>().Find(f.Module)!.Online);
    }

    [Fact]
    public void AncillaryOverloadConsumesItsChargeAndCancellationCannotRefundIt()
    {
        using Fixture f = new(ancillary: true);
        var ammo = f.Fit.Db.Services.GetRequiredService<IPilotAmmunition>();
        int before = Assert.Single(ammo.Read(f.Pilot, f.Ship)).Quantity;
        var paid = f.Pay();
        Assert.NotNull(paid.Magazine);
        Assert.Equal(before - 1, paid.Magazine.Quantity);
        Assert.Equal(0, paid.Booster.CapacitorNeed);
        var key = Assert.Single(f.Read().Cycles).Key;
        f.Advance(100);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            ShipHeatPersistence.CancelPaidCycle(world, f.Pilot, f.Ship, key, f.Now);
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.Empty(f.Read().Cycles);
        Assert.Equal(before - 1, Assert.Single(ammo.Read(f.Pilot, f.Ship)).Quantity);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(key.CompletesAt, saved.Set<ModuleCooldown>().Find(f.Module)!.ReadyAt);
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(f.Ship)!.Charge);
    }

    [Fact]
    public void CatalogChangesRebaseAtTheBoundaryAndCannotDiscardDirtyHeat()
    {
        using Fixture f = new();
        var paid = f.Pay();
        f.Read();
        f.Advance(paid.Booster.DurationMilliseconds / 3);
        var before = f.Read();
        var profile = before.Racks.Single(r => r.Profile.Rack == ModuleRack.Medium).Profile;
        double rate = Assert.Single(before.Cycles).Absorption;
        using (CatalogDbContext catalog = new(f.Fit.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                UPDATE TypeAttribute SET Value={profile.Generation * 2}
                WHERE ItemTypeId=601 AND AttributeId={(int)DogmaAttributeId.HeatGenerationMultiplier}
                """
            );
        }
        var changed = f.Read();
        Assert.Equal(before.Heat(ModuleRack.Medium), changed.Heat(ModuleRack.Medium), 8);
        Assert.Equal(profile.Generation * 2, changed.Racks.Single(r => r.Profile.Rack == ModuleRack.Medium).Profile.Generation);
        f.Fit.Clock.Milliseconds += 100;
        Assert.Equal(
            HeatRules.Calculate(profile with { Generation = profile.Generation * 2 }, before.Heat(ModuleRack.Medium), rate, 100),
            f.Read().Heat(ModuleRack.Medium),
            8
        );
    }

    [Fact]
    public async Task DetachedHeatCaptureKeepsItsGenerationAndRestartSettlesTheSavedDeadlineOnce()
    {
        using Fixture f = new();
        var paid = f.Pay();
        f.Read();
        f.Advance(paid.Booster.DurationMilliseconds / 3);
        var first = f.Read();
        WorldSnapshot snapshot;
        using (var metrics = SessionPerformance.Begin("heat capture", _ => { }, force: true)!)
        {
            snapshot = Assert.IsType<WorldSnapshot>(f.Live!.CaptureSnapshot());
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (snapshot)
        {
            f.Advance(paid.Booster.DurationMilliseconds / 3);
            var later = f.Read();
            Assert.True(later.Heat(ModuleRack.Medium) > first.Heat(ModuleRack.Medium));
            snapshot.Project();
            using WorldDbContext captured = new(snapshot.Database);
            var state = ShipHeatPersistence.Decode(captured.Set<SavedShipHeat>().Find(f.Ship)!.StateJson);
            Assert.Equal(first.UpdatedAt, state.UpdatedAt);
            Assert.Equal(first.Heat(ModuleRack.Medium), state.Heat(ModuleRack.Medium));
        }
        await f.Live!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Fit.Db.Path)!, "heat-reopened.sqlite");
        File.Copy(f.Fit.Db.Path, copy);
        f.Advance(paid.Booster.DurationMilliseconds);
        WorldInitialization.Initialize(new(copy), f.Fit.Clock);
        PilotShipHeatStore reopened = new(new(copy));
        var finished = reopened.Read(f.Pilot, f.Ship, f.Now);
        Assert.Empty(finished.Cycles);
        Assert.Equal(finished.Heat(ModuleRack.Medium), reopened.Read(f.Pilot, f.Ship, f.Now).Heat(ModuleRack.Medium));
        using WorldDbContext saved = new(copy);
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(f.Ship)!.Charge);
        Assert.Single(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public async Task FailedCheckpointWritingRetainsResidentHeatForTheNextSave()
    {
        int attempts = 0;
        using Fixture f = new(
            persist: (snapshot, path) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new IOException("injected write failure");
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            }
        );
        var paid = f.Pay();
        f.Read();
        f.Advance(paid.Booster.DurationMilliseconds / 2);
        var before = f.Read();
        await Assert.ThrowsAsync<IOException>(() => f.Live!.FlushAsync());
        Assert.Equal(before.Heat(ModuleRack.Medium), f.Read().Heat(ModuleRack.Medium));
        await f.Live!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Fit.Db.Path)!, "heat-retry.sqlite");
        File.Copy(f.Fit.Db.Path, copy);
        using WorldDbContext saved = new(copy);
        Assert.Equal(
            before.Heat(ModuleRack.Medium),
            ShipHeatPersistence.Decode(saved.Set<SavedShipHeat>().Find(f.Ship)!.StateJson).Heat(ModuleRack.Medium)
        );
    }

    [Fact]
    public void AFailedCommandCannotRollBackPreviouslyAccruedResidentHeat()
    {
        using Fixture f = new();
        var paid = f.Pay();
        f.Read();
        f.Advance(paid.Booster.DurationMilliseconds / 2);
        var before = f.Read();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Set<SavedShipHeat>().Find(f.Ship)!.StateJson = System.Text.Json.JsonSerializer.Serialize(
                HeatCycleRules.Create(before.Racks.Select(r => r.Profile).ToArray(), f.Now)
            );
            world.SaveChanges();
            // A failed player command discards this synthetic reset, while the
            // separate native projection before its transaction remains paid.
        }
        var after = f.Read();
        Assert.Equal(before.Heat(ModuleRack.Medium), after.Heat(ModuleRack.Medium));
        Assert.Equal(Assert.Single(before.Cycles).Key, Assert.Single(after.Cycles).Key);
    }

    [Fact]
    public void AStaleWorldCancellationCannotStopTheNextPaidContributionOrRefundItsCost()
    {
        using Fixture f = new();
        var first = f.Pay();
        var old = Assert.Single(f.Read().Cycles).Key;
        f.Advance(first.Booster.DurationMilliseconds);
        Assert.Empty(f.Read().Cycles);
        var next = f.Pay();
        var current = Assert.Single(f.Read().Cycles).Key;
        f.Advance(next.Booster.DurationMilliseconds / 2);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            ShipHeatPersistence.CancelPaidCycle(world, f.Pilot, f.Ship, old, f.Now);
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.Equal(current, Assert.Single(f.Read().Cycles).Key);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(next.Capacitor, saved.Set<ShipEnergy>().Find(f.Ship)!.Charge);
        Assert.Equal(current.CompletesAt, saved.Set<ModuleCooldown>().Find(f.Module)!.ReadyAt);
    }

    [Fact]
    public void HeatProjectionFailureRollsBackAlreadyProjectedCombatAndCanRetryTheSameDetachedGeneration()
    {
        using Eden.Protocol.Tests.Ammunition.AmmunitionTests.Fixture f = new(liveWorld: true);
        var database = new WorldDatabase(f.Db.Path);
        PassiveCatalogFixture.LoadOverload(database.CatalogPath);
        var pilot = f.Pilot;
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            var profiles = ShipHeatPersistence.Profiles(PilotFittingStore.ReadAttributes(catalog, pilot.ShipTypeId));
            var state = HeatCycleRules.Restore(
                new(f.Clock.Now, profiles.Select(p => new HeatRackSnapshot(p, p.Rack == ModuleRack.Medium ? 30 : 0)).ToArray(), [])
            );
            world.Add(new SavedShipHeat { ShipId = pilot.ShipId, StateJson = System.Text.Json.JsonSerializer.Serialize(state) });
            world.SaveChanges();
        }
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        heat.Read(pilot.PilotId, pilot.ShipId, f.Clock.Now);
        f.Fire();
        f.Clock.Milliseconds += 1000;
        var current = heat.Read(pilot.PilotId, pilot.ShipId, f.Clock.Now);
        using var snapshot = Assert.IsType<WorldSnapshot>(LiveWorld.Find(f.Db.Path)!.CaptureSnapshot());
        double oldHeat;
        using (WorldDbContext captured = new(snapshot.Database))
        {
            oldHeat = ShipHeatPersistence.Decode(captured.Set<SavedShipHeat>().Find(pilot.ShipId)!.StateJson).Heat(ModuleRack.Medium);
            Assert.Equal(200, captured.Set<OwnedItem>().Single(i => i.LocationId == f.Gun).Quantity);
        }
        using (var command = snapshot.Database.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER RejectHeat BEFORE UPDATE ON WorldShipHeat BEGIN SELECT RAISE(ABORT,'heat'); END";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => snapshot.Project());
        using (WorldDbContext captured = new(snapshot.Database))
        {
            Assert.Equal(200, captured.Set<OwnedItem>().Single(i => i.LocationId == f.Gun).Quantity);
            Assert.Equal(
                oldHeat,
                ShipHeatPersistence.Decode(captured.Set<SavedShipHeat>().Find(pilot.ShipId)!.StateJson).Heat(ModuleRack.Medium)
            );
        }
        using (var command = snapshot.Database.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER RejectHeat";
            command.ExecuteNonQuery();
        }
        snapshot.Project();
        using WorldDbContext applied = new(snapshot.Database);
        Assert.Equal(199, applied.Set<OwnedItem>().Single(i => i.LocationId == f.Gun).Quantity);
        Assert.Equal(
            current.Heat(ModuleRack.Medium),
            ShipHeatPersistence.Decode(applied.Set<SavedShipHeat>().Find(pilot.ShipId)!.StateJson).Heat(ModuleRack.Medium)
        );
    }

    [Fact]
    public void UnpaidAndOrdinaryActivationsCannotRegisterOverloadHeatAndForeignReadsAreRejected()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        using (CatalogDbContext catalog = new(f.Fit.CatalogPath))
        {
            var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.Pilot);
            var selected = FittingCalculation.Read(world, catalog, pilot, overloadedModules: new HashSet<long> { f.Module });
            Assert.Throws<InvalidOperationException>(() =>
                ShipHeatPersistence.BeginPaidCycle(world, catalog, selected, f.Module, f.Now, f.Now + TimeSpan.TicksPerSecond)
            );
            using var transaction = world.Database.BeginTransaction();
            Assert.Throws<InvalidDataException>(() =>
                ShipHeatPersistence.BeginPaidCycle(world, catalog, selected, f.Module, f.Now, f.Now + TimeSpan.TicksPerSecond)
            );
        }
        f.Boosting.Boost(f.Pilot, f.Module, f.Now);
        Assert.True(f.Read().Idle);
        Assert.Throws<InvalidDataException>(() => f.Heat.Read(f.Pilot + 1, f.Ship, f.Now));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedShipHeat>());
    }

    [Fact]
    public void IdleInvalidationDoesNotStartBackgroundSqlOrCreateHeatRows()
    {
        using Fixture f = new();
        Assert.True(f.Read().Idle);
        f.Fit.Train(OverloadRules.ThermodynamicsSkill, 5);
        using (var metrics = SessionPerformance.Begin("idle heat invalidation", _ => { }, force: true)!)
        {
            using var ownership = f.Live!.Enter();
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.True(f.Read().Idle);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedShipHeat>());
    }

    [Fact]
    public void ReadingAnUnchangedSavedIdleShipDoesNotQueueAProjection()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        using (CatalogDbContext catalog = new(f.Fit.CatalogPath))
        {
            var profiles = ShipHeatPersistence.Profiles(PilotFittingStore.ReadAttributes(catalog, Ibis));
            world.Add(
                new SavedShipHeat { ShipId = f.Ship, StateJson = ShipHeatPersistence.Encode(HeatCycleRules.Create(profiles, f.Now)) }
            );
            world.SaveChanges();
        }
        Assert.True(f.Read().Idle);
        Assert.Null(f.Live!.Heat.Capture());
        f.Advance(1000);
        using var metrics = SessionPerformance.Begin("saved idle heat", _ => { }, force: true)!;
        Assert.True(f.Read().Idle);
        Assert.Null(f.Live.Heat.Capture());
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void ColdForeignReadsRejectBeforeFittingAndInvalidClocksNeverReturnCachedValues()
    {
        using Fixture f = new();
        using (var metrics = SessionPerformance.Begin("foreign heat", _ => { }, force: true)!)
        {
            Assert.Throws<InvalidDataException>(() => f.Heat.Read(f.Pilot + 1, f.Ship, f.Now));
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.True(f.Read().Idle);
        Assert.Throws<InvalidDataException>(() => f.Heat.Read(f.Pilot, f.Ship, -1));
    }

    [Fact]
    public void OlderSavesGainAnEmptyHeatTableAndMalformedSavedTimelinesFailInitialization()
    {
        using Fixture f = new(live: false);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldShipHeat; PRAGMA user_version=106");
        }
        WorldInitialization.Initialize(new(f.Fit.Db.Path), f.Fit.Clock);
        Assert.True(f.Read().Idle);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Empty(world.Set<SavedShipHeat>());
            world.Add(new SavedShipHeat { ShipId = f.Ship, StateJson = "{\"UpdatedAt\":-1,\"Racks\":[],\"Cycles\":[]}" });
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => WorldInitialization.Initialize(new(f.Fit.Db.Path), f.Fit.Clock));
    }
}
