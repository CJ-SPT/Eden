using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Protocol.Combat;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class PilotMissileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidentOutgoingMissileImpactPublishesAndSavesWithoutSql(bool npcTarget)
    {
        using var f = Prepare();
        using CatalogLifetime catalogLifetime = new(new(f.Db.Path));
        using ResidentFitCache fitting = new(new(f.Db.Path));
        long launcher = MissileCombatTests.Load(f, Launcher, Charge);
        Space(f);
        var npc = MissileCombatTests.Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long target = npcTarget ? npc.Id : VictimShip;
        List<PilotDamage> events = [];
        f.Db.Services.GetRequiredService<PilotDamageSignal>().Applied += events.Add;
        var launch = Launch(f, launcher, target);
        live.Economy.Read(
            world =>
            {
                using CatalogDbContext catalog = new(f.CatalogPath);
                if (npcTarget)
                {
                    _ = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
                }
                else
                {
                    _ = PilotFittingStore.ShipAttributes(world, catalog, Victim);
                }
                return true;
            },
            new(pilots: [Victim], ships: [VictimShip])
        );
        MissileImpact? impact = null;
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Advance(f);
            impact = Assert.Single(Store(f).TakeImpacts(f.PilotId));
            Advance(f);
            Assert.Empty(Store(f).TakeImpacts(f.PilotId));
        });
        Assert.Equal(launch.Projectile.Id, impact!.ProjectileId);
        Assert.True(impact.Applied > 0);
        Assert.False(impact.Destroyed);
        if (npcTarget)
        {
            Assert.Empty(events);
        }
        else
        {
            Assert.Equal(impact.Applied, Assert.Single(events).Applied);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        var projectile = reopened.Set<WorldMissile>().Find(launch.Projectile.Id)!;
        Assert.True(projectile.Reported);
        Assert.Equal(MissileFlightStatus.Impact, projectile.Status);
        Assert.Equal(impact.Applied, reopened.Set<SavedCombatDamage>().Find(target, f.ShipId)!.Damage);
    }

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
