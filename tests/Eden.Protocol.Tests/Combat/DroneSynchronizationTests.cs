using System.Text.Json;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol.Docking;
using Eden.Protocol.Drones;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using Eden.World.Combat.Npcs;
using Eden.World.Drones;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class DroneSynchronizationTests(ITestOutputHelper output)
{
    private const int Brutix = 16229;
    private const int BrutixNavyIssue = 33151;
    private const int HammerheadTwo = 2185;
    private const int SystemId = 30000144;

    private sealed record NativeInventoryUpdate(InventoryRow Item, IReadOnlyDictionary<InventoryColumn, long> Previous);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(7, 5)]
    public void LaunchNotificationsRemoveEveryLaunchedDroneFromTheNativeBayCache(int stored, int launched)
    {
        using Fixture f = new(BrutixNavyIssue);
        long stack = f.Bay(HammerheadTwo, stored);
        f.Space();
        using var scope = f.Base.Db.Services.CreateScope();
        using var context = Context(f, scope.ServiceProvider, 1000001);
        context.Flight.Snapshot();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var result = router.Call(
            context,
            "ship",
            "LaunchDrones",
            new object?[]
            {
                new List<object?> { new object?[] { stack, (long)launched } },
                null,
                false,
            },
            _ => { }
        );
        var launch = Assert.IsType<DroneLaunchReply>(Assert.IsType<PilotCallResult.Success>(result).Reply);
        Assert.DoesNotContain(stack, launch.Launches.Single().DroneIds);
        var changes = context.Notifications.OfType<ItemChanged>().ToArray();
        output.WriteLine(
            "DRONE_INVENTORY=" + JsonSerializer.Serialize(changes.Select(change => new NativeInventoryUpdate(change.Item, change.Previous)))
        );
        Assert.All(
            changes,
            change =>
            {
                if (change.Item.Quantity < 0)
                {
                    Assert.DoesNotContain(InventoryColumn.Quantity, change.Previous.Keys);
                }
            }
        );
        var source = Assert.Single(changes, change => change.Item.Id == stack);
        Assert.Equal(context.Pilot.ShipId, source.Previous.GetValueOrDefault(InventoryColumn.Location, context.Pilot.ShipId));
        Assert.Equal((int)InventoryFlag.DroneBay, source.Previous.GetValueOrDefault(InventoryColumn.Flag, (int)InventoryFlag.DroneBay));
        Assert.Equal(stored, source.Previous.GetValueOrDefault(InventoryColumn.StackSize, source.Item.Quantity));
        var bayIds = f
            .Base.Inventory.List(f.Pilot, f.Ship)
            .Where(item => item.FlagId == (int)InventoryFlag.DroneBay)
            .Select(item => item.Id);
        var inSpace = context.Flight.VisibleDrones.Select(drone => drone.Id).Except(bayIds).Order().ToArray();
        Assert.Equal(launch.Launches.Single().DroneIds.Order(), inSpace);
        Assert.Equal(launched, inSpace.Length);
        Assert.Equal(
            stored - launched,
            f.Base.Inventory.List(f.Pilot, f.Ship).Where(item => item.FlagId == (int)InventoryFlag.DroneBay).Sum(item => item.Quantity)
        );
        using WorldDbContext reopened = new(f.Base.Db.Path);
        Assert.Equal(
            stored,
            reopened.Set<OwnedItem>().Where(item => item.OwnerId == f.Pilot && item.TypeId == HammerheadTwo).Sum(item => item.Quantity)
        );
        Assert.Equal(launched, reopened.Set<WorldDrone>().Count(drone => drone.PilotId == f.Pilot && drone.SystemId != null));
        if (stored == launched)
        {
            Assert.Null(reopened.Set<OwnedItem>().Find(stack));
        }
    }

    [Theory]
    [InlineData(Brutix)]
    [InlineData(BrutixNavyIssue)]
    public async Task NativeUndockKeepsAllFiveDronesOrbitingAndRefreshesIdleObservers(int hullType)
    {
        using Fixture f = new(hullType);
        long stack = f.Bay(HammerheadTwo, 5);
        using var ownerScope = f.Base.Db.Services.CreateScope();
        using var owner = Context(f, ownerScope.ServiceProvider, 1000001);
        Assert.IsType<PilotCallResult.Success>(owner.Flight.Execute(new FlightCommand.Undock(f.Ship)));
        owner.Flight.Snapshot();
        int systemId = owner.Pilot.SystemId;
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            Assert.Null(world.Positions.Find(f.Ship)!.SystemId);
        }
        var wing = f.Base.Db.Store.FindPilot(1000002)!;
        f.Base.Db.Store.SetPosition(wing.ShipId, owner.Pilot.Position! + new SpacePosition(10_000, 0, 0), systemId);
        using var observerScope = f.Base.Db.Services.CreateScope();
        using var observer = Context(f, observerScope.ServiceProvider, 1000002);
        observer.Flight.Snapshot();
        Assert.Empty(observer.Flight.VisibleDrones);
        var services = ownerScope.ServiceProvider;
        var launch = Launch(services, owner, stack);
        Assert.Equal(5, owner.Flight.Updates.Count(u => u.Command is DestinyCommand.Follow { Mode: NavigationMode.Orbit }));
        var initial = f.Drones.List(systemId, f.Now).ToDictionary(drone => drone.Id, drone => drone.Position);
        Assert.Equal(5, initial.Count);
        observer.Flight.RefreshDrones();
        Assert.Equal(5, observer.Flight.Updates.Count(u => u.Command is DestinyCommand.Follow { Mode: NavigationMode.Orbit }));
        owner.Flight.Updates.Clear();
        observer.Flight.Updates.Clear();
        f.Base.Clock.Seconds++;
        services.GetRequiredService<DroneHandler>().Advance(owner);
        observer.Flight.RefreshDrones();
        foreach (var session in new[] { owner, observer })
        {
            Assert.All(session.Flight.VisibleDrones, d => Assert.NotEqual(initial[d.Id], d.Position));
            Assert.DoesNotContain(
                session.Flight.Updates,
                u => u.Command is DestinyCommand.Follow or DestinyCommand.SetBallPosition or DestinyCommand.SetBallVelocity
            );
        }
        for (int cycle = 0; cycle < 4; cycle++)
        {
            owner.Flight.Updates.Clear();
            observer.Flight.Updates.Clear();
            f.Base.Clock.Seconds++;
            await Tick(f, ownerScope.ServiceProvider, owner);
            await Tick(f, observerScope.ServiceProvider, observer);
            var contacts = f.Drones.List(systemId, f.Now);
            Assert.Equal(5, contacts.Count);
            Assert.All(contacts, drone => Assert.Equal(f.Ship, drone.ShipId));
            Assert.All(contacts, drone => Assert.True(drone.Velocity.Length > 0));
            Assert.All(contacts, drone => Assert.NotEqual(initial[drone.Id], drone.Position));
            Assert.Equal(contacts, observer.Flight.VisibleDrones);
            Assert.Equal(50, f.Drones.Control(f.Pilot).UsedBandwidth);
        }
        var ids = launch.Launches.Single().DroneIds;
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            var target = owner.Pilot.Position! + new SpacePosition(3000, 0, 0);
            (npc.X, npc.Y, npc.Z) = (target.X, target.Y, target.Z);
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now)
        );
        Assert.All(f.Drones.List(systemId, f.Now), drone => Assert.Equal(DroneActivity.Idle, drone.Activity));
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.SystemId = systemId;
            world.SaveChanges();
        }
        owner.Flight.RefreshNpcs();
        var targeting = services.GetRequiredService<TargetingHandler>();
        targeting.CallWire(owner, "dogmaIM", "AddTarget", [f.Npc.Id]);
        f.Base.Clock.Seconds += 30;
        targeting.Advance(owner);
        Assert.True(targeting.IsLocked(f.Npc.Id));
        var group = ids.Cast<object?>().ToList();
        Command(services, owner, "CmdEngage", group, f.Npc.Id);
        Assert.All(f.Drones.List(systemId, f.Now), drone => Assert.Equal(DroneActivity.Fighting, drone.Activity));
        Command(services, owner, "CmdReturnHome", new List<object?> { ids[0] });
        Assert.Equal(DroneActivity.Returning, owner.Flight.VisibleDrones.Single(d => d.Id == ids[0]).Activity);
        Command(services, owner, "CmdReturnBay", group);
        for (int cycle = 0; cycle < 30 && owner.Flight.VisibleDrones.Count > 0; cycle++)
        {
            f.Base.Clock.Seconds++;
            await Tick(f, services, owner);
        }
        Assert.Empty(owner.Flight.VisibleDrones);
        Assert.Empty(f.Drones.List(systemId, f.Now));
        await Tick(f, observerScope.ServiceProvider, observer);
        Assert.Empty(observer.Flight.VisibleDrones);
        var relaunched = f.Drones.Launch(f.Pilot, ids.Select(id => new DroneStack(id, 1)).ToArray(), f.Now);
        Assert.Equal(ids.Order(), relaunched.Launches.SelectMany(group => group.DroneIds).Order());
        f.Drones.Order(f.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        Assert.Equal(5, f.Tick().Changes.Count);
        using WorldDbContext reopened = new(f.Base.Db.Path);
        Assert.Equal(
            ids.Order(),
            reopened
                .Set<OwnedItem>()
                .Where(item => item.LocationId == f.Ship && item.FlagId == (int)InventoryFlag.DroneBay)
                .Select(item => item.Id)
                .Order()
        );
    }

    internal static PilotCallResult Bound(
        IServiceProvider services,
        PilotCallContext pilot,
        string service,
        string method,
        params object?[] args
    )
    {
        object location =
            service == "ship"
                ? new object?[] { (long)pilot.Pilot.LocationId, pilot.Pilot.Position is null ? 15L : 5L }
                : (long)pilot.Pilot.SystemId;
        return services
            .GetRequiredService<PilotCallRouter>()
            .Call(pilot, service, "MachoBindObject", new object?[] { location, new object?[] { method, args } }, _ => { });
    }

    internal static BlueModel Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return Assert.IsType<BoundPilotReply>(((PilotCallResult.Success)result).Reply).Reply;
    }

    internal static DroneLaunchReply Launch(IServiceProvider services, PilotCallContext pilot, long stack) =>
        Assert.IsType<DroneLaunchReply>(
            Reply(Bound(services, pilot, "ship", "LaunchDrones", new List<object?> { new object?[] { stack, 5L } }, null, false))
        );

    internal static void Command(IServiceProvider services, PilotCallContext pilot, string method, params object?[] args) =>
        Assert.IsType<DroneCommandReply>(Reply(Bound(services, pilot, "entity", method, args)));

    internal static PilotCallContext Context(Fixture f, IServiceProvider services, long account)
    {
        var pilot = f.Base.Db.Store.FindPilot(account)!;
        PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            services.GetRequiredService<FlightSessionFactory>().Create,
            account,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    private static async Task Tick(Fixture f, IServiceProvider services, PilotCallContext context)
    {
        PilotSessionOutput writer = new(context, context.AccountId, context.AccountId, _ => Task.CompletedTask, _ => { });
        PilotSessionTick tick = new(
            f.Base.Clock,
            services.GetRequiredService<PilotUpdateCoordinator>(),
            services.GetRequiredService<SkillHandler>(),
            services.GetRequiredService<IAsteroidField>()
        );
        await tick.AdvanceAsync(context, writer, _ => { }, context.AccountId, context.AccountId);
    }
}
