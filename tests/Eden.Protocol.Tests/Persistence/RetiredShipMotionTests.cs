using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class RetiredShipMotionTests
{
    [Fact]
    public void LateMovementForDeletedHullCannotRecreateAnOrphanCheckpointPosition()
    {
        const long ship = 2000000099999;
        const int system = 30000142;
        using TestWorld fixture = new();
        var pilot = fixture.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Add(
                new Ship
                {
                    Id = ship,
                    OwnerId = pilot.PilotId,
                    TypeId = pilot.Ship.TypeId,
                    StationId = checked((int)pilot.StationId),
                    Name = "Retired motion fixture",
                }
            );
            world.Add(
                new FlightPosition
                {
                    ShipId = ship,
                    SystemId = system,
                    X = 1000,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld live = new(new(fixture.Path), new TestClock(), _ => { });
        live.Economy.Warm();
        using var context = live.UseSystem(system);
        var runtime = live.Combat!;
        runtime.PublishPilotPosition(ship, system, new(2000, 0, 0));
        using (var action = live.Economy.Begin())
        {
            action.Set<FlightPosition>().Remove(action.Set<FlightPosition>().Find(ship)!);
            action.Set<Ship>().Remove(action.Set<Ship>().Find(ship)!);
            action.Commit();
        }
        using (var counters = SessionPerformance.Begin("late motion", _ => { }, force: true)!)
        {
            runtime.PublishPilotPosition(ship, system, new(3000, 0, 0));
            Assert.Equal(0, counters.Commands);
            Assert.Equal(0, counters.Connections);
            Assert.Equal(0, counters.FittingCalculations);
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        snapshot.Project();
        using WorldDbContext saved = new(snapshot.Database);
        Assert.Null(saved.Ships.Find(ship));
        Assert.Null(saved.Positions.Find(ship));
    }
}
