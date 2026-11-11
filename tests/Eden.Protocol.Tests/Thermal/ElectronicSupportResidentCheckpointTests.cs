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
            beforeEffect = before.Set<WorldPlayerEwar>().Find(f.Module)?.ContactJson;
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
            Assert.Equal(beforeEffect, failed.Set<WorldPlayerEwar>().Find(f.Module)?.ContactJson);
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
