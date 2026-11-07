using System.Text.Json;
using Eden.Data.Drones;
using Eden.Data.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Drones;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using static Eden.Protocol.Tests.Combat.DroneSynchronizationTests;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class DroneRecoveryTests(ITestOutputHelper output)
{
    private const int HammerheadTwo = 2185;
    private const int Brutix = 16229;
    private const int SystemId = 30000144;

    [Theory]
    [InlineData("cargo")]
    [InlineData("scoopBay")]
    [InlineData("recall")]
    public void ScoopingAndRecallingFiveDronesQueuesRemovalForOwnerAndObserver(string route)
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 5);
        if (route != "recall")
        {
            f.Drones.Release(f.Pilot, f.Ship);
        }
        var wing = f.Base.Db.Store.FindPilot(1000002)!;
        var position = f.Base.Db.Store.FindPilot(1000001)!.Position!;
        f.Base.Db.Store.SetPosition(wing.ShipId, position + new SpacePosition(100, 0, 0), SystemId);
        using var ownerScope = f.Base.Db.Services.CreateScope();
        using var observerScope = f.Base.Db.Services.CreateScope();
        using var owner = Context(f, ownerScope.ServiceProvider, 1000001);
        using var observer = Context(f, observerScope.ServiceProvider, 1000002);
        owner.Flight.Snapshot();
        observer.Flight.Snapshot();
        Assert.Equal(5, observer.Flight.VisibleDrones.Count);
        owner.Flight.Updates.Clear();
        observer.Flight.Updates.Clear();
        foreach (long id in ids)
        {
            if (route == "recall")
            {
                Command(ownerScope.ServiceProvider, owner, "CmdReturnBay", new List<object?> { id });
            }
            else
            {
                object?[] args = route == "scoopBay" ? [new object?[] { id }] : [id];
                Reply(Bound(ownerScope.ServiceProvider, owner, "ship", route == "scoopBay" ? "ScoopDrone" : "Scoop", args));
            }
        }
        if (route == "recall")
        {
            f.Base.Clock.Seconds++;
            ownerScope.ServiceProvider.GetRequiredService<DroneHandler>().Advance(owner);
        }
        observer.Flight.RefreshDrones();
        foreach (var viewer in new[] { owner, observer })
        {
            Assert.Empty(viewer.Flight.VisibleDrones);
            var removals = viewer.Flight.Updates.Where(u => u.Command is DestinyCommand.RemoveBall).ToArray();
            Assert.Equal(ids.Order(), removals.Select(u => ((DestinyCommand.RemoveBall)u.Command).Id).Order());
            Assert.All(removals, u => Assert.False(((DestinyCommand.RemoveBall)u.Command).Explode));
            var wire = removals.Select(u => BlueMarshal.Decode(BlueMarshal.Encode(u))).ToArray();
            output.WriteLine("DRONE_REMOVAL=" + JsonSerializer.Serialize(new { ids, wire }));
            // RealFlushState appends its own explosion map to these server arguments.
            foreach (var value in wire)
            {
                var update = Assert.IsType<object?[]>(value);
                var command = Assert.IsType<object?[]>(update[1]);
                Assert.Equal("RemoveBalls", BlueMarshal.Text(command[0]));
                var arguments = Assert.IsType<object?[]>(command[1]);
                Assert.Single(arguments);
            }
            viewer.Flight.Updates.Clear();
            viewer.Flight.RefreshDrones();
            Assert.DoesNotContain(viewer.Flight.Updates, u => u.Command is DestinyCommand.RemoveBall);
        }
        using WorldDbContext reopened = new(f.Base.Db.Path);
        Assert.All(
            ids,
            id =>
            {
                var item = reopened.Set<OwnedItem>().Find(id)!;
                Assert.Equal(f.Ship, item.LocationId);
                Assert.Equal((int)(route == "cargo" ? InventoryFlag.Cargo : InventoryFlag.DroneBay), item.FlagId);
                Assert.Equal(1, item.Quantity);
                Assert.Null(reopened.Set<WorldDrone>().Find(id)!.SystemId);
            }
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverviewTupleAndPanelListScoopTheSameIndividual(bool tuple)
    {
        using Fixture f = new();
        long id = f.Launch(quantity: 1)[0];
        f.Drones.Release(f.Pilot, f.Ship);
        using var scope = f.Base.Db.Services.CreateScope();
        using var owner = Context(f, scope.ServiceProvider, 1000001);
        owner.Flight.Snapshot();
        object selection = tuple ? new object?[] { id } : new List<object?> { id };
        var decoded = BlueMarshal.Decode(BlueMarshal.Encode(new object?[] { selection }));
        var args = Assert.IsType<object?[]>(decoded);
        Assert.IsType<DroneCommandReply>(Reply(Bound(scope.ServiceProvider, owner, "ship", "ScoopDrone", args)));
        Assert.Empty(owner.Flight.VisibleDrones);
        using WorldDbContext reopened = new(f.Base.Db.Path);
        var item = reopened.Set<OwnedItem>().Find(id)!;
        Assert.Equal(f.Ship, item.LocationId);
        Assert.Equal(f.Pilot, item.OwnerId);
        Assert.Equal((int)InventoryFlag.DroneBay, item.FlagId);
        Assert.Equal(1, item.Quantity);
        Assert.Null(reopened.Set<WorldDrone>().Find(id)!.SystemId);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("text")]
    [InlineData("negative")]
    public void InvalidOverviewTupleCannotMoveAnyDrones(string invalid)
    {
        using Fixture f = new();
        long id = f.Launch(quantity: 1)[0];
        f.Drones.Release(f.Pilot, f.Ship);
        var before = f.Drones.List(SystemId, f.Now);
        object?[] selection = invalid switch
        {
            "duplicate" => [id, id],
            "empty" => [],
            "text" => [id, "invalid"],
            _ => [id, -1L],
        };
        using var scope = f.Base.Db.Services.CreateScope();
        using var owner = Context(f, scope.ServiceProvider, 1000001);
        owner.Flight.Snapshot();
        var args = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(new object?[] { selection })));
        Assert.IsType<PilotCallResult.Failure>(Bound(scope.ServiceProvider, owner, "ship", "ScoopDrone", args));
        Assert.Equal(before, f.Drones.List(SystemId, f.Now));
        Assert.Empty(owner.Notifications.OfType<ItemChanged>());
        using WorldDbContext reopened = new(f.Base.Db.Path);
        Assert.Equal(SystemId, reopened.Set<OwnedItem>().Find(id)!.LocationId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BoundApproachAndOrbitAcceptControlledAndLostDroneContacts(bool controlled, bool orbit)
    {
        using Fixture f = new();
        long id = f.Launch(quantity: 1)[0];
        if (!controlled)
        {
            f.Drones.Release(f.Pilot, f.Ship);
        }
        var origin = f.Base.Db.Store.FindPilot(1000001)!.Position!;
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            var drone = world.Set<WorldDrone>().Find(id)!;
            (drone.X, drone.Y, drone.Z) = (origin.X + 10_000, origin.Y, origin.Z);
            world.SaveChanges();
        }
        using var scope = f.Base.Db.Services.CreateScope();
        using var owner = Context(f, scope.ServiceProvider, 1000001);
        owner.Flight.Snapshot();
        string method = orbit ? "CmdOrbit" : "CmdFollowBall";
        Reply(Bound(scope.ServiceProvider, owner, "beyonce", method, id, orbit ? 1000L : 0L));
        Assert.Contains(
            owner.Flight.Updates,
            u =>
                u.Command is DestinyCommand.Follow follow
                && follow.Id == f.Ship
                && follow.TargetId == id
                && follow.Mode == (orbit ? NavigationMode.Orbit : NavigationMode.Follow)
        );
        for (int cycle = 0; cycle < 5; cycle++)
        {
            f.Base.Clock.Seconds++;
            owner.Flight.Checkpoint();
        }
        Assert.True(owner.Pilot.Position!.DistanceTo(origin + new SpacePosition(10_000, 0, 0)) < 10_000);
        owner.Flight.Updates.Clear();
        Assert.IsType<PilotCallResult.UnsupportedCall>(Bound(scope.ServiceProvider, owner, "beyonce", method, long.MaxValue, 0L));
        Assert.DoesNotContain(owner.Flight.Updates, u => u.Command is DestinyCommand.Follow);
        // Removing the real drone must terminate the course on the next flight tick.
        f.Drones.Release(f.Pilot, f.Ship);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Set<WorldDrone>().Find(id)!.SystemId = null;
            world.SaveChanges();
        }
        owner.Flight.RefreshDrones();
        owner.Flight.Updates.Clear();
        f.Base.Clock.Seconds++;
        owner.Flight.Checkpoint();
        Assert.Contains(owner.Flight.Updates, u => u.Command is DestinyCommand.Stop or DestinyCommand.GotoDirection);
    }

    [Fact]
    public void BoundApproachReachesAForeignLostDroneAndAllowsScoopingIt()
    {
        using Fixture f = new();
        long id = f.Launch(quantity: 1)[0];
        f.Drones.Release(f.Pilot, f.Ship);
        var drone = Assert.Single(f.Drones.List(SystemId, f.Now));
        var wing = f.Base.Db.Store.FindPilot(1000002)!;
        f.Base.Db.Store.SetPosition(wing.ShipId, drone.Position + new SpacePosition(5000, 0, 0), SystemId);
        using var scope = f.Base.Db.Services.CreateScope();
        using var collector = Context(f, scope.ServiceProvider, 1000002);
        collector.Flight.Snapshot();
        Reply(Bound(scope.ServiceProvider, collector, "beyonce", "CmdFollowBall", id, 0L));
        for (
            int cycle = 0;
            cycle < 200
                && collector.Pilot.Position!.DistanceTo(drone.Position) - collector.Pilot.Flight.ShipRadius - drone.Profile.Radius
                    > DroneRules.RecallRange;
            cycle++
        )
        {
            f.Base.Clock.Seconds++;
            collector.Flight.Checkpoint();
        }
        Reply(Bound(scope.ServiceProvider, collector, "ship", "Scoop", id));
        Assert.Empty(collector.Flight.VisibleDrones);
        using WorldDbContext reopened = new(f.Base.Db.Path);
        var item = reopened.Set<OwnedItem>().Find(id)!;
        Assert.Equal(wing.PilotId, item.OwnerId);
        Assert.Equal(wing.ShipId, item.LocationId);
        Assert.Equal((int)InventoryFlag.Cargo, item.FlagId);
        Assert.Equal(1, item.Quantity);
    }

    [Fact]
    public void DockBoardAndUndockLeavesOldDronesVisibleAndOnlyExplicitReconnectRestoresTheirPanelEntries()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(f.Base.CatalogPath))
        {
            var attributes = catalog.FittingAttributes(Brutix);
            foreach (var (skill, level) in InventoryDefinitions.RequiredSkills)
            {
                if (attributes.TryGetValue(skill, out var id) && id > 0)
                {
                    f.Base.Train((int)id, (int)attributes[level]);
                }
            }
        }
        long original = f.Ship;
        long stack = f.Bay(HammerheadTwo, 5);
        long next;
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            next = InventoryPersistence.NextId(world);
            world.Ships.Add(
                new Ship
                {
                    Id = next,
                    OwnerId = f.Pilot,
                    TypeId = Brutix,
                    StationId = (int)f.Base.StationId,
                    Name = "Replacement",
                }
            );
            world.SaveChanges();
        }
        using var scope = f.Base.Db.Services.CreateScope();
        var services = scope.ServiceProvider;
        using var owner = Context(f, services, 1000001);
        owner.Flight.Execute(new FlightCommand.Undock(original));
        owner.Flight.Snapshot();
        var ids = Launch(services, owner, stack).Launches.Single().DroneIds;
        var group = ids.Cast<object?>().ToList();
        var controlled = Snapshot(owner);
        Assert.Equal(ids.Order(), controlled.Drones!.Select(d => d.Drone.Id).Order());
        Assert.IsNotType<PilotCallResult.Success>(owner.Flight.Execute(new FlightCommand.Dock(f.Base.StationId, next)));
        Assert.Equal(50, f.Drones.Control(f.Pilot).UsedBandwidth);

        var before = f.Drones.List(owner.Pilot.SystemId, f.Now);
        Assert.All(before, d => Assert.Equal(original, d.ShipId));
        Assert.Equal(PilotCallResult.None, owner.Flight.Execute(new FlightCommand.Dock(owner.Pilot.StationId, original)));
        Assert.All(f.Drones.List(owner.Pilot.SystemId, f.Now), d => Assert.Equal(0, d.ShipId));
        Reply(Bound(services, owner, "ship", "Board", next, original));
        Assert.Equal(PilotCallResult.None, owner.Flight.Execute(new FlightCommand.Undock(next)));
        var lost = Snapshot(owner);
        Assert.Empty(lost.Drones!);
        Assert.Equal(ids.Order(), lost.Slims.OfType<DroneSlimItem>().Select(d => d.Drone.Id).Order());
        Assert.Equal(0, f.Drones.Control(f.Pilot).UsedBandwidth);
        Assert.IsType<PilotCallResult.Failure>(Bound(services, owner, "entity", "CmdReturnBay", group));
        Assert.All(
            owner.Flight.VisibleDrones,
            d =>
            {
                Assert.Equal(before.Single(old => old.Id == d.Id).Position, d.Position);
                Assert.Equal(SpacePosition.Zero, d.Velocity);
            }
        );
        owner.Flight.Updates.Clear();
        Command(services, owner, "CmdReconnectToDrones", group);
        var reconnect = owner.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.DroneStateChanged>().ToArray();
        Assert.All(owner.Flight.VisibleDrones, d => Assert.Equal(next, d.ShipId));
        Assert.Equal(50, f.Drones.Control(f.Pilot).UsedBandwidth);
        owner.Flight.Updates.Clear();
        Command(services, owner, "CmdAbandonDrone", group);
        var release = owner.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.DroneStateChanged>().ToArray();
        Assert.Equal(5, release.Length);
        Assert.All(release, d => Assert.True(d.State.Removed));
        Assert.DoesNotContain(owner.Flight.Updates, u => u.Command is DestinyCommand.RemoveBall);
        Assert.Equal(0, f.Drones.Control(f.Pilot).UsedBandwidth);
        var released = Snapshot(owner);
        Assert.Empty(released.Drones!);
        var recovered = released.Slims.OfType<DroneSlimItem>().Select(s => Fields(s)).ToArray();
        Assert.All(
            recovered,
            s =>
            {
                Assert.Equal(f.Pilot, s["ownerID"]);
                Assert.Null(s["controllerID"]);
                Assert.Null(s["controllerOwnerID"]);
            }
        );
        output.WriteLine(
            "DRONE_PANEL="
                + JsonSerializer.Serialize(
                    new
                    {
                        pilot = f.Pilot,
                        original,
                        next,
                        system = owner.Pilot.SystemId,
                        ids,
                        controlled = controlled.Drones!.Select(d => Fields(d)),
                        lost = lost.Drones!.Select(d => Fields(d)),
                        slims = recovered,
                        release = release.Select(d => d.State.Arguments),
                        reconnect = reconnect.Select(d => d.State.Arguments),
                    }
                )
        );
        Reply(Bound(services, owner, "ship", "Scoop", ids[0]));
        Assert.IsType<DroneCommandReply>(Reply(Bound(services, owner, "ship", "ScoopDrone", new List<object?> { ids[1] })));
        Assert.Equal(3, owner.Flight.VisibleDrones.Count);
        using WorldDbContext reopened = new(f.Base.Db.Path);
        Assert.Equal((int)InventoryFlag.Cargo, reopened.Set<OwnedItem>().Find(ids[0])!.FlagId);
        Assert.Equal((int)InventoryFlag.DroneBay, reopened.Set<OwnedItem>().Find(ids[1])!.FlagId);
        Assert.All(ids.Take(2), id => Assert.Equal(next, reopened.Set<OwnedItem>().Find(id)!.LocationId));
    }

    private static Dictionary<string, object?> Fields(BlueModel model) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(model.ToBlue()).State);

    private static BallparkSnapshot Snapshot(PilotCallContext pilot)
    {
        pilot.Flight.Updates.Clear();
        pilot.Flight.Snapshot();
        return Assert.Single(pilot.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.SetState>()).Snapshot;
    }

    [Fact]
    public void DistantOrdersAndShutdownUseShipControlRatherThanObserverVisibility()
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 1);
        using var scope = f.Base.Db.Services.CreateScope();
        var services = scope.ServiceProvider;
        using var owner = Context(f, services, 1000001);
        owner.Flight.Snapshot();
        var handler = services.GetRequiredService<DroneHandler>();
        var origin = owner.Pilot.Position!;
        SetDistance(DroneRules.ShutdownDistance);
        Command(services, owner, "CmdReturnHome", ids.Cast<object?>().ToList());
        f.Base.Clock.Seconds++;
        handler.Advance(owner);
        var drone = Assert.Single(f.Drones.List(SystemId, f.Now));
        Assert.Equal(f.Ship, drone.ShipId);
        Assert.True(drone.Position.DistanceTo(origin) < DroneRules.ShutdownDistance);
        Command(services, owner, "CmdReturnBay", ids.Cast<object?>().ToList());
        owner.Flight.Updates.Clear();
        f.Base.Clock.Seconds++;
        handler.Advance(owner);
        Assert.DoesNotContain(owner.Flight.Updates, u => u.Command is DestinyCommand.SetBallPosition or DestinyCommand.Follow);
        SetDistance(DroneRules.ShutdownDistance + 1);
        f.Base.Clock.Seconds++;
        handler.Advance(owner);
        drone = Assert.Single(f.Drones.List(SystemId, f.Now));
        Assert.Equal(0, drone.ShipId);
        Assert.Equal(SpacePosition.Zero, drone.Velocity);
        Assert.Equal(0, f.Drones.Control(f.Pilot).UsedBandwidth);
        SetDistance(NpcEncounterDefinitions.VisibilityRange + 1);
        Assert.Throws<InvalidDataException>(() => f.Drones.Order(f.Pilot, ids, DroneOrder.Reconnect, null, new HashSet<long>(), f.Now));
        SetDistance(NpcEncounterDefinitions.VisibilityRange);
        Command(services, owner, "CmdReconnectToDrones", ids.Cast<object?>().ToList());
        Assert.Equal(f.Ship, Assert.Single(f.Drones.List(SystemId, f.Now)).ShipId);

        void SetDistance(double distance)
        {
            using WorldDbContext world = new(f.Base.Db.Path);
            var saved = world.Set<WorldDrone>().Find(ids[0])!;
            (saved.X, saved.Y, saved.Z) = (origin.X + distance, origin.Y, origin.Z);
            world.SaveChanges();
        }
    }

    [Fact]
    public void NewEngageRequiresLockAndRangeWhileAnAcceptedAttackContinues()
    {
        using Fixture f = new();
        var ids = f.Launch(quantity: 1);
        double range = f.Drones.Control(f.Pilot).Range;
        f.Space(range);
        var locks = new HashSet<long> { f.Npc.Id };
        f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, f.Npc.Id, locks, f.Now);
        locks.Clear();
        f.Space(range + 1);
        f.Tick();
        var pursuit = Assert.Single(f.Drones.List(SystemId, f.Now));
        Assert.Equal(DroneActivity.Fighting, pursuit.Activity);
        Assert.Equal(f.Npc.Id, pursuit.TargetId);
        locks.Add(f.Npc.Id);
        Assert.Throws<InvalidDataException>(() => f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, f.Npc.Id, locks, f.Now));
        Assert.Equal(pursuit, Assert.Single(f.Drones.List(SystemId, f.Now)));
    }

    [Fact]
    public async Task ScoopChecksRangeAndCapacityAndOnlyOnePilotCanRecoverTheSameDamagedDrone()
    {
        using Fixture f = new();
        var ids = f.Launch(HammerheadTwo, 2);
        var wing = f.Base.Db.Store.FindPilot(1000002)!;
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            var saved = world.Set<WorldDrone>().Find(ids[0])!;
            saved.ArmorDamage = 12;
            saved.StructureDamage = 3;
            world.SaveChanges();
        }
        var deployed = f.Drones.List(SystemId, f.Now);
        var contact = deployed.Single(d => d.Id == ids[0]);
        using CatalogDbContext catalog = new(f.Base.CatalogPath);
        double range = DroneRules.RecallRange + catalog.FittingType(wing.ShipTypeId)!.Radius!.Value + contact.Profile.Radius;
        void Approach(double distance) =>
            f.Base.Db.Store.SetPosition(wing.ShipId, contact.Position + new SpacePosition(distance, 0, 0), SystemId);
        Approach(range);
        Assert.Throws<InvalidDataException>(() => f.Drones.Scoop(wing.PilotId, ids, InventoryFlag.Cargo, f.Now));
        Assert.Equal(deployed, f.Drones.List(SystemId, f.Now));
        f.Drones.Release(f.Pilot, f.Ship);
        // Leave room for one of the two selected drones. The entire scoop must roll back.
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = f.Pilot,
                    LocationId = f.Ship,
                    TypeId = HammerheadTwo,
                    Quantity = 6,
                    FlagId = (int)InventoryFlag.DroneBay,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Drones.Scoop(f.Pilot, ids, InventoryFlag.DroneBay, f.Now));
        Assert.Equal(2, f.Drones.List(SystemId, f.Now).Count);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            Assert.All(ids, id => Assert.Equal(SystemId, world.Set<OwnedItem>().Find(id)!.LocationId));
        }
        Approach(range + 1);
        var lost = f.Drones.List(SystemId, f.Now);
        Assert.Throws<InvalidDataException>(() => f.Drones.Scoop(wing.PilotId, [ids[0]], InventoryFlag.Cargo, f.Now));
        Assert.Equal(lost, f.Drones.List(SystemId, f.Now));
        Approach(range);
        // The second pilot can scoop without drone skills or the ability to launch this drone.
        var change = Assert.Single(f.Drones.Scoop(wing.PilotId, [ids[0]], InventoryFlag.Cargo, f.Now));
        Assert.Equal(wing.PilotId, change.Item.OwnerId);
        Assert.Equal(f.Pilot, change.Previous!.OwnerId);
        Assert.Equal(ids[0], change.Item.Id);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            var saved = world.Set<WorldDrone>().Find(ids[0])!;
            Assert.Equal(wing.PilotId, saved.PilotId);
            Assert.Equal(12, saved.ArmorDamage);
            Assert.Equal(3, saved.StructureDamage);
            Assert.Null(saved.SystemId);
        }
        Approach(0);
        var winner = await Task.WhenAll(
            new[] { f.Pilot, wing.PilotId }.Select(pilot =>
                Task.Run(() =>
                {
                    try
                    {
                        f.Drones.Scoop(pilot, [ids[1]], InventoryFlag.Cargo, f.Now);
                        return pilot;
                    }
                    catch (InvalidDataException)
                    {
                        return 0L;
                    }
                })
            )
        );
        long pilotId = Assert.Single(winner, p => p != 0);
        using WorldDbContext reopened = new(f.Base.Db.Path);
        var item = reopened.Set<OwnedItem>().Find(ids[1])!;
        Assert.Equal(pilotId, item.OwnerId);
        Assert.Equal(pilotId == wing.PilotId ? wing.ShipId : f.Ship, item.LocationId);
        Assert.Equal(1, item.Quantity);
        Assert.Null(reopened.Set<WorldDrone>().Find(ids[1])!.SystemId);
    }
}
