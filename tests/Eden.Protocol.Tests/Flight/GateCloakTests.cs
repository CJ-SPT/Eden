using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Thermal;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class GateCloakTests
{
    [Fact]
    public void HealthReadsFollowGateTransfersAndUseCurrentDamageOnReturn()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var pilot = db.Store.FindPilot(1000001)!;
        var gate = db.Objects.Single(body => body.DestinationGateId.HasValue);
        var systems = db.Services.GetRequiredService<ISystemObjects>();
        var travel = db.Services.GetRequiredService<IPilotGateTravel>();
        var health = db.Services.GetRequiredService<IPilotHealth>();
        var live = db.Services.GetRequiredService<LiveWorld>();
        db.Store.SetPosition(pilot.ShipId, gate.Position, pilot.SystemId);

        using var source = live.UseSystem(pilot.SystemId);
        var initial = health.Read(pilot.PilotId, clock.Now);
        var destination = travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId!.Value);
        using (var damage = live.Economy.Begin(new(pilots: [pilot.PilotId])))
        {
            damage
                .Set<ShipHealthState>()
                .Add(
                    new()
                    {
                        ShipId = pilot.ShipId,
                        ShieldCharge = initial.Defense.Shield,
                        ArmorDamage = 1,
                        UpdatedAt = clock.Now,
                    }
                );
            damage.Commit();
        }
        var damaged = health.Read(pilot.PilotId, clock.Now);
        Assert.Equal(initial.Remaining.Armor - 1, damaged.Remaining.Armor);

        var returnGate = systems.Load(destination.SystemId).Single(body => body.Id == gate.DestinationGateId);
        using var away = live.UseSystem(destination.SystemId);
        Assert.Equal(damaged, health.Read(pilot.PilotId, clock.Now));
        db.Store.SetPosition(pilot.ShipId, returnGate.Position, destination.SystemId);
        var returned = travel.Transfer(pilot.PilotId, pilot.ShipId, returnGate.Id, gate.Id);
        Assert.Equal(pilot.SystemId, returned.SystemId);
        Assert.Equal(damaged, health.Read(pilot.PilotId, clock.Now));
        using var arrived = live.UseSystem(returned.SystemId);
        Assert.Equal(damaged, health.Read(pilot.PilotId, clock.Now));
    }

    [Fact]
    public void HeatReadsFollowACommittedGateTransfer()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var pilot = db.Store.FindPilot(1000001)!;
        var gate = db.Objects.Single(body => body.DestinationGateId.HasValue);
        var travel = db.Services.GetRequiredService<IPilotGateTravel>();
        var heat = db.Services.GetRequiredService<IPilotShipHeat>();
        var live = db.Services.GetRequiredService<LiveWorld>();
        db.Store.SetPosition(pilot.ShipId, gate.Position, pilot.SystemId);
        using var source = live.UseSystem(pilot.SystemId);
        var before = heat.Read(pilot.PilotId, pilot.ShipId, clock.Now);
        travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId!.Value);

        Assert.True(HeatCycleRules.SameState(before, heat.Read(pilot.PilotId, pilot.ShipId, clock.Now)));
        Assert.Equal(pilot.SystemId, live.CurrentSystem);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("heading")]
    [InlineData("speed")]
    [InlineData("stop")]
    [InlineData("warp")]
    [InlineData("late")]
    public async Task GateArrivalCloaksTheShipUntilMovementOrTheExactDeadline(string action)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var systems = db.Services.GetRequiredService<ISystemObjects>();
        var space = db.Services.GetRequiredService<SharedSpace>();
        var pilot = db.Store.FindPilot(1000001)!;
        var gate = db.Objects.Single(body => body.DestinationGateId.HasValue);
        db.Store.SetPosition(pilot.ShipId, gate.Position);
        pilot = db.Store.FindPilot(1000001)!;
        using FlightSession flight = new(
            db.Store,
            pilot,
            clock,
            space,
            db.Objects,
            gates: db.Services.GetRequiredService<IPilotGateTravel>()
        );
        flight.Snapshot();
        var other = db.Store.FindPilot(1000002)!;
        var exit = systems.Load(gate.DestinationSystemId!.Value).Single(body => body.Id == gate.DestinationGateId);
        db.Store.SetPosition(other.ShipId, exit.Position + new SpacePosition(10_000, 0, 0), gate.DestinationSystemId);
        other = db.Store.FindPilot(1000002)!;
        using FlightSession observer = new(db.Store, other, clock, space, systems.Load(other.SystemId));
        observer.Snapshot();
        observer.Updates.Clear();
        Assert.Same(PilotCallResult.None, flight.StartJump(new(gate.Id, gate.DestinationGateId!.Value, pilot.ShipId), systems));
        flight.GateDeparturePublished();
        clock.Seconds += FlightSession.JumpDurationTicks;
        Assert.True(flight.CompleteJump());
        if (action == "late")
        {
            clock.AdvanceWallTime(TimeSpan.FromSeconds(61));
            flight.Snapshot();
            Assert.False(flight.Cloaked);
            Assert.True(flight.CanTarget);
            Assert.False(space.IsAreaProtected(flight.Pilot.SystemId, pilot.ShipId));
            Assert.Empty(flight.NotificationsBeforeFlight.OfType<JumpCloakUpdated>());
            Assert.DoesNotContain(flight.Updates, update => update.Command is DestinyCommand.CloakBall);
            return;
        }
        flight.Snapshot();
        Assert.True(flight.Cloaked);
        Assert.False(flight.CanTarget);
        Assert.True(space.IsAreaProtected(flight.Pilot.SystemId, pilot.ShipId));
        Assert.Null(space.Find(flight.Pilot.SystemId, pilot.ShipId));
        var timer = Assert.Single(flight.NotificationsBeforeFlight.OfType<JumpCloakUpdated>());
        long deadline = clock.Now + GateTravelRules.CloakDurationTicks;
        Assert.Equal(new object?[] { pilot.ShipId, deadline, GateTravelRules.CloakDurationTicks }, timer.Arguments);
        Assert.NotEmpty(BlueMarshal.Encode(timer.Arguments));
        Assert.Contains(flight.Updates, update => update.Command is DestinyCommand.CloakBall { Gate: true });
        Assert.Contains(flight.Updates, update => update.Command is DestinyCommand.SpecialEffect { Graphic: "effects.CloakNoAnim" });
        flight.Snapshot();
        Assert.Single(flight.NotificationsBeforeFlight.OfType<JumpCloakUpdated>());

        observer.RefreshPeers();
        Assert.Single(observer.Updates, update => update.Command is DestinyCommand.GateActivity { Arriving: true });
        Assert.DoesNotContain(observer.Updates, update => update.Command is DestinyCommand.AddBalls);
        Assert.DoesNotContain(space.Peers(other), contact => contact.Pilot.ShipId == pilot.ShipId);
        Assert.DoesNotContain(space.NpcTargets(other.SystemId), target => target.ShipId == pilot.ShipId);
        Assert.DoesNotContain(space.SmartbombShips(other), target => target.Id == pilot.ShipId);
        Assert.Null(observer.FindTarget(pilot.ShipId));
        Assert.Same(PilotCallResult.Unsupported, flight.Execute(new FlightCommand.Heading(double.NaN, 0, 0)));
        Assert.True(flight.GateCloaked);
        Assert.DoesNotContain(
            db.Services.GetRequiredService<IPilotInformation>().Directional(other.PilotId, Math.Tau, 1_000_000, new(1, 0, 0), clock.Now),
            contact => contact.Id == pilot.ShipId
        );
        db.Store.SetPosition(other.ShipId, flight.Pilot.Position! + new SpacePosition(1000, 0, 0), flight.Pilot.SystemId);
        observer.Relocate(db.Store.FindPilot(1000002)!, systems.Load(flight.Pilot.SystemId));
        flight.Checkpoint();
        Assert.True(flight.GateCloaked);
        Assert.DoesNotContain(space.Observe(other.SystemId, other.ShipId, flight.Pilot.Position!, 0), c => c.ShipId == pilot.ShipId);

        await LiveWorld.Find(db.Path)!.FlushAsync();
        using (var connection = WorldConnections.FileConnection(db.Path))
        using (WorldDbContext saved = new(connection))
        {
            Assert.Equal(deadline, saved.Set<Eden.Data.World.Propulsion.SpatialNavigationState>().Find(pilot.ShipId)!.GateCloakUntil);
        }

        if (action == "expiry")
        {
            clock.AdvanceWallTime(TimeSpan.FromTicks(GateTravelRules.CloakDurationTicks - 1));
            flight.Checkpoint();
            Assert.True(flight.Cloaked);
            Assert.True(space.IsAreaProtected(other.SystemId, pilot.ShipId));
            clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            // An observer sees the exact deadline without waiting for the owner's next frame.
            Assert.False(space.IsAreaProtected(other.SystemId, pilot.ShipId));
            Assert.NotNull(space.Find(other.SystemId, pilot.ShipId));
            Assert.Contains(space.Observe(other.SystemId, other.ShipId, flight.Pilot.Position!, 0), c => c.ShipId == pilot.ShipId);
            Assert.False(Assert.Single(space.Peers(other), contact => contact.Pilot.ShipId == pilot.ShipId).Cloaked);
            flight.Checkpoint();
        }
        else
        {
            FlightCommand command = action switch
            {
                "heading" => new FlightCommand.Heading(1, 0, 0),
                "speed" => new FlightCommand.Speed(1),
                "stop" => new FlightCommand.Stop(),
                _ => new FlightCommand.WarpToPoint(123, flight.Pilot.Position! + new SpacePosition(1_000_000_000, 0, 0), 0),
            };
            Assert.Same(PilotCallResult.None, flight.Execute(command));
        }
        Assert.False(flight.Cloaked);
        Assert.Single(flight.NotificationsBeforeFlight.OfType<JumpCloakCancelled>());
        Assert.Contains(flight.Updates, update => update.Command is DestinyCommand.UncloakBall);
        flight.Checkpoint();
        Assert.Single(flight.NotificationsBeforeFlight.OfType<JumpCloakCancelled>());
        Assert.False(db.Services.GetRequiredService<IPilotSpatialModules>().Status(pilot.ShipId).GateCloaked(clock.Now));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(61)]
    public async Task CheckpointReopenKeepsTheOriginalDeadlineAndRejectedTransfersLeaveItUnchanged(int seconds)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var pilot = db.Store.FindPilot(1000001)!;
        var gate = db.Objects.Single(body => body.DestinationGateId.HasValue);
        db.Store.SetPosition(pilot.ShipId, gate.Position);
        var travel = db.Services.GetRequiredService<IPilotGateTravel>();
        var spatial = db.Services.GetRequiredService<IPilotSpatialModules>();
        Assert.Throws<InvalidDataException>(() => travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.Id));
        Assert.Equal(0, spatial.Status(pilot.ShipId).GateCloakUntil);
        var destination = travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId!.Value);
        long deadline = clock.Now + GateTravelRules.CloakDurationTicks;
        Assert.Equal(deadline, spatial.Status(pilot.ShipId).GateCloakUntil);
        Assert.Throws<InvalidDataException>(() => travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId.Value));
        Assert.Equal(deadline, spatial.Status(pilot.ShipId).GateCloakUntil);
        await LiveWorld.Find(db.Path)!.FlushAsync();
        db.Services.Dispose();
        clock.Seconds += seconds;
        WorldInitialization.Initialize(new(db.Path), clock);
        using var services = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        var status = services.GetRequiredService<IPilotSpatialModules>().Status(pilot.ShipId);
        Assert.Equal(deadline, status.GateCloakUntil);
        Assert.Equal(seconds < 60, status.GateCloaked(clock.Now));
        Assert.Equal(destination.SystemId, services.GetRequiredService<Eden.World.Docking.SqliteWorld>().FindPilot(1000001)!.SystemId);
        var cloak = services.GetRequiredService<IPilotGateCloak>();
        Assert.Throws<InvalidDataException>(() => cloak.Cancel(90000002, pilot.ShipId));
        Assert.Equal(deadline, services.GetRequiredService<IPilotSpatialModules>().Status(pilot.ShipId).GateCloakUntil);
        cloak.Cancel(pilot.PilotId, pilot.ShipId);
        Assert.Equal(0, services.GetRequiredService<IPilotSpatialModules>().Status(pilot.ShipId).GateCloakUntil);
        await LiveWorld.Find(db.Path)!.FlushAsync();
        using var connection = WorldConnections.FileConnection(db.Path);
        using WorldDbContext saved = new(connection);
        Assert.Equal(0, saved.Set<Eden.Data.World.Propulsion.SpatialNavigationState>().Find(pilot.ShipId)!.GateCloakUntil);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModulePermissionUsesTheFittedModulesCatalogAttribute(bool preserves)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var pilot = db.Store.FindPilot(1000001)!;
        const int moduleType = InventoryDefinitions.CivilianMinerType;
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (3123,'canActivateInGateCloak')");
            catalog.Database.ExecuteSql($"INSERT INTO TypeAttribute VALUES ({moduleType},3123,{(preserves ? 1 : 0)})");
        }
        long module;
        using (WorldDbContext world = new(db.Path))
        {
            var item = world
                .Add(
                    new OwnedItem
                    {
                        OwnerId = pilot.PilotId,
                        TypeId = moduleType,
                        LocationId = pilot.ShipId,
                        FlagId = (int)InventoryFlag.HighSlot0,
                        Quantity = 1,
                        Singleton = true,
                        Online = true,
                    }
                )
                .Entity;
            world.SaveChanges();
            module = item.Id;
        }
        var gate = db.Objects.Single(body => body.DestinationGateId.HasValue);
        db.Store.SetPosition(pilot.ShipId, gate.Position);
        var destination = db
            .Services.GetRequiredService<IPilotGateTravel>()
            .Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId!.Value);
        using FlightSession flight = new(
            db.Store,
            destination,
            clock,
            db.Services.GetRequiredService<SharedSpace>(),
            db.Services.GetRequiredService<ISystemObjects>().Load(destination.SystemId)
        );
        flight.Snapshot();
        flight.PrepareGateCloakModule(module);
        Assert.Equal(preserves, flight.GateCloaked);
        Assert.Equal(preserves, db.Services.GetRequiredService<SharedSpace>().IsAreaProtected(destination.SystemId, pilot.ShipId));
    }
}
