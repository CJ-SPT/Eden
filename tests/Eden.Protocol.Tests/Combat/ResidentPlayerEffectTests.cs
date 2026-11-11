using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class PlayerEwarTests
{
    [Theory]
    [InlineData(1948, DogmaEffect.PlayerEcm)]
    [InlineData(1968, DogmaEffect.SensorDampener)]
    [InlineData(2108, DogmaEffect.TrackingDisruptor)]
    [InlineData(37543, DogmaEffect.GuidanceDisruptor)]
    [InlineData(12709, DogmaEffect.TargetPainter)]
    public void ResidentPlayerEffectsPayRepeatSweepStopAndCheckpointWithoutSql(int type, DogmaEffect effect)
    {
        using var f = Prepare();
        long module = f.Fit(type);
        Space(f);
        long ship = f.ShipId;
        long target = Wing(f);
        using ResidentFitCache fittingCache = new(new(f.Db.Path));
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var store = Store(f);
        ElectronicWarfareActivation Fire() =>
            store.Activate(f.PilotId, module, effect, new(target, TackleTargetKind.Ship), new HashSet<long> { target }, f.Clock.Now);
        var first = Assert.Single(Fire().Contacts);
        live.Economy.Warm();
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(first.ExpiresAt - f.Clock.Now));
        void Prime()
        {
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
                    _ = PilotFittingStore.ShipAttributes(world, catalog, world.Pilots.Single(row => row.ActiveShipId == target));
                    return true;
                },
                new(pilots: [f.PilotId], ships: [ship, target])
            );
        }
        Prime();
        ElectronicWarfareActivation? result = null;
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            result = Fire();
            Assert.Throws<InvalidDataException>(() => Fire());
        });
        var paid = Assert.IsType<ElectronicWarfareActivation>(result);
        var contact = Assert.Single(paid.Contacts);
        Assert.True(contact.ExpiresAt > first.ExpiresAt);
        Assert.Equal(contact, Assert.Single(store.Incoming(target)));
        using (var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            checkpoint.Project();
            using WorldDbContext saved = new(checkpoint.Database);
            Assert.Equal(contact, Assert.Single(saved.Set<WorldPlayerEwar>()).Contact());
            Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(ship)!.Charge, 8);
        }
        Prime();
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            store.Sweep(f.Clock.Now);
            Assert.Single(store.Incoming(target));
            store.Stop(f.PilotId, ship, module);
            Assert.Empty(store.Incoming(target));
        });
        using var stopped = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        stopped.Project();
        using WorldDbContext reopened = new(stopped.Database);
        Assert.Empty(reopened.Set<WorldPlayerEwar>());
        Assert.Null(reopened.Set<ActiveFittedModule>().Find(module));
        Assert.Equal(contact.ExpiresAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(paid.Capacitor, reopened.Set<ShipEnergy>().Find(ship)!.Charge, 8);
    }
}
