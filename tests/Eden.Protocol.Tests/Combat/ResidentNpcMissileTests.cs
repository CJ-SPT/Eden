using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class NpcMissileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidentIncomingFlightAndLossCommitWithoutSql(bool lethal)
    {
        using Fixture f = new(liveWorld: true, useTestClock: true);
        Prepare(f);
        if (lethal)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=100000 WHERE ItemTypeId=212 AND AttributeId=114");
        }
        long pilot = f.Pilot.PilotId;
        long ship = f.Pilot.ShipId;
        Fire(f);
        var live = LiveWorld.Find(f.Db.Path)!;
        using CatalogLifetime catalogLifetime = new(new(f.Db.Path));
        using ResidentFitCache fitting = new(new(f.Db.Path));
        live.Economy.Read(
            world =>
            {
                using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
                _ = PilotFittingStore.ShipAttributes(world, catalog, pilot);
                if (lethal)
                {
                    catalog.Definitions.PreloadCore();
                    _ = catalog.FittingAttributes(FleetDefinitions.CapsuleType);
                    _ = catalog.FittingAttributes(26510); // Fixture's Caldari corvette wreck.
                    _ = catalog.Records(CatalogData.Client.Achievements);
                    _ = catalog.Records(CatalogData.Client.Achievementevents);
                }
                return true;
            },
            new(pilots: [pilot], ships: [ship])
        );
        List<NpcShot> shots = [];
        var incoming = Incoming(f);
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident incoming missile flight", _ => { }, force: true, command: commands.Add)!)
        {
            for (int tick = 0; tick < 8; tick++)
            {
                f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
                shots.AddRange(incoming.Advance(pilot, ship, SpacePosition.Zero, f.Clock.Now));
            }
            Assert.Empty(incoming.Advance(pilot, ship, SpacePosition.Zero, f.Clock.Now));
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.InRange(metrics.FittingCalculations, 0, lethal ? 1 : 0);
        }
        var shot = Assert.Single(shots);
        Assert.True(shot.Applied > 0);
        Assert.Equal(lethal, shot.Health.Remaining.Destroyed);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.True(Assert.Single(reopened.Set<WorldMissile>()).Reported);
        if (lethal)
        {
            var loss = Assert.Single(reopened.Set<ShipLoss>());
            Assert.Equal(ship, loss.ShipId);
            Assert.Equal(shot.Capsule!.ShipId, loss.CapsuleId);
            Assert.Equal(ship, Assert.Single(reopened.Set<WorldWreck>()).ShipId);
            Assert.Null(reopened.Ships.Find(ship));
            Assert.Equal(loss.CapsuleId, reopened.Pilots.Find(pilot)!.ActiveShipId);
        }
        else
        {
            Assert.Empty(reopened.Set<ShipLoss>());
            Assert.NotNull(reopened.Set<ShipHealthState>().Find(ship));
        }
    }

    [Fact]
    public void ResidentNpcLaunchRetainsItsExactDeadlineAndDoesNotRepeatWithoutPayment()
    {
        using Fixture f = new(liveWorld: true, useTestClock: true);
        Prepare(f);
        long pilot = f.Pilot.PilotId;
        long ship = f.Pilot.ShipId;
        var combat = f.Combat;
        NpcShot? Shoot() => combat.Fire(pilot, ship, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, 1);
        Assert.Null(Shoot());
        var live = LiveWorld.Find(f.Db.Path)!;
        long next = live.Economy.Inspect(world => world.Set<NpcAggression>().Find(f.Npc.Id)!.MissileReadyAt);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(next - f.Clock.Now - 1));
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.Null(Shoot());
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            Assert.Null(Shoot());
            Assert.Null(Shoot());
        });
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        var launched = saved.Set<WorldMissile>().OrderBy(row => row.Id).ToArray();
        Assert.Equal(2, launched.Length);
        Assert.All(launched, row => Assert.Equal(ship, row.TargetId));
        Assert.True(launched[1].LaunchedAt > launched[0].LaunchedAt);
        Assert.True(saved.Set<NpcAggression>().Find(f.Npc.Id)!.MissileReadyAt > next);
    }
}
