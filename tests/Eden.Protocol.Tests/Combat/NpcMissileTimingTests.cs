using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class NpcMissileTests
{
    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 1.3)]
    public void DelayedImpactUsesContactSpeed(bool braking, double expectedRawDamage)
    {
        var timely = Run(false);
        var late = Run(true);
        Assert.Equal(expectedRawDamage, timely.Damage.Total, 6);
        Assert.Equal(timely.Damage, late.Damage);
        Assert.Equal(timely.Applied, late.Applied);
        Assert.Equal(timely.Weapon.StartedAt, late.Weapon.StartedAt);
        NpcShot Run(bool latePoll)
        {
            using Fixture f = new(liveWorld: true, useTestClock: true);
            Prepare(f);
            using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute VALUES (644,'aimedLaunch')");
                catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (212,644,1),(212,653,100)");
            }
            var start = f.Npc.Position + new SpacePosition(500, 0, 0);
            var ship = f.Pilot.ShipId;
            var pilot = f.Pilot.PilotId;
            f.Db.Store.SetPosition(ship, start, f.Npc.SystemId);
            var launch = f.Clock.Now;
            _ = f.Combat.Fire(pilot, ship, f.Npc.Id, SpacePosition.Zero, launch, .5, 1);
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            SpacePosition moving = new(-1000, 0, 0);
            var atImpact = braking ? moving : SpacePosition.Zero;
            var afterImpact = braking ? SpacePosition.Zero : moving;
            f.Db.Store.SetPosition(ship, start + atImpact, f.Npc.SystemId);
            if (braking)
            {
                // The target moved through the impact, then stopped before the late callback.
                var live = LiveWorld.Find(f.Db.Path)!;
                using var ownership = live.Enter();
                live.Economy.MissileHistory(ship)!.Observe(f.Clock.Now, start + atImpact, atImpact);
            }
            if (!latePoll)
            {
                return Assert.Single(Incoming(f).Advance(pilot, ship, atImpact, f.Clock.Now));
            }
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            f.Db.Store.SetPosition(ship, start + atImpact + afterImpact, f.Npc.SystemId);
            var shot = Assert.Single(Incoming(f).Advance(pilot, ship, afterImpact, f.Clock.Now));
            Assert.True(shot.Weapon.StartedAt < launch + TimeSpan.TicksPerSecond);
            return shot;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public void LaunchPhaseDoesNotBackdateImpact(int phase)
    {
        using Fixture f = new(liveWorld: true, useTestClock: true);
        Prepare(f);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute VALUES (644,'aimedLaunch')");
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (212,644,1)");
        }
        var start = f.Npc.Position + new SpacePosition(500, 0, 0);
        SpacePosition velocity = new(100, 0, 0);
        var ship = f.Pilot.ShipId;
        var pilot = f.Pilot.PilotId;
        f.Db.Store.SetPosition(ship, start, f.Npc.SystemId);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(phase * TimeSpan.TicksPerMillisecond - f.Clock.Now % TimeSpan.TicksPerSecond));
        _ = f.Combat.Fire(pilot, ship, f.Npc.Id, velocity, f.Clock.Now, .5, 1);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
        f.Db.Store.SetPosition(ship, start + velocity * .001, f.Npc.SystemId);
        var hits = Incoming(f).Advance(pilot, ship, velocity, f.Clock.Now);
        Assert.Empty(hits);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(999));
        f.Db.Store.SetPosition(ship, start + velocity, f.Npc.SystemId);
        Assert.Single(Incoming(f).Advance(pilot, ship, velocity, f.Clock.Now));
    }
}
