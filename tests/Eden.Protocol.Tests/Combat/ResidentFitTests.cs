using Eden.Data.Catalog;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentFitTests
{
    [Fact]
    public void ShipTravelAndNamesReuseFitsButHullAndOwnershipChangesInvalidateBothOwners()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var expected = f.Fitting.ShipAttributes(90000001);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                UPDATE WorldShip SET StationId=60003760,Name='Renamed ship',StorageFlagId=4,StoredInId=NULL,StructureId=NULL
                WHERE Id=1000000000001;
                UPDATE WorldShip SET TypeId=TypeId,OwnerId=OwnerId WHERE Id=1000000000001;
                """
            );
        }
        using (var metrics = SessionPerformance.Begin("unchanged fit after travel", _ => { }, force: true)!)
        {
            Assert.Equal(expected.OrderBy(p => p.Key), f.Fitting.ShipAttributes(90000001).OrderBy(p => p.Key));
            Assert.Equal(0, metrics.FittingCalculations);
            Assert.InRange(metrics.Commands, 1, 8);
            Assert.Equal(1, metrics.Connections);
        }
        using WorldDbContext changed = new(f.Db.Path);
        using var transaction = changed.Database.BeginTransaction();
        string? original = changed.FitRevision(90000001);
        changed.Database.ExecuteSqlRaw("UPDATE WorldShip SET TypeId=TypeId+1 WHERE Id=1000000000001");
        Assert.NotEqual(original, changed.FitRevision(90000001));
        string? nextOwner = changed.FitRevision(90000002);
        // Use an inactive hull so transferring ownership preserves active-ship foreign keys.
        changed.Database.ExecuteSqlRaw(
            """
            INSERT INTO WorldShip(Id,OwnerId,TypeId,Name,StationId)
            VALUES(1000000000999,90000001,601,'Spare',60003760);
            """
        );
        string? previousOwner = changed.FitRevision(90000001);
        changed.Database.ExecuteSqlRaw("UPDATE WorldShip SET OwnerId=90000002 WHERE Id=1000000000999");
        Assert.NotEqual(previousOwner, changed.FitRevision(90000001));
        Assert.NotEqual(nextOwner, changed.FitRevision(90000002));
        transaction.Rollback();
        using var rollback = SessionPerformance.Begin("rolled back hull change", _ => { }, force: true)!;
        Assert.Equal(expected.OrderBy(p => p.Key), f.Fitting.ShipAttributes(90000001).OrderBy(p => p.Key));
        Assert.Equal(0, rollback.FittingCalculations);
    }

    [Fact]
    public void ActiveEffectMembershipInvalidatesAtBothBoundaries()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        _ = f.Fitting.ShipAttributes(90000001);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new ActiveFittedModule { ModuleId = f.Module, ShipId = 1000000000001 });
            world.SaveChanges();
        }
        using (var metric = SessionPerformance.Begin("active", _ => { }, force: true)!)
        {
            _ = f.Fitting.ShipAttributes(90000001);
            Assert.Equal(1, metric.FittingCalculations);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<ActiveFittedModule>().ExecuteDelete();
        }
        using (var metric = SessionPerformance.Begin("inactive", _ => { }, force: true)!)
        {
            _ = f.Fitting.ShipAttributes(90000001);
            Assert.Equal(1, metric.FittingCalculations);
        }
    }

    [Fact]
    public void IncomingWebAndItsRemovalRefreshCachedSpeed()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES({(int)DogmaAttributeId.StasisWebifierResistance},{"webResistance"})"
            );
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES(601,{(int)DogmaAttributeId.StasisWebifierResistance},{1d})"
            );
        }
        double initial = f.Fitting.ShipAttributes(90000001)[DogmaAttributeId.MaxVelocity];
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new WorldTackle
                {
                    ModuleId = f.Module,
                    PilotId = 90000001,
                    SourceShipId = 1000000000001,
                    TargetId = 1000000000001,
                    TargetKind = TackleTargetKind.Ship,
                    Effect = DogmaEffect.StasisWebifier,
                    WebPercent = -50,
                    TypeId = (int)CombatWeaponType.CivilianGatlingRailgun,
                }
            );
            world.SaveChanges();
        }
        double webbed = f.Fitting.ShipAttributes(90000001)[DogmaAttributeId.MaxVelocity];
        Assert.Equal(initial * 0.5, webbed, 6);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldTackle>().ExecuteDelete();
        }
        Assert.Equal(initial, f.Fitting.ShipAttributes(90000001)[DogmaAttributeId.MaxVelocity]);
    }

    [Fact]
    public void DisposedWriteTransactionCannotPublishOnAnotherCommitAndReopenStartsCold()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var expected = f.Fitting.ShipAttributes(90000001);
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            using (var tx = world.Database.BeginTransaction())
            {
                world.Set<OwnedItem>().Find(f.Module)!.Online = false;
                world.SaveChanges();
                _ = PilotFittingStore.ShipAttributes(world, catalog, 90000001);
            }
            using (var tx = world.Database.BeginTransaction())
            {
                tx.Commit();
            }
        }
        Assert.Equal(expected.OrderBy(p => p.Key), f.Fitting.ShipAttributes(90000001).OrderBy(p => p.Key));
        cache.Dispose();
        using ResidentFitCache reopened = new(new(f.Db.Path));
        using var metrics = SessionPerformance.Begin("reopen", _ => { }, force: true)!;
        Assert.Equal(expected.OrderBy(p => p.Key), f.Fitting.ShipAttributes(90000001).OrderBy(p => p.Key));
        Assert.Equal(1, metrics.FittingCalculations);
    }

    [Fact]
    public void CommittedFitIsReusedAndPendingAndRolledBackChangesCannotPoisonIt()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        _ = f.Fitting.ShipAttributes(90000001);
        using (var measure = SessionPerformance.Begin("warm", _ => { }, force: true)!)
        {
            _ = f.Fitting.ShipAttributes(90000001);
            Assert.Equal(0, measure.FittingCalculations);
        }
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            var module = world.Set<OwnedItem>().Find(f.Module)!;
            module.Online = false;
            var pending = PilotFittingStore.ShipAttributes(world, catalog, 90000001);
            world.SaveChanges();
            var saved = PilotFittingStore.ShipAttributes(world, catalog, 90000001);
            Assert.Equal(pending.OrderBy(p => p.Key), saved.OrderBy(p => p.Key));
            transaction.Rollback();
        }
        using (var measure = SessionPerformance.Begin("rollback", _ => { }, force: true)!)
        {
            _ = f.Fitting.ShipAttributes(90000001);
            Assert.Equal(0, measure.FittingCalculations);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(f.Module)!.Online = false;
            world.SaveChanges();
        }
        using (var measure = SessionPerformance.Begin("changed", _ => { }, force: true)!)
        {
            _ = f.Fitting.ShipAttributes(90000001);
            Assert.Equal(1, measure.FittingCalculations);
        }
        using (var measure = SessionPerformance.Begin("warm changed", _ => { }, force: true)!)
        {
            _ = f.Fitting.ShipAttributes(90000001);
            Assert.Equal(0, measure.FittingCalculations);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task ConcurrentReadersUseCommittedImmutableFits(int readers)
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var expected = f.Fitting.ShipAttributes(90000001);
        await Task.WhenAll(
            Enumerable
                .Range(0, readers)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        var actual = f.Fitting.ShipAttributes(90000001);
                        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
                        ((Dictionary<Eden.Gameplay.Dogma.DogmaAttributeId, double>)actual).Clear();
                    })
                )
        );
        Assert.Equal(expected.OrderBy(p => p.Key), f.Fitting.ShipAttributes(90000001).OrderBy(p => p.Key));
    }

    [Fact]
    public void WarmShotsRetainLivePaymentsAndReuseTheFit()
    {
        using PilotCombatTests.Fixture f = new();
        using ResidentFitCache cache = new(new(f.Db.Path));
        _ = f.Fire();
        f.Clock.Milliseconds += 2000;
        using var measure = SessionPerformance.Begin("shot", _ => { }, force: true)!;
        var shot = f.Fire();
        Assert.True(shot.Applied > 0);
        Assert.Equal(0, measure.FittingCalculations);
    }
}
