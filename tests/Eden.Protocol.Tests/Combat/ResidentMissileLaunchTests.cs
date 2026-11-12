using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class PilotMissileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidentMissileLaunchRejectsRepeatsCancelsAndSavesWithoutSql(bool npcTarget)
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge, quantity: 4);
        Space(f);
        long ship = f.ShipId;
        long target = npcTarget ? MissileCombatTests.Space(f).Id : VictimShip;
        using ResidentFitCache fitting = new(new(f.Db.Path));
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var store = Store(f);
        var first = Launch(f, launcher, target);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks((long)Math.Ceiling(first.Launcher.CycleMilliseconds * TimeSpan.TicksPerMillisecond)));
        live.Economy.Read(
            world =>
            {
                using CatalogDbContext catalog = new(f.CatalogPath);
                _ = PilotFittingStore.ShipAttributes(world, catalog, f.PilotId);
                return true;
            },
            new(pilots: [f.PilotId], ships: [ship])
        );
        MissileLaunch? second = null;
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            second = Launch(f, launcher, target);
            Assert.Throws<InvalidDataException>(() => Launch(f, launcher, target));
            Assert.Empty(store.TakeImpacts(f.PilotId));
            store.Cancel(f.PilotId, ship);
            Assert.Empty(store.List(SystemId));
        });
        Assert.Equal(2, second!.Magazine.Quantity);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        var flights = reopened.Set<WorldMissile>().OrderBy(row => row.Id).ToArray();
        Assert.Equal(2, flights.Length);
        Assert.All(
            flights,
            row =>
            {
                Assert.Equal(MissileFlightStatus.Expired, row.Status);
                Assert.Equal(f.Clock.Now, row.EndedAt);
                Assert.Equal(target, row.TargetId);
            }
        );
        Assert.Equal(2, reopened.Set<OwnedItem>().Single(row => row.LocationId == launcher).Quantity);
        Assert.Equal(
            f.Clock.Now + (long)Math.Ceiling(second.Launcher.CycleMilliseconds * TimeSpan.TicksPerMillisecond),
            reopened.Set<ModuleCooldown>().Find(launcher)!.ReadyAt
        );
    }

    [Fact]
    public void ResidentMissileReportAcknowledgementIsSavedWithoutSqlOrReplay()
    {
        using var f = Prepare();
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var launch = Launch(f, launcher);
        Advance(f);
        var store = Store(f);
        using var pending = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            var impact = Assert.Single(store.TakeImpacts(f.PilotId));
            Assert.Equal(launch.Projectile.Id, impact.ProjectileId);
            Assert.True(impact.Applied > 0);
            Assert.Empty(store.TakeImpacts(f.PilotId));
        });
        pending.Project();
        using (WorldDbContext old = new(pending.Database))
        {
            Assert.False(old.Set<WorldMissile>().Find(launch.Projectile.Id)!.Reported);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.True(reopened.Set<WorldMissile>().Find(launch.Projectile.Id)!.Reported);
    }
}
