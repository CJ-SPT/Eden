using Eden.Data.World.Mining;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class MiningTests
{
    [Fact]
    public void AutoMiningRouteSettingsValidateAtomicallyAndSurviveReopen()
    {
        using Fixture f = new(useTestClock: true);
        var (context, targeting, mining, _) = f.Client(locked: false);
        var controller = CreateController(f, targeting, mining);
        var store = f.Db.Services.GetRequiredService<IPilotAutoMiningRoutes>();
        var saved = RouteStatus(controller.Call(context, AutoMiningHandler.Service, "SaveRoute", ["Jita, Perimeter, Jita", 1L]));
        Assert.Equal("Jita, Perimeter, Jita", saved.RouteText);
        Assert.Equal(AutoMiningFleetMode.Lead, saved.FleetMode);
        foreach (object?[] args in new object?[][] { ["missing", 0L], ["Jita", 3L], [42L, 0L], [new string('x', 2049), 0L] })
        {
            Assert.IsType<PilotCallResult.Failure>(controller.Call(context, AutoMiningHandler.Service, "SaveRoute", args));
            Assert.Equal([30000142, 30000144, 30000142], store.ReadRoute(context.Pilot.PilotId).Systems.ToArray());
        }
        var wire = BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(saved)));
        Assert.Equal("Jita, Perimeter, Jita", BlueMarshal.Text(wire["routeSystems"]));
        Assert.Equal(1L, wire["fleetMode"]);
        f.Disconnect(context);
        f.Reopen();
        using var live = new LiveWorld(new(f.Db.Path), f.Clock);
        Assert.Equal([30000142, 30000144, 30000142], store.ReadRoute(90000001).Systems.ToArray());
        Assert.Equal(AutoMiningFleetMode.Lead, store.ReadRoute(90000001).FleetMode);
    }

    [Fact]
    public void AutoMiningFleetRequiresOptInAndStopsWhenTheBossStopsOrMembershipChanges()
    {
        using Fixture f = new(useTestClock: true);
        var (leader, leadTargeting, leadMining, _) = f.Client(locked: false);
        var (member, memberTargeting, memberMining, _) = f.Client(1000002, locked: false);
        var lead = CreateController(f, leadTargeting, leadMining);
        var follow = CreateController(f, memberTargeting, memberMining);
        var fleets = f.Db.Services.GetRequiredService<ICooperativeFleets>();
        var coordination = f.Db.Services.GetRequiredService<IAutoMiningFleets>();
        foreach (var context in new[] { leader, member })
        {
            var pilot = context.Pilot;
            fleets.Update(Guid.NewGuid(), new(pilot.PilotId, pilot.CorpId, pilot.ShipId, pilot.ShipTypeId, pilot.SystemId, pilot.Position));
        }
        var fleet = fleets.Create(leader.Pilot.PilotId);
        fleets.Invite(leader.Pilot.PilotId, fleet.Id, member.Pilot.PilotId, fleet.Members[0].Placement with { Role = FleetRole.Member });
        fleets.Accept(member.Pilot.PilotId, fleet.Id);
        Command(follow, member, "SaveRoute", "", 2L);
        Assert.IsType<PilotCallResult.Failure>(follow.Call(member, AutoMiningHandler.Service, "Start", []));
        Command(lead, leader, "SaveRoute", "", 1L);
        Assert.True(Command(lead, leader, "Start").Running);
        Assert.False(Command(follow, member, "GetStatus").Running);
        Assert.True(Command(follow, member, "Start").Running);
        Assert.Equal(leader.Pilot.SystemId, coordination.Follow(member.Pilot.PilotId, leader.Pilot.PilotId).SystemId);
        Command(lead, leader, "Stop");
        follow.Advance(member, sessionTick: true);
        Assert.False(Command(follow, member, "GetStatus").Running);
        Assert.Contains("not leading", Command(follow, member, "GetStatus").Status);
        Command(lead, leader, "Start");
        Command(follow, member, "Start");
        fleets.Leave(member.Pilot.PilotId, fleet.Id);
        follow.Advance(member, sessionTick: true);
        Assert.False(Command(follow, member, "GetStatus").Running);
        Assert.Throws<InvalidDataException>(() => coordination.Follow(member.Pilot.PilotId, leader.Pilot.PilotId));
        f.Clock.Seconds += 31;
        fleets.Invite(leader.Pilot.PilotId, fleet.Id, member.Pilot.PilotId, fleet.Members[0].Placement with { Role = FleetRole.Member });
        fleets.Accept(member.Pilot.PilotId, fleet.Id);
        Assert.Throws<InvalidDataException>(() => coordination.Follow(member.Pilot.PilotId, leader.Pilot.PilotId));
        lead.Cancel(leader);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutoMiningRouteCrossesPairedGatesMinesAndCompletesWithoutRepeating(bool fleetRun)
    {
        using Fixture f = new(useTestClock: true);
        var gate = f.Db.Objects.Single(body => body.DestinationGateId.HasValue);
        var systems = f.Db.Services.GetRequiredService<ISystemObjects>();
        var exit = systems.Load(gate.DestinationSystemId!.Value).Single(body => body.Id == gate.DestinationGateId);
        var destination = exit.Position + new SpacePosition(1_000_000, 0, 0);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlInterpolated(
                $"""
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                VALUES (40009233,30000144,15,9,{destination.X},{destination.Y},{destination.Z})
                """
            );
            var homeBelt = gate.Position + new SpacePosition(1_000_000, 0, 0);
            catalog.Database.ExecuteSqlInterpolated(
                $"""
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                VALUES (40009232,30000142,15,9,{homeBelt.X},{homeBelt.Y},{homeBelt.Z})
                """
            );
            catalog.Database.ExecuteSqlRaw("INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid','40009232','{{}}')");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            var rock = world.Set<WorldAsteroid>().Find(Fixture.Asteroid)!;
            rock.SystemId = gate.DestinationSystemId.Value;
            rock.BeltId = 40009233;
            rock.X = destination.X + 1000;
            rock.Y = destination.Y;
            rock.Z = destination.Z;
            rock.Quantity = 63;
            world.SaveChanges();
        }
        var (context, targeting, mining, module) = f.Client(locked: false, initialPosition: gate.Position + new SpacePosition(5000, 0, 0));
        var controller = CreateController(f, targeting, mining);
        var (wing, wingTargeting, wingMining, wingModule) = f.Client(
            1000002,
            locked: false,
            initialPosition: gate.Position + new SpacePosition(5000, 100, 0)
        );
        var follower = CreateController(f, wingTargeting, wingMining);
        List<RouteParticipant> participants = [new(context, targeting, mining, module, controller)];
        if (fleetRun)
        {
            var fleets = f.Db.Services.GetRequiredService<ICooperativeFleets>();
            foreach (var pilot in new[] { context.Pilot, wing.Pilot })
            {
                fleets.Update(
                    Guid.NewGuid(),
                    new(pilot.PilotId, pilot.CorpId, pilot.ShipId, pilot.ShipTypeId, pilot.SystemId, pilot.Position)
                );
            }
            var fleet = fleets.Create(context.Pilot.PilotId);
            fleets.Invite(context.Pilot.PilotId, fleet.Id, wing.Pilot.PilotId, fleet.Members[0].Placement with { Role = FleetRole.Member });
            fleets.Accept(wing.Pilot.PilotId, fleet.Id);
            participants.Add(new(wing, wingTargeting, wingMining, wingModule, follower));
        }
        Command(controller, context, "SaveRoute", "Perimeter, Jita", fleetRun ? 1L : 0L);
        Assert.True(Command(controller, context, "Start").Running);
        if (fleetRun)
        {
            Command(follower, wing, "SaveRoute", "", 2L);
            Assert.True(Command(follower, wing, "Start").Running);
        }
        long navigation = context.Flight.PilotNavigationRevision;
        Assert.IsType<PilotCallResult.Failure>(controller.Call(context, AutoMiningHandler.Service, "SaveRoute", ["Jita", 0L]));
        int jumps = 0;
        int wingJumps = 0;
        bool mined = false;
        for (int second = 0; second < 480 && Command(controller, context, "GetStatus").Running; second++)
        {
            f.Clock.Seconds++;
            foreach (var participant in participants)
            {
                var current = participant.Context;
                current.Flight.Checkpoint();
                current.Flight.GateDeparturePublished();
                if (current.Flight.CompleteJump())
                {
                    if (current == context)
                    {
                        jumps++;
                    }
                    else
                    {
                        wingJumps++;
                    }
                    participant.Controller.CheckFlight(current);
                    Assert.True(Command(participant.Controller, current, "GetStatus").Running);
                    current.Flight.Snapshot();
                }
                current.Flight.RefreshAsteroids(f.Db.Services.GetRequiredService<IAsteroidField>().List(current.Pilot.SystemId));
                participant.Targeting.Advance(current);
                participant.Mining.Advance(current);
                participant.Controller.Advance(current, sessionTick: true);
                mined |= participant.Mining.IsActive(participant.Module);
            }
        }
        Assert.Equal(2, jumps);
        Assert.True(mined);
        Assert.Equal(navigation, context.Flight.PilotNavigationRevision);
        Assert.Equal(30000142, context.Pilot.SystemId);
        Assert.False(Command(controller, context, "GetStatus").Running);
        Assert.Contains("Route complete", Command(controller, context, "GetStatus").Status);
        Assert.Equal(
            63,
            participants.Sum(participant =>
                f.Inventory.List(participant.Context.Pilot.PilotId, participant.Context.Pilot.ShipId)
                    .Where(item => item.TypeId == 1230)
                    .Sum(item => item.Quantity)
            )
        );
        Assert.False(Command(follower, wing, "GetStatus").Running);
        Assert.Equal(fleetRun, wingJumps > 0);
        f.Clock.Seconds += 60;
        controller.Advance(context, sessionTick: true);
        Assert.False(mining.IsActive(module));
    }

    private static AutoMiningStatus Command(AutoMiningHandler handler, PilotCallContext context, string method, params object?[] args) =>
        RouteStatus(handler.Call(context, AutoMiningHandler.Service, method, args));

    [Fact]
    public async Task AutoMiningRouteHaulsAcrossSystemsAndReturnsThroughSessionFrames()
    {
        using HaulFixture f = new(6.343, multiSystem: true);
        f.Save(95);
        Assert.IsType<PilotCallResult.Success>(f.Call("SaveRoute", "Perimeter", 0L));
        f.Start();
        bool unloaded = false;
        bool returned = false;
        int transitions = 0;
        int lastSystem = f.Context.Pilot.SystemId;
        for (int second = 0; second < 900 && !returned; second++)
        {
            await f.Step(1);
            Assert.True(f.Status().Running, f.Status().Status);
            if (f.Context.Pilot.SystemId != lastSystem)
            {
                transitions++;
                lastSystem = f.Context.Pilot.SystemId;
            }
            if (f.Context.Pilot.Position is not null && !f.Context.Flight.InBallpark)
            {
                await f.Calls.ProcessAsync(
                    Eden.Protocol.Tests.PilotSessions.PilotServicesTests.Request(
                        "beyonce",
                        "MachoBindObject",
                        [(long)f.Context.Pilot.SystemId, null]
                    )
                );
            }
            if (f.Context.Pilot.Position is null && f.Ore(f.Station).Sum(item => item.Quantity) == 63)
            {
                unloaded = true;
                Assert.Empty(f.Ore(f.Context.Pilot.ShipId));
            }
            returned = unloaded && f.Context.Pilot.SystemId == 30000144 && f.Mining.IsActive(f.Module);
        }
        Assert.True(unloaded, f.Status().Status);
        Assert.True(returned, f.Status().Status);
        Assert.Equal(3, transitions);
        Assert.Equal(63, Assert.Single(f.Ledger.Read(f.Context.Pilot.PilotId)).Quantity.Collected);
        Assert.Equal(63, f.Ore(f.Station).Sum(item => item.Quantity));
        Assert.InRange(f.Context.Pilot.Position!.DistanceTo(f.Origin), 0, 5000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutoMiningFleetTravelYieldsToManualNavigationAndStop(bool manual)
    {
        using Fixture f = new(useTestClock: true);
        var (context, targeting, mining, _) = f.Client(locked: false);
        var controller = CreateController(f, targeting, mining);
        var fleets = f.Db.Services.GetRequiredService<ICooperativeFleets>();
        var pilot = context.Pilot;
        fleets.Update(Guid.NewGuid(), new(pilot.PilotId, pilot.CorpId, pilot.ShipId, pilot.ShipTypeId, pilot.SystemId, pilot.Position));
        fleets.Create(pilot.PilotId);
        Command(controller, context, "SaveRoute", "", 1L);
        Command(controller, context, "Start");
        if (manual)
        {
            Assert.IsType<PilotCallResult.Success>(context.Flight.Execute(new FlightCommand.Heading(0, 1, 0)));
            controller.Advance(context, sessionTick: true);
            Assert.Equal(new SpacePosition(0, 1, 0), context.Flight.Heading);
        }
        else
        {
            Command(controller, context, "Stop");
        }
        Assert.False(Command(controller, context, "GetStatus").Running);
        Assert.False(mining.HasCycle);
        f.Clock.Seconds += 60;
        controller.Advance(context, sessionTick: true);
        Assert.False(Command(controller, context, "GetStatus").Running);
    }

    private static AutoMiningStatus RouteStatus(PilotCallResult result) =>
        Assert.IsType<AutoMiningStatus>(Assert.IsType<PilotCallResult.Success>(result).Reply);

    private sealed record RouteParticipant(
        PilotCallContext Context,
        TargetingHandler Targeting,
        MiningHandler Mining,
        long Module,
        AutoMiningHandler Controller
    );
}
