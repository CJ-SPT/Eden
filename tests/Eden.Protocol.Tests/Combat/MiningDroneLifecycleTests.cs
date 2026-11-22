using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Drones;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class MiningDroneLifecycleTests
{
    private const long Rock = 8000000000300;
    private const int System = 30000144;

    private static Fixture Prepare(bool ice, double capacity = 1000000)
    {
        var f = UtilityDroneTests.Prepare();
        PassiveCatalogFixture.LoadMining(f.Base.CatalogPath);
        using CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true);
        catalog.Database.ExecuteSql(
            $"INSERT OR REPLACE INTO TypeAttribute VALUES (16229,{(int)DogmaAttributeId.GeneralMiningHoldCapacity},{capacity})"
        );
        return f;
    }

    private static long Launch(Fixture f, LiveWorld live, bool ice, DroneOrder order)
    {
        long stack = f.Bay(ice ? 43699 : 10246, 1);
        f.Space();
        using (var transaction = live.Economy.Begin())
        {
            var position = transaction.Pilots.Find(f.Pilot)!.ActiveShip.Position!;
            transaction.Add(
                new WorldAsteroid
                {
                    Id = Rock,
                    SystemId = System,
                    BeltId = 40009232,
                    TypeId = ice ? 16262 : 1230,
                    Quantity = 100000,
                    Radius = 30,
                    X = position.X + 500,
                    Y = position.Y,
                    Z = position.Z,
                }
            );
            transaction.Commit();
        }
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        f.Drones.Order(f.Pilot, [drone], order, Rock, new HashSet<long> { Rock }, f.Now);
        return drone;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedOreAndIceCyclesUseGatheringSettingsHoldsLedgersAndSkillPointsAfterRestart(bool ice)
    {
        using var f = Prepare(ice);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long drone = Launch(f, live, ice, DroneOrder.MineRepeatedly);
        int seconds = (int)Math.Ceiling(f.Drones.Profile(f.Pilot, ice ? 43699 : 10246)!.Utility!.DurationMilliseconds / 1000);
        f.Tick();
        f.Tick(seconds);
        int first = Assert.Single(f.Tick().Changes).Item.Quantity;
        Assert.InRange(first, ice ? 1 : 468, ice ? 1 : 469);
        Assert.Empty(f.Drones.Advance(f.Pilot, f.Now, (int)f.Base.Clock.Seconds).Changes);
        f.Drones.Release(f.Pilot, f.Ship);
        live.Dispose();
        WorldTuningTests.Activate(
            f.Base.Db,
            settings =>
                settings with
                {
                    Resources = settings.Resources with { OreYieldMultiplier = 2, IceYieldMultiplier = 2 },
                    MiningHolds = settings.MiningHolds with { GeneralMultiplier = 2 },
                    Progression = settings.Progression with { MiningSkillPointMultiplier = 2 },
                }
        );
        using LiveWorld restarted = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Reconnect, null, new HashSet<long>(), f.Now);
        f.Drones.Order(f.Pilot, [drone], DroneOrder.MineRepeatedly, Rock, new HashSet<long> { Rock }, f.Now);
        var effective = f.Drones.Profile(f.Pilot, ice ? 43699 : 10246)!;
        Assert.Equal(ice ? 2000 : 93.75, effective.Utility!.Amount);
        f.Tick();
        f.Tick(seconds);
        var delivered = Assert.Single(f.Tick().Changes);
        int second = delivered.Item.Quantity - first;
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, delivered.Item.FlagId);
        Assert.InRange(second, ice ? 2 : 937, ice ? 2 : 938);
        var log = Assert.Single(restarted.Economy.Inspect(world => world.Set<SavedPersonalMiningLog>().ToArray()));
        Assert.Equal(first + second, log.Quantity);
        Assert.Equal(0, log.Wasted);
        var points = f.Base.Db.Services.GetRequiredService<IPilotFreeSkillPoints>();
        Assert.Equal(0, points.Balance(f.Pilot));
        f.Base.Clock.Seconds += ActivityRewardTickRules.DefaultIntervalSeconds;
        f.Base.Db.Services.GetRequiredService<IActivityRewardTicks>().Advance();
        Assert.True(points.Balance(f.Pilot) > 0);
        using var checkpoint = Assert.IsType<WorldSnapshot>(restarted.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Equal(100000 - first - second, reopened.Set<WorldAsteroid>().Find(Rock)!.Quantity);
        Assert.Equal(first + second, Assert.Single(reopened.Set<SavedPersonalMiningLog>()).Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecallCollectsPartialOreButRequiresACompleteIceCycle(bool ice)
    {
        using var f = Prepare(ice);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long drone = Launch(f, live, ice, DroneOrder.MineRepeatedly);
        int seconds = (int)Math.Ceiling(f.Drones.Profile(f.Pilot, ice ? 43699 : 10246)!.Utility!.DurationMilliseconds / 1000);
        f.Tick();
        f.Base.Clock.Seconds += seconds / 2;
        f.Drones.Order(f.Pilot, [drone], DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        var changes = f.Tick().Changes;
        Assert.Single(changes, c => c.Item.Id == drone && c.Item.FlagId == (int)InventoryFlag.DroneBay);
        var ore = changes.Where(c => c.Item.TypeId == (ice ? 16262 : 1230)).ToArray();
        if (ice)
        {
            Assert.Empty(ore);
            Assert.Equal(100000, live.Economy.Inspect(w => w.Set<WorldAsteroid>().Find(Rock)!.Quantity));
        }
        else
        {
            Assert.InRange(Assert.Single(ore).Item.Quantity, 234, 235);
        }
        Assert.Empty(f.Tick(seconds * 2).Changes);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Null(reopened.Set<WorldDrone>().Find(drone)!.SystemId);
        Assert.Empty(JsonSerializer.Deserialize<DroneWork>(reopened.Set<WorldDrone>().Find(drone)!.WorkJson!)!.Cargo!);
    }

    [Fact]
    public void FullMiningHoldKeepsCargoOnDroneUntilConfiguredCapacityIsAvailable()
    {
        using var f = Prepare(false, capacity: 1);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long drone = Launch(f, live, false, DroneOrder.Mine);
        f.Tick();
        f.Tick(60);
        var delivered = Assert.Single(f.Tick().Changes);
        Assert.Equal(10, delivered.Item.Quantity);
        var pending = live.Economy.Inspect(w => JsonSerializer.Deserialize<DroneWork>(w.Set<WorldDrone>().Find(drone)!.WorkJson!)!);
        Assert.True(pending.Returning);
        Assert.InRange(Assert.Single(pending.Cargo!).Quantity, 458, 459);
        Assert.Empty(f.Tick(60).Changes);
        f.Drones.Release(f.Pilot, f.Ship);
        live.Dispose();
        WorldTuningTests.Activate(f.Base.Db, s => s with { MiningHolds = s.MiningHolds with { GeneralMultiplier = 100 } });
        using LiveWorld restarted = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Reconnect, null, new HashSet<long>(), f.Now);
        var complete = Assert.Single(f.Tick().Changes);
        Assert.InRange(complete.Item.Quantity, 468, 469);
        Assert.Empty(f.Tick(60).Changes);
    }

    [Fact]
    public void WarmMiningAndObserverReadsUseNoSqlOrFittingCalculations()
    {
        using var f = Prepare(false);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        using ResidentFitCache fits = new(new(f.Base.Db.Path));
        _ = Launch(f, live, false, DroneOrder.MineRepeatedly);
        f.Tick();
        f.Tick(60);
        f.Tick();
        f.Tick();
        using var metrics = SessionPerformance.Begin("resident mining drone cycles", _ => { }, force: true)!;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            f.Tick(60);
            f.Tick();
            f.Tick();
            for (int observer = 0; observer < 5; observer++)
            {
                Assert.Single(f.Drones.List(System, f.Now));
            }
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
