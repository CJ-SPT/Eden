using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FittingFixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class PaidNaniteRepairTests
{
    private const int ShieldBooster = 399;

    private sealed class SurvivingLoot : IShipLootRoll
    {
        public bool Survives() => true;
    }

    private sealed class Fixture : IDisposable
    {
        internal FittingFixture Fit { get; } = new();
        internal long Pilot { get; }
        internal long Ship { get; }
        internal long Module { get; }
        internal long Second { get; }
        internal LiveWorld? Live { get; }
        internal long Now => Fit.Clock.Now;
        internal IPilotNaniteRepair Repairs => Fit.Db.Services.GetRequiredService<IPilotNaniteRepair>();

        internal Fixture(bool live = false, int paste = 50, bool second = false)
        {
            PassiveCatalogFixture.LoadOverload(Fit.CatalogPath);
            Pilot = Fit.PilotId;
            Ship = Fit.ShipId;
            Module = Fit.Fit(ShieldBooster);
            Second = second ? Fit.Fit(ShieldBooster) : 0;
            Damage(Module, 20);
            if (paste > 0)
            {
                Fit.Grant(NaniteRepairRules.PasteType, paste, cargo: true);
            }
            Fit.Db.Store.SetPosition(Ship, SpacePosition.Zero);
            Live = live ? new(new(Fit.Db.Path), Fit.Clock, _ => { }) : null;
        }

        internal void Damage(long module, double damage)
        {
            using WorldDbContext world = new(Fit.Db.Path);
            var row = world.Set<SavedModuleCondition>().Find(module) ?? world.Add(new SavedModuleCondition { ModuleId = module }).Entity;
            row.Damage = damage;
            world.SaveChanges();
        }

        internal double DamageLeft(long module)
        {
            using WorldDbContext world = new(Fit.Db.Path);
            return world.Set<SavedModuleCondition>().Find(module)?.Damage ?? 0;
        }

        internal long Paste()
        {
            using WorldDbContext world = new(Fit.Db.Path);
            return world
                .Set<OwnedItem>()
                .Where(i =>
                    i.OwnerId == Pilot
                    && i.LocationId == Ship
                    && i.TypeId == NaniteRepairRules.PasteType
                    && i.FlagId == (int)InventoryFlag.Cargo
                )
                .Sum(i => (long)i.Quantity);
        }

        internal NaniteRepairSchedule Start() => Assert.Single(Repairs.Start(Pilot, Ship, [Module], Now).Started).Schedule!;

        internal void At(long at) => Fit.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - Now));

        public void Dispose()
        {
            Live?.Dispose();
            Fit.Dispose();
        }
    }

    [Fact]
    public void ZeroNativeValueRepairHealsAtomicallyWithoutPasteOrATimer()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(f.Fit.CatalogPath, writable: true))
        {
            var type = catalog.Set<SourceRecord>().Find("client/types", ShieldBooster.ToString())!;
            var data = JsonNode.Parse(type.Data)!;
            data["basePrice"] = 0;
            type.Data = data.ToJsonString();
            catalog.Update(type);
            var materials = catalog.Set<SourceRecord>().Find("client/typematerials", ShieldBooster.ToString())!;
            materials.Data = "{\"materials\":[]}";
            catalog.Update(materials);
            catalog.SaveChanges();
        }
        var batch = f.Repairs.Start(f.Pilot, f.Ship, [f.Module], f.Now);
        var repair = Assert.Single(batch.Started);
        Assert.Null(repair.Schedule);
        Assert.Equal(0, repair.Condition.Damage);
        Assert.Empty(batch.Inventory);
        Assert.Equal(50, f.Paste());
        Assert.Equal(0, f.DamageLeft(f.Module));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedNaniteRepair>());
        Assert.True(saved.Set<OwnedItem>().Find(f.Module)!.Online);
    }

    [Fact]
    public void PaidRepairCommitsCargoAndItsExactDeadlineAndCancelsOnlyElapsedWork()
    {
        using Fixture f = new();
        long start = f.Now;
        var result = f.Repairs.Start(f.Pilot, f.Ship, [f.Module], start);
        var repair = Assert.Single(result.Started);
        var schedule = Assert.IsType<NaniteRepairSchedule>(repair.Schedule);
        Assert.Equal(start, schedule.Key.StartedAt);
        Assert.Equal(start + 2 * TimeSpan.TicksPerMinute, schedule.Key.CompletesAt);
        Assert.Equal(1, schedule.PasteUnits);
        Assert.Equal(49, f.Paste());
        Assert.Equal(20, f.DamageLeft(f.Module));
        var inventory = Assert.Single(result.Inventory);
        Assert.Equal(50, inventory.Previous!.Quantity);
        Assert.Equal(49, inventory.Item.Quantity);
        Assert.Empty(result.MissingPaste);
        Assert.Empty(result.Failed);
        f.At(start + TimeSpan.TicksPerMinute);
        Assert.Equal(10, f.Repairs.Stop(f.Pilot, f.Ship, schedule.Key, f.Now)!.Damage);
        Assert.Equal(10, f.DamageLeft(f.Module));
        Assert.Equal(49, f.Paste());
        f.Damage(f.Module, 15);
        Assert.Equal(15, f.Repairs.Stop(f.Pilot, f.Ship, schedule.Key, f.Now)!.Damage);
        Assert.Equal(15, f.DamageLeft(f.Module));
    }

    [Fact]
    public void BatchSeparatesSuccessMissingPasteAndInvalidModulesWithoutPartialItemConsumption()
    {
        using Fixture f = new(paste: 1, second: true);
        long second = f.Second;
        f.Damage(second, 20);
        long loose = f.Fit.Grant(ShieldBooster);
        var result = f.Repairs.Start(f.Pilot, f.Ship, [f.Module, second, loose], f.Now);
        Assert.Equal(f.Module, Assert.Single(result.Started).ModuleId);
        Assert.Equal(second, Assert.Single(result.MissingPaste));
        Assert.Equal(loose, Assert.Single(result.Failed));
        var consumed = Assert.Single(result.Inventory);
        Assert.Equal(1, consumed.Previous!.Quantity);
        Assert.Equal(0, consumed.Item.Quantity);
        Assert.Equal(0, f.Paste());
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Equal(f.Module, Assert.Single(world.Set<SavedNaniteRepair>()).ModuleId);
        Assert.Equal(20, f.DamageLeft(second));
    }

    [Fact]
    public void FailedScheduleWriteRollsBackPasteAndEveryBatchRepair()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectNanite BEFORE INSERT ON WorldNaniteRepair "
                    + "BEGIN SELECT RAISE(ABORT,'injected repair failure'); END"
            );
        }
        var error = Assert.Throws<DbUpdateException>(() => f.Start());
        Assert.IsType<SqliteException>(error.InnerException);
        Assert.Equal(50, f.Paste());
        Assert.Equal(20, f.DamageLeft(f.Module));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedNaniteRepair>());
    }

    [Fact]
    public void SameTickCancelAndRestartHaveDifferentGenerationsAndRejectTheOldStop()
    {
        using Fixture f = new();
        var old = f.Start();
        Assert.Equal(20, f.Repairs.Stop(f.Pilot, f.Ship, old.Key, f.Now)!.Damage);
        var next = f.Start();
        Assert.Equal(old.Key.StartedAt, next.Key.StartedAt);
        Assert.Equal(old.Key.CompletesAt, next.Key.CompletesAt);
        Assert.NotEqual(old.Key.Generation, next.Key.Generation);
        Assert.Null(f.Repairs.Stop(f.Pilot, f.Ship, old.Key, f.Now));
        Assert.Equal(20, f.DamageLeft(f.Module));
        Assert.Equal(48, f.Paste());
    }

    [Fact]
    public void DuplicateAndStaleRequestsCannotSpendAgainOrStopTheNextRepair()
    {
        using Fixture f = new();
        var old = f.Start();
        var duplicate = f.Repairs.Start(f.Pilot, f.Ship, [f.Module], f.Now);
        Assert.Empty(duplicate.Started);
        Assert.Empty(duplicate.Inventory);
        Assert.Equal(f.Module, Assert.Single(duplicate.Failed));
        Assert.Equal(49, f.Paste());
        f.At(f.Now + TimeSpan.TicksPerMinute);
        Assert.Equal(10, f.Repairs.Stop(f.Pilot, f.Ship, old.Key, f.Now)!.Damage);
        var current = f.Start();
        Assert.Null(f.Repairs.Stop(f.Pilot, f.Ship, old.Key, f.Now));
        Assert.Equal(48, f.Paste());
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Equal(current.Key, NaniteRepairPersistence.Schedule(Assert.Single(world.Set<SavedNaniteRepair>())).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartSettlesPaidElapsedProgressOnceWithoutReplayingOrRefunding(bool late)
    {
        using Fixture f = new();
        var schedule = f.Start();
        f.At(late ? schedule.Key.CompletesAt + TimeSpan.TicksPerMinute : schedule.Key.StartedAt + TimeSpan.TicksPerMinute / 2);
        WorldInitialization.Initialize(new(f.Fit.Db.Path), f.Fit.Clock);
        Assert.Equal(late ? 0 : 15, f.DamageLeft(f.Module));
        Assert.Equal(49, f.Paste());
        f.At(schedule.Key.CompletesAt + TimeSpan.TicksPerMinute);
        WorldInitialization.Initialize(new(f.Fit.Db.Path), f.Fit.Clock);
        Assert.Equal(late ? 0 : 15, f.DamageLeft(f.Module));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedNaniteRepair>());
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public void WarmOwnerFramesNeedNoSqlAndCompletePaidRepairsWithoutAClientRead()
    {
        using Fixture f = new(live: true);
        var schedule = f.Start();
        using (f.Live!.Enter()) { }
        using (var metrics = SessionPerformance.Begin("warm paid repair", _ => { }, force: true)!)
        {
            for (int frame = 0; frame < 100; frame++)
            {
                f.Fit.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
                using (f.Live.Enter())
                {
                    Assert.True(f.Live.Nanites.TryRepairing(f.Module, f.Now, out bool repairing));
                    Assert.True(repairing);
                }
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        f.At(schedule.Key.CompletesAt);
        using (f.Live.Enter()) { }
        Assert.Equal(0, f.DamageLeft(f.Module));
        Assert.Equal(49, f.Paste());
        using (var metrics = SessionPerformance.Begin("idle paid repair", _ => { }, force: true)!)
        {
            for (int frame = 0; frame < 100; frame++)
            {
                using (f.Live.Enter()) { }
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
    }

    [Fact]
    public void CoordinatesAndUnrelatedItemMembershipKeepTheActiveRepairCacheWarm()
    {
        using Fixture f = new(live: true);
        f.Start();
        using (f.Live!.Enter()) { }
        long unrelated = f.Fit.Grant(ShieldBooster);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldPosition SET X=X+1 WHERE ShipId={f.Ship}");
            world.Remove(world.Set<OwnedItem>().Find(unrelated)!);
            world.SaveChanges();
        }
        using var metrics = SessionPerformance.Begin("unrelated repair metadata", _ => { }, force: true)!;
        using (f.Live.Enter())
        {
            Assert.True(f.Live.Nanites.TryRepairing(f.Module, f.Now, out bool repairing));
            Assert.True(repairing);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void PaidRepairBlocksShieldActivationAndAnOverloadCycleWithoutSpendingCapacitor()
    {
        using Fixture f = new(live: true, second: true);
        f.Fit.Train(OverloadRules.ThermodynamicsSkill, 3);
        double before = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        f.Start();
        using (f.Live!.Enter()) { }
        PilotShieldBoostStore boosting = new(new(f.Fit.Db.Path));
        Assert.Throws<InvalidDataException>(() => boosting.Boost(f.Pilot, f.Module, f.Now));
        Assert.Throws<InvalidDataException>(() => boosting.BoostOverloaded(f.Pilot, f.Second, f.Now));
        Assert.Equal(before, f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now), 8);
        Assert.Equal(20, f.DamageLeft(f.Module));
        Assert.Equal(49, f.Paste());
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<ModuleCooldown>());
        Assert.Empty(world.Set<SavedShipHeat>());
    }

    [Fact]
    public void ARepairCannotStartOnAnotherModuleWhileAnOverloadCycleIsAlreadyPaid()
    {
        using Fixture f = new(live: true, second: true);
        f.Fit.Train(OverloadRules.ThermodynamicsSkill, 3);
        f.Damage(f.Second, 20);
        new PilotShieldBoostStore(new(f.Fit.Db.Path)).BoostOverloaded(f.Pilot, f.Module, f.Now);
        double before = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        Assert.Throws<InvalidDataException>(() => f.Repairs.Start(f.Pilot, f.Ship, [f.Second], f.Now));
        Assert.Equal(50, f.Paste());
        Assert.Equal(20, f.DamageLeft(f.Second));
        Assert.Equal(before, f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now), 8);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedNaniteRepair>());
        var heat = ShipHeatPersistence.Decode(saved.Set<SavedShipHeat>().Find(f.Ship)!.StateJson);
        Assert.Equal(f.Module, Assert.Single(heat.Cycles).ModuleId);
    }

    [Fact]
    public void DockingCancelsAtItsBoundaryAndKeepsOnlyPaidProgress()
    {
        using Fixture f = new(live: true);
        var schedule = f.Start();
        using (f.Live!.Enter()) { }
        f.At(schedule.Key.StartedAt + TimeSpan.TicksPerMinute);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Positions.Remove(world.Positions.Single(p => p.ShipId == f.Ship));
            world.SaveChanges();
        }
        // No reader observes the docking mutation until after the old deadline.
        f.At(schedule.Key.CompletesAt);
        using (f.Live.Enter()) { }
        Assert.Equal(10, f.DamageLeft(f.Module));
        Assert.Equal(49, f.Paste());
    }

    [Fact]
    public void RejectedUnfittedSelectionCannotSettleAnOldRepairAsASideEffect()
    {
        using Fixture f = new();
        var schedule = f.Start();
        long station = f.Fit.StationId;
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(f.Module)!;
            item.LocationId = station;
            item.FlagId = (int)InventoryFlag.Hangar;
            world.SaveChanges();
        }
        f.At(schedule.Key.CompletesAt);
        var rejected = f.Repairs.Start(f.Pilot, f.Ship, [f.Module], f.Now);
        Assert.Equal(f.Module, Assert.Single(rejected.Failed));
        Assert.Empty(rejected.Started);
        Assert.Empty(rejected.Inventory);
        Assert.Equal(20, f.DamageLeft(f.Module));
        using WorldDbContext unchanged = new(f.Fit.Db.Path);
        Assert.Equal(schedule.Key, NaniteRepairPersistence.Schedule(Assert.Single(unchanged.Set<SavedNaniteRepair>())).Key);
    }

    [Fact]
    public void HangarPasteMalformedSelectionsAndForeignShipsCannotFundARepair()
    {
        using Fixture f = new(paste: 0);
        f.Fit.Grant(NaniteRepairRules.PasteType, 50);
        var missing = f.Repairs.Start(f.Pilot, f.Ship, [f.Module], f.Now);
        Assert.Equal(f.Module, Assert.Single(missing.MissingPaste));
        Assert.Empty(missing.Started);
        Assert.Empty(missing.Inventory);
        Assert.Throws<InvalidDataException>(() => f.Repairs.Start(f.Pilot, f.Ship, [f.Module, f.Module], f.Now));
        Assert.Throws<InvalidDataException>(() => f.Repairs.Start(f.Pilot, f.Ship, [], f.Now));
        Assert.Throws<InvalidDataException>(() => f.Repairs.Start(f.Pilot, f.Ship, [0], f.Now));
        Assert.Throws<InvalidDataException>(() => f.Repairs.Start(f.Pilot, f.Ship + 1, [f.Module], f.Now));
        Assert.Throws<InvalidDataException>(() => f.Repairs.Start(f.Pilot + 1, f.Ship, [f.Module], f.Now));
        Assert.Equal(20, f.DamageLeft(f.Module));
        Assert.Equal(0, f.Paste());
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<SavedNaniteRepair>());
    }

    [Theory]
    [InlineData(ModuleCooldownKind.Cycle)]
    [InlineData(ModuleCooldownKind.Reload)]
    public void PaidCyclesExcludeRepairsWhileReloadingDoesNot(ModuleCooldownKind kind)
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Add(
                new ModuleCooldown
                {
                    ModuleId = f.Module,
                    Kind = kind,
                    ReadyAt = f.Now + TimeSpan.TicksPerMinute,
                }
            );
            world.SaveChanges();
        }
        var result = f.Repairs.Start(f.Pilot, f.Ship, [f.Module], f.Now);
        if (kind == ModuleCooldownKind.Cycle)
        {
            Assert.Equal(f.Module, Assert.Single(result.Failed));
            Assert.Empty(result.Started);
            Assert.Equal(50, f.Paste());
        }
        else
        {
            Assert.Equal(f.Module, Assert.Single(result.Started).ModuleId);
            Assert.Empty(result.Failed);
            Assert.Equal(49, f.Paste());
        }
        Assert.Equal(20, f.DamageLeft(f.Module));
    }

    [Fact]
    public void FailedSettlementRollsBackDamageAndKeepsTheSamePaidRepairForRetry()
    {
        using Fixture f = new();
        var schedule = f.Start();
        f.At(schedule.Key.StartedAt + TimeSpan.TicksPerMinute);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectNaniteStop BEFORE UPDATE ON WorldModuleCondition "
                    + "BEGIN SELECT RAISE(ABORT,'injected repair stop failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Repairs.Stop(f.Pilot, f.Ship, schedule.Key, f.Now));
        Assert.Equal(20, f.DamageLeft(f.Module));
        Assert.Equal(49, f.Paste());
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Equal(schedule.Key, NaniteRepairPersistence.Schedule(Assert.Single(world.Set<SavedNaniteRepair>())).Key);
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectNaniteStop");
        }
        Assert.Equal(10, f.Repairs.Stop(f.Pilot, f.Ship, schedule.Key, f.Now)!.Damage);
        Assert.Equal(49, f.Paste());
    }

    [Fact]
    public void BurnoutDuringRepairStopsWithoutHealingOrReonliningTheModule()
    {
        using Fixture f = new(live: true);
        var schedule = f.Start();
        f.At(schedule.Key.StartedAt + TimeSpan.TicksPerMinute);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            using CatalogDbContext catalog = new(f.Fit.CatalogPath);
            Assert.True(ModuleConditionPersistence.ApplyDamage(world, catalog, f.Module, 20).Current.BurnedOut);
            world.SaveChanges();
            transaction.Commit();
        }
        using (f.Live!.Enter()) { }
        Assert.Equal(40, f.DamageLeft(f.Module));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedNaniteRepair>());
        Assert.False(saved.Set<OwnedItem>().Find(f.Module)!.Online);
    }

    [Fact]
    public void WreckTransitionPreservesPaidProgressOnTheSurvivingModuleIdentity()
    {
        using Fixture f = new();
        var schedule = f.Start();
        f.At(schedule.Key.StartedAt + TimeSpan.TicksPerMinute);
        long wreck;
        using (WorldDbContext world = new(f.Fit.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            using CatalogDbContext catalog = new(f.Fit.CatalogPath);
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == f.Pilot);
            ShipLossPersistence.Apply(
                world,
                catalog,
                pilot,
                null,
                f.Ship,
                catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId,
                pilot.ActiveShip.Position!,
                f.Now,
                new SurvivingLoot(),
                PilotInteractionHistoryStore.Player(world, pilot, f.Ship)
            );
            world.SaveChanges();
            wreck = world.Set<Eden.Data.World.Loot.WorldWreck>().Single(w => w.ShipId == f.Ship).Id;
            transaction.Commit();
        }
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedNaniteRepair>());
        Assert.Equal(10, saved.Set<SavedModuleCondition>().Find(f.Module)!.Damage);
        var survivor = saved.Set<OwnedItem>().Find(f.Module)!;
        Assert.Equal(wreck, survivor.LocationId);
        Assert.Equal((int)InventoryFlag.Cargo, survivor.FlagId);
        Assert.False(survivor.Online);
    }

    [Fact]
    public void DetachedCheckpointKeepsThePaidRepairAndItsCargoFromTheSameGeneration()
    {
        using Fixture f = new(live: true);
        var first = f.Start();
        using (f.Live!.Enter()) { }
        f.At(first.Key.StartedAt + TimeSpan.TicksPerMinute / 2);
        WorldSnapshot snapshot;
        using (var metrics = SessionPerformance.Begin("paid repair capture", _ => { }, force: true)!)
        {
            snapshot = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot());
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (snapshot)
        {
            f.At(first.Key.StartedAt + TimeSpan.TicksPerMinute);
            Assert.Equal(10, f.Repairs.Stop(f.Pilot, f.Ship, first.Key, f.Now)!.Damage);
            var next = f.Start();
            Assert.NotEqual(first.Key, next.Key);
            Assert.Equal(48, f.Paste());
            snapshot.Project();
            using (WorldDbContext captured = new(snapshot.Database))
            {
                Assert.Equal(first.Key, NaniteRepairPersistence.Schedule(Assert.Single(captured.Set<SavedNaniteRepair>())).Key);
                Assert.Equal(20, captured.Set<SavedModuleCondition>().Find(f.Module)!.Damage);
            }
            string copy = Path.Combine(Path.GetDirectoryName(f.Fit.Db.Path)!, "nanite-captured.sqlite");
            LiveWorld.PersistSnapshot(snapshot.Database, copy);
            WorldInitialization.Initialize(new(copy), f.Fit.Clock);
            using WorldDbContext reopened = new(copy);
            Assert.Equal(10, reopened.Set<SavedModuleCondition>().Find(f.Module)!.Damage);
            Assert.Empty(reopened.Set<SavedNaniteRepair>());
            Assert.Equal(49, reopened.Set<OwnedItem>().Where(i => i.TypeId == NaniteRepairRules.PasteType).Sum(i => i.Quantity));
        }
    }

    [Fact]
    public void UpgradeCreatesTheRepairTableAndMalformedTimelinesFailInitialization()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldNaniteRepair; PRAGMA user_version=107;");
        }
        WorldInitialization.Initialize(new(f.Fit.Db.Path), f.Fit.Clock);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Equal(108, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
            Assert.Empty(world.Set<SavedNaniteRepair>());
            world.Database.OpenConnection();
            world.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints=ON");
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldNaniteRepair(
                    ModuleId,Generation,PilotId,ShipId,StartedAt,CompletesAt,InitialDamage,InitialHitPoints,PasteUnits)
                VALUES({f.Module},{Guid.NewGuid()},{f.Pilot},{f.Ship},{f.Now},{f.Now},20,40,1)
                """
            );
        }
        Assert.Throws<InvalidDataException>(() => WorldInitialization.Initialize(new(f.Fit.Db.Path), f.Fit.Clock));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothVolleyOwnersRejectAnyRepairingBankMemberBeforeSpendingAmmunition(bool resident, bool slave)
    {
        using Eden.Protocol.Tests.Ammunition.AmmunitionTests.Fixture f = new(liveWorld: resident);
        PassiveCatalogFixture.LoadOverload(new WorldDatabase(f.Db.Path).CatalogPath);
        var pilot = f.Pilot;
        long second = f.Buy(561, 1);
        f.Fitting.Fit(pilot.PilotId, [second], pilot.StationId, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(pilot.PilotId, pilot.ShipId, second, true);
        new PilotWeaponBankStore(new(f.Db.Path), f.Clock).Change(pilot.PilotId, pilot.ShipId, WeaponBankOperation.Link, f.Gun, second);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 6000;
        f.Space();
        f.Combat.FireBank(pilot.PilotId, f.Gun, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, .5);
        f.Clock.Milliseconds += 3000;
        long module = slave ? second : f.Gun;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new SavedModuleCondition { ModuleId = module, Damage = 20 });
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilot.PilotId,
                    LocationId = pilot.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    TypeId = NaniteRepairRules.PasteType,
                    Quantity = 50,
                }
            );
            world.SaveChanges();
        }
        var start = f.Db.Services.GetRequiredService<IPilotNaniteRepair>().Start(pilot.PilotId, pilot.ShipId, [module], f.Clock.Now);
        Assert.Single(start.Started);
        int ammunition = f.Ammo.Read(pilot.PilotId, pilot.ShipId).Sum(m => m.Quantity);
        Assert.Throws<InvalidDataException>(() => f.Combat.FireBank(pilot.PilotId, f.Gun, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
        Assert.Equal(ammunition, f.Ammo.Read(pilot.PilotId, pilot.ShipId).Sum(m => m.Quantity));
        using WorldDbContext unchanged = new(f.Db.Path);
        Assert.Equal(20, unchanged.Set<SavedModuleCondition>().Find(module)!.Damage);
        Assert.Single(unchanged.Set<SavedNaniteRepair>());
    }
}
