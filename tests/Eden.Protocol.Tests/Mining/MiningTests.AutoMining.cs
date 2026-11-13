using Eden.Data.World.Combat;
using Eden.Data.World.Mining;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class MiningTests
{
    [Fact]
    public void AutoMiningRetargetsAfterSharedDepletionAndStopsOnceAtTheDeadline()
    {
        const long first = Fixture.Asteroid;
        const long next = first + 1;
        using Fixture f = new(useTestClock: true);
        using ResidentFitCache fits = new(new(f.Db.Path));
        AddAutoMiningRocks(f, 63);
        var (context, _, _, module) = f.Client(locked: false);
        var (wing, _, wingMining, wingModule) = f.Client(1000002);
        using var scope = f.Db.Services.CreateScope();
        var services = scope.ServiceProvider;
        var targeting = services.GetRequiredService<TargetingHandler>();
        var mining = services.GetRequiredService<MiningHandler>();
        var updates = services.GetRequiredService<PilotUpdateCoordinator>();
        var asteroids = services.GetRequiredService<IAsteroidField>();
        var ledger = services.GetRequiredService<IPersonalMiningLedger>();
        var live = LiveWorld.Find(f.Db.Path)!;
        var controller = CreateController(f, targeting, mining);
        updates.Enter(context);

        Assert.Equal("Veldspar", Command("SaveFilter", "veldspar, Veldspar").OreFilter);
        Assert.Equal("Locking asteroid", Command("Start").Status);
        Assert.False(targeting.IsLocked(first));
        Assert.False(mining.IsActive(module));
        using (var metrics = SessionPerformance.Begin("pending automatic lock", _ => { }, force: true)!)
        {
            f.Clock.Milliseconds += 1000;
            controller.Advance(context);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        f.Clock.Milliseconds += 9000;
        targeting.Advance(context);
        controller.Advance(context);
        Assert.True(targeting.IsLocked(first));
        Assert.Contains(
            context.Notifications.OfType<TargetChanged>(),
            change => change.Change == TargetChange.Added && change.TargetId == first
        );
        Assert.Equal(first, mining.ActiveEffect(context.Pilot, module)!.TargetId);

        // Both pilots paid a cycle, but only the first settlement gets the last ore.
        Assert.Null(wingMining.CallWire(wing, "dogmaIM", "Activate", [wingModule, "miningLaser", first, 0L]));
        f.Clock.Milliseconds += 10000;
        wingMining.Advance(wing);
        Assert.Equal(63, Assert.Single(wing.Notifications.OfType<OreMined>()).Yield.Extracted);
        Assert.Empty(ledger.Read(context.Pilot.PilotId));
        context.Flight.RefreshAsteroids(asteroids.List(context.Pilot.SystemId));
        targeting.Advance(context);
        mining.Advance(context);
        controller.Advance(context);
        Assert.False(targeting.IsLocked(first));
        Assert.True(targeting.IsPending(next));
        f.Clock.Milliseconds += 10000;
        targeting.Advance(context);
        controller.Advance(context);
        Assert.Equal(next, mining.ActiveEffect(context.Pilot, module)!.TargetId);

        f.Clock.Milliseconds += 10000;
        var stopped = Command("Stop");
        Assert.False(stopped.Running);
        Assert.False(mining.IsActive(module));
        Assert.False(targeting.IsLocked(next));
        Assert.Equal(63, Assert.Single(context.Notifications.OfType<OreMined>()).Yield.Extracted);
        Assert.Equal(63, Assert.Single(ledger.Read(context.Pilot.PilotId)).Quantity.Collected);
        Assert.Equal(63, Assert.Single(ledger.Read(wing.Pilot.PilotId)).Quantity.Collected);
        f.Clock.Milliseconds += 10_000;
        mining.Advance(context);
        controller.Advance(context);
        Assert.Equal(63, Assert.Single(ledger.Read(context.Pilot.PilotId)).Quantity.Collected);
        Assert.Null(live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(module)));

        f.Disconnect(context);
        f.Disconnect(wing);
        f.Reopen();
        var saved = f.Db.Services.GetRequiredService<IPilotAutoMining>();
        Assert.Equal("Veldspar", saved.ReadFilter(90000001).Text);
        Assert.Equal("", saved.ReadFilter(90000002).Text);
        Assert.Equal(63, Assert.Single(ledger.Read(90000001)).Quantity.Collected);

        AutoMiningStatus Command(string method, params object?[] args) =>
            Assert.IsType<AutoMiningStatus>(
                Assert.IsType<PilotCallResult.Success>(controller.Call(context, AutoMiningHandler.Service, method, args)).Reply
            );
    }

    [Fact]
    public void AutoMiningPreservesManualCyclesAndHonorsManualModuleStop()
    {
        using Fixture f = new(useTestClock: true);
        var (context, targeting, mining, module) = f.Client();
        var controller = CreateController(f, targeting, mining);
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 100L]));
        Assert.IsType<PilotCallResult.Success>(Call("Start"));
        Assert.IsType<PilotCallResult.Success>(Call("Stop"));
        Assert.True(mining.IsActive(module));
        Assert.True(targeting.IsLocked(Fixture.Asteroid));
        Assert.Null(mining.CallWire(context, "dogmaIM", "Deactivate", [module, "miningLaser"]));

        Assert.IsType<PilotCallResult.Success>(Call("Start"));
        Assert.True(mining.IsAutomatic(module));
        f.Clock.Milliseconds += 10_000;
        Assert.Null(mining.CallWire(context, "dogmaIM", "Deactivate", [module, "miningLaser"]));
        Assert.Single(context.Notifications.OfType<OreMined>());
        controller.Advance(context);
        Assert.False(mining.IsActive(module));
        Assert.IsType<PilotCallResult.Success>(Call("Start"));
        Assert.True(mining.IsAutomatic(module));
        controller.Cancel(context);
        Assert.False(mining.IsActive(module));
        Assert.True(targeting.IsLocked(Fixture.Asteroid));
        Assert.Single(context.Notifications.OfType<OreMined>());

        PilotCallResult Call(string method) => controller.Call(context, AutoMiningHandler.Service, method, []);
    }

    [Fact]
    public void AutoMiningIgnoresOtherOreAndOutOfRangeRocksAndRejectsInvalidFilters()
    {
        using Fixture f = new(useTestClock: true);
        AddAutoMiningRocks(f, 10000);
        var (context, targeting, mining, _) = f.Client(locked: false);
        var disabled = CreateController(f, targeting, mining, enabled: false);
        Assert.IsType<PilotCallResult.Failure>(disabled.Call(context, AutoMiningHandler.Service, "Start", []));
        var enabled = CreateController(f, targeting, mining);
        Assert.IsType<PilotCallResult.Success>(Call("SaveFilter", "Scordite"));
        Assert.IsType<PilotCallResult.Failure>(Call("SaveFilter", "Definitely not a resource"));
        var result = Assert.IsType<AutoMiningStatus>(Assert.IsType<PilotCallResult.Success>(Call("Start")).Reply);
        Assert.Equal("Scordite", result.OreFilter);
        Assert.Equal("Waiting for matching ore in range", result.Status);
        Assert.False(targeting.IsPending(Fixture.Asteroid));
        Assert.False(targeting.IsPending(Fixture.Asteroid + 1));
        Assert.False(targeting.IsPending(Fixture.Asteroid + 2));

        PilotCallResult Call(string method, params object?[] args) => enabled.Call(context, AutoMiningHandler.Service, method, args);
    }

    [Fact]
    public void AutoMiningDoesNotRelockAnAsteroidThePilotUnlocked()
    {
        using Fixture f = new(useTestClock: true);
        AddAutoMiningRocks(f, 10000);
        var (context, targeting, mining, module) = f.Client(locked: false);
        var controller = CreateController(f, targeting, mining);
        Assert.IsType<PilotCallResult.Success>(controller.Call(context, AutoMiningHandler.Service, "Start", []));
        f.Clock.Milliseconds += 10_000;
        targeting.Advance(context);
        controller.Advance(context);
        Assert.True(mining.IsAutomatic(module));
        Assert.Null(targeting.CallWire(context, "dogmaIM", "RemoveTarget", [Fixture.Asteroid]));
        f.Clock.Milliseconds += 1000;
        mining.Advance(context);
        controller.Advance(context);
        Assert.False(targeting.IsPending(Fixture.Asteroid));
        Assert.True(targeting.IsPending(Fixture.Asteroid + 1));

        controller.Cancel(context);
        Assert.False(targeting.IsPending(Fixture.Asteroid + 1));
    }

    private static AutoMiningHandler CreateController(Fixture f, TargetingHandler targeting, MiningHandler mining, bool enabled = true) =>
        new(
            f.Clock,
            f.Db.Services.GetRequiredService<IPilotAutoMining>(),
            f.Db.Services.GetRequiredService<IPilotFitting>(),
            targeting,
            mining,
            new TestAutoMiningAvailability(enabled)
        );

    private sealed record TestAutoMiningAvailability(bool AutoMiningEnabled) : IAutoMiningAvailability;

    private static void AddAutoMiningRocks(Fixture f, int quantity)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (460,'Scordite',25);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (1228,'Scordite',460);
                INSERT INTO TypeAttribute VALUES (1228,552,100);
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','1228','{{"published":1,"volume":0.15}}');
                """
            );
        }
        using WorldDbContext world = new(f.Db.Path);
        var rows = world.Set<WorldAsteroid>();
        var first = rows.Find(Fixture.Asteroid)!;
        first.Quantity = quantity;
        Add(Fixture.Asteroid + 1, first.TypeId, first.X + 4000);
        Add(Fixture.Asteroid + 2, 1228, first.X + 20000);
        world.SaveChanges();

        void Add(long id, int typeId, double x) =>
            rows.Add(
                new()
                {
                    Id = id,
                    BeltId = first.BeltId,
                    SystemId = first.SystemId,
                    TypeId = typeId,
                    Quantity = 10000,
                    X = x,
                    Y = first.Y,
                    Z = first.Z,
                    Radius = first.Radius,
                }
            );
    }
}
