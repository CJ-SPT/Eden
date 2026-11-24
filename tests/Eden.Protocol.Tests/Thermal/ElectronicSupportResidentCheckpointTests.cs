using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Thermal;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Thermal;

public sealed partial class ElectronicSupportHeatTests
{
    [Fact]
    public void ModuleConditionViewsFollowHeatOwnerLifetime()
    {
        const int destinationSystem = 30000144;
        using Fixture f = new(Neutralizer);
        f.Pay();
        var heat = f.Read();
        var baseline = heat.Modules.Select(module => new FittedModuleCondition(module.Condition, module.Online, 0)).ToArray();
        using var owner = f.Live.EnterShared("condition view lifetime test");
        var source = f.Live.HeatForKnownShip(f.Ship);
        var before = source.Conditions(f.Ship, baseline);
        Assert.NotSame(baseline, before);
        Assert.Equal(1, source.CachedConditionViews);

        using (var transfer = f.Live.Economy.Begin(new(systems: [System, destinationSystem], ships: [f.Ship])))
        {
            transfer.Positions.Find(f.Ship)!.SystemId = destinationSystem;
            transfer.Commit();
        }

        Assert.Equal(0, source.CachedConditionViews);
        var destination = f.Live.HeatForKnownShip(f.Ship);
        Assert.NotSame(source, destination);
        _ = f.Read();
        var after = destination.Conditions(f.Ship, baseline);
        Assert.NotSame(before, after);
        Assert.Equal(before, after);
        Assert.Equal(1, destination.CachedConditionViews);
        destination.Forget(f.Ship);
        Assert.Equal(0, destination.CachedConditionViews);
    }

    [Theory]
    [InlineData(Neutralizer, false)]
    [InlineData(Neutralizer, true)]
    [InlineData(Jammer, false)]
    [InlineData(Jammer, true)]
    public void ResidentSupportHeatCheckpointFailureRetainsTheCommittedGenerationAndRetries(int type, bool stop)
    {
        using Fixture f = new(type);
        f.Pay();
        if (stop)
        {
            // Establish the earlier paid relational generation at a transaction synchronization boundary.
            using (f.Live.EnterShared("paid checkpoint fixture baseline"))
            {
                f.Live.Economy.Flush();
            }
            f.Stop();
        }
        var committed = f.Read();
        Assert.Equal(stop, committed.Idle);
        double source = f.SourceCharge;
        double target = f.TargetCharge;
        int events = f.Events.Changes.Count;
        Assert.Equal(f.Kind == Family.Capacitor ? 2 : 0, events);
        using var checkpoint = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot());
        double beforeSource;
        double beforeTarget;
        string? beforeHeat;
        string? beforeEffect;
        bool beforeMarker;
        using (WorldDbContext before = new(checkpoint.Database))
        {
            beforeSource = before.Set<ShipEnergy>().Find(f.Ship)!.Charge;
            beforeTarget = before.Set<ShipEnergy>().Find(f.Target)!.Charge;
            beforeHeat = before.Set<SavedShipHeat>().Find(f.Ship)?.StateJson;
            beforeEffect = before.Set<WorldPlayerEwar>().Find(f.Module, f.Target)?.ContactJson;
            beforeMarker = before.Set<ActiveFittedModule>().Any(row => row.ModuleId == f.Module);
            Assert.Equal(stop, beforeMarker);
            before.Database.ExecuteSqlRaw(
                stop
                    ? """
                    CREATE TRIGGER RejectResidentCapStop BEFORE DELETE ON WorldActiveModule
                    BEGIN SELECT RAISE(ABORT,'checkpoint stop'); END
                    """
                    : """
                    CREATE TRIGGER RejectResidentCapHeat BEFORE INSERT ON WorldShipHeat
                    BEGIN SELECT RAISE(ABORT,'checkpoint heat'); END
                    """
            );
        }
        Assert.Throws<SqliteException>(() => checkpoint.Project());
        Assert.True(HeatCycleRules.SameState(committed, f.Read()));
        Assert.Equal(source, f.SourceCharge, 8);
        Assert.Equal(target, f.TargetCharge, 8);
        Assert.Equal(events, f.Events.Changes.Count);
        using (WorldDbContext failed = new(checkpoint.Database))
        {
            Assert.Equal(beforeSource, failed.Set<ShipEnergy>().Find(f.Ship)!.Charge, 8);
            Assert.Equal(beforeTarget, failed.Set<ShipEnergy>().Find(f.Target)!.Charge, 8);
            Assert.Equal(beforeHeat, failed.Set<SavedShipHeat>().Find(f.Ship)?.StateJson);
            Assert.Equal(beforeEffect, failed.Set<WorldPlayerEwar>().Find(f.Module, f.Target)?.ContactJson);
            Assert.Equal(beforeMarker, failed.Set<ActiveFittedModule>().Any(row => row.ModuleId == f.Module));
            failed.Database.ExecuteSqlRaw("DROP TRIGGER IF EXISTS RejectResidentCapHeat; DROP TRIGGER IF EXISTS RejectResidentCapStop;");
        }
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Equal(source, reopened.Set<ShipEnergy>().Find(f.Ship)!.Charge, 8);
        Assert.Equal(target, reopened.Set<ShipEnergy>().Find(f.Target)!.Charge, 8);
        Assert.Equal(!stop, reopened.Set<ActiveFittedModule>().Any(row => row.ModuleId == f.Module));
        Assert.Equal(!stop && f.Kind == Family.Ewar, reopened.Set<WorldPlayerEwar>().Any(row => row.ModuleId == f.Module));
        Assert.True(HeatCycleRules.SameState(committed, ShipHeatPersistence.Decode(reopened.Set<SavedShipHeat>().Find(f.Ship)!.StateJson)));
        Assert.Equal(events, f.Events.Changes.Count);
    }
}
