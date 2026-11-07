using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Combat;

public sealed class FittingSnapshotTests
{
    private const long PilotId = 90000001;
    private const long ShipId = 1000000000001;

    [Fact]
    public void ModuleAndBrainReadsShareTheCommittedGraphAndReturnIndependentAttributes()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var module = f.Inventory.List(PilotId, ShipId).Single(i => i.Id == f.Module);
        IReadOnlyDictionary<DogmaAttributeId, double> first;
        using (var cold = SessionPerformance.Begin("cold", _ => { }, force: true)!)
        {
            first = f.Fitting.ModuleAttributes(PilotId, module);
            Assert.Equal(1, cold.FittingCalculations);
        }
        using var warm = SessionPerformance.Begin("warm", _ => { }, force: true)!;
        var second = f.Fitting.ModuleAttributes(PilotId, module);
        Assert.Equal(first.OrderBy(p => p.Key), second.OrderBy(p => p.Key));
        ((Dictionary<DogmaAttributeId, double>)second).Clear();
        Assert.Equal(first.OrderBy(p => p.Key), f.Fitting.ModuleAttributes(PilotId, module).OrderBy(p => p.Key));
        _ = f.Fitting.SkillBonuses(PilotId);
        Assert.Equal(0, warm.FittingCalculations);
        Assert.InRange(warm.Commands, 1, 9);
        Assert.Equal(3, warm.Connections);
    }

    [Fact]
    public void PendingAndRolledBackSnapshotsStayPrivateAndCommittedChangesInvalidate()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var module = f.Inventory.List(PilotId, ShipId).Single(i => i.Id == f.Module);
        var expected = f.Fitting.ModuleAttributes(PilotId, module);
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            using (var transaction = world.Database.BeginTransaction())
            {
                world.Set<OwnedItem>().Find(f.Module)!.FlagId = (int)InventoryFlag.Cargo;
                using var pending = SessionPerformance.Begin("pending", _ => { }, force: true)!;
                _ = PilotFittingStore.ReadModuleAttributes(world, catalog, PilotId, module);
                Assert.Equal(1, pending.FittingCalculations);
                world.SaveChanges();
                _ = PilotFittingStore.ReadModuleAttributes(world, catalog, PilotId, module);
                Assert.Null(world.CachedFittingSnapshot(PilotId, ShipId, world.FitRevision(PilotId))!.Modules.GetValueOrDefault(module.Id));
            }
            // Reusing a context after disposing its transaction must discard staged snapshots.
            using var unrelated = world.Database.BeginTransaction();
            unrelated.Commit();
        }
        using (var warm = SessionPerformance.Begin("rollback", _ => { }, force: true)!)
        {
            Assert.Equal(expected.OrderBy(p => p.Key), f.Fitting.ModuleAttributes(PilotId, module).OrderBy(p => p.Key));
            Assert.Equal(0, warm.FittingCalculations);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(f.Module)!.Online = false;
            world.SaveChanges();
        }
        using var changed = SessionPerformance.Begin("changed", _ => { }, force: true)!;
        _ = f.Fitting.ModuleAttributes(PilotId, module with { Online = false });
        Assert.Equal(1, changed.FittingCalculations);
        _ = f.Fitting.SkillBonuses(PilotId);
        Assert.Equal(1, changed.FittingCalculations);
    }

    [Fact]
    public void CooldownsRemainFreshWithoutRebuildingTheGraph()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var module = f.Inventory.List(PilotId, ShipId).Single(i => i.Id == f.Module) with
        {
            GroupId = TackleDefinitions.WarpCoreStabilizerGroup,
        };
        Assert.Equal(0, f.Fitting.ModuleAttributes(PilotId, module)[DogmaAttributeId.NextActivationTime]);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new ModuleCooldown { ModuleId = module.Id, ReadyAt = 12345 });
            world.SaveChanges();
        }
        using var warm = SessionPerformance.Begin("cooldown", _ => { }, force: true)!;
        Assert.Equal(12345, f.Fitting.ModuleAttributes(PilotId, module)[DogmaAttributeId.NextActivationTime]);
        Assert.Equal(0, warm.FittingCalculations);
    }

    [Fact]
    public void CatalogEditsAndReopeningDiscardSnapshots()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var module = f.Inventory.List(PilotId, ShipId).Single(i => i.Id == f.Module);
        double previous = f.Fitting.ModuleAttributes(PilotId, module)[DogmaAttributeId.MaxRange];
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                UPDATE TypeAttribute SET Value={previous + 100}
                    WHERE ItemTypeId={module.TypeId} AND AttributeId={(int)DogmaAttributeId.MaxRange}
                """
            );
        }
        using (var changed = SessionPerformance.Begin("catalog", _ => { }, force: true)!)
        {
            Assert.Equal(previous + 100, f.Fitting.ModuleAttributes(PilotId, module)[DogmaAttributeId.MaxRange]);
            Assert.Equal(1, changed.FittingCalculations);
        }
        cache.Dispose();
        using ResidentFitCache reopened = new(new(f.Db.Path));
        using var cold = SessionPerformance.Begin("reopen", _ => { }, force: true)!;
        Assert.Equal(previous + 100, f.Fitting.ModuleAttributes(PilotId, module)[DogmaAttributeId.MaxRange]);
        Assert.Equal(1, cold.FittingCalculations);
    }
}
