using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Propulsion;

public sealed partial class TackleTests
{
    [Theory]
    [InlineData(526, DogmaEffect.StasisWebifier)]
    [InlineData(447, DogmaEffect.WarpScrambler)]
    [InlineData(3242, DogmaEffect.WarpDisruptor)]
    public void ResidentTacklePaysRepeatsSweepsCancelsAndCheckpointsWithoutSql(int type, DogmaEffect effect)
    {
        using var f = Prepare(type);
        long module = f.Fit(type);
        Space(f);
        long ship = f.ShipId;
        long target = Wing(f);
        using ResidentFitCache fittingCache = new(new(f.Db.Path));
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var store = Store(f);
        TackleActivation Fire() =>
            store.Activate(f.PilotId, module, effect, new(target, TackleTargetKind.Ship), new HashSet<long> { target }, f.Clock.Now);
        var first = Fire();
        live.Economy.Warm();
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(first.ReadyAt - f.Clock.Now));
        live.Economy.Read(
            world =>
            {
                using CatalogDbContext catalog = new(f.CatalogPath);
                _ = PilotFittingStore.ReadModuleAttributes(
                    world,
                    catalog,
                    f.PilotId,
                    PilotInventoryStore.Read(catalog, world.Set<OwnedItem>().Find(module)!)
                );
                _ = PilotFittingStore.ShipAttributes(world, catalog, f.PilotId);
                return true;
            },
            new(pilots: [f.PilotId], ships: [ship, target])
        );
        TackleActivation? result = null;
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            result = Fire();
            Assert.Throws<InvalidDataException>(() => Fire());
            store.Sweep(f.Clock.Now);
            Assert.Single(store.Incoming(target));
            store.Cancel(f.PilotId, ship);
            Assert.Empty(store.Incoming(target));
        });
        var paid = Assert.IsType<TackleActivation>(result);
        Assert.True(paid.ReadyAt > first.ReadyAt);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Empty(reopened.Set<WorldTackle>());
        Assert.Equal(paid.Capacitor, reopened.Set<ShipEnergy>().Find(ship)!.Charge, 8);
        Assert.Equal(paid.ReadyAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void ResidentCoreStrengthExpiresAtItsPaidBoundaryWithoutSqlAndRetainsReactivation()
    {
        const int Core = 10998;
        using var f = Prepare(Core);
        long module = f.Fit(Core);
        Space(f);
        long ship = f.ShipId;
        using ResidentFitCache fittingCache = new(new(f.Db.Path));
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var store = Store(f);
        var paid = store.Activate(f.PilotId, module, DogmaEffect.WarpCoreStabilizer, null, new HashSet<long>(), f.Clock.Now);
        long expires = f.Clock.Now + (long)Math.Ceiling(paid.Module.DurationMilliseconds * TimeSpan.TicksPerMillisecond);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(expires - f.Clock.Now - 1));
        ResidentMonitorAssertions.WithoutSql(() => store.Sweep(f.Clock.Now));
        Assert.NotNull(live.Economy.Inspect(world => world.Set<ActiveFittedModule>().Find(module)));
        using var active = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            store.Sweep(f.Clock.Now);
            Assert.Throws<InvalidDataException>(() =>
                store.Activate(f.PilotId, module, DogmaEffect.WarpCoreStabilizer, null, new HashSet<long>(), f.Clock.Now)
            );
        });
        Assert.Null(live.Economy.Inspect(world => world.Set<ActiveFittedModule>().Find(module)));
        active.Project();
        using (WorldDbContext saved = new(active.Database))
        {
            Assert.NotNull(saved.Set<ActiveFittedModule>().Find(module));
            Assert.Equal(paid.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        }
        using var stopped = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        stopped.Project();
        using WorldDbContext reopened = new(stopped.Database);
        Assert.Null(reopened.Set<ActiveFittedModule>().Find(module));
        Assert.Equal(paid.ReadyAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(paid.Capacitor, reopened.Set<ShipEnergy>().Find(ship)!.Charge, 8);
    }
}
