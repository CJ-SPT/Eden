using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Docking;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerGateWatchTests(ITestOutputHelper output)
{
    [Fact]
    public void AWatchFollowsSelectedShipMovementDockingAndGridRangeWithoutWarmDatabaseWork()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        var world = host.GetRequiredService<SqliteWorld>();
        var pilot = world.FindPilot(1000001)!;
        int system = pilot.SystemId;
        var gate = host.GetRequiredService<ISystemObjects>().Load(system).First(g => g.DestinationSystemId is not null);
        var watch = host.GetRequiredService<ICapsuleerGateWatch>();
        var activity = new HumanActivitySnapshot(0, 1, [system]) { Pilots = [new(pilot.PilotId, system)] };
        live.Economy.Warm();
        using (var cold = SessionPerformance.Begin("cold resident gate monitor", output.WriteLine, force: true)!)
        {
            Assert.Empty(watch.Read(activity));
            Assert.Equal(0, cold.Commands);
            Assert.Equal(0, cold.Connections);
            Assert.Equal(0, cold.FittingCalculations);
        }
        var near = gate.Position + new SpacePosition(gate.Radius + 10000, 0, 0);
        world.SetPosition(pilot.ShipId, near, system);
        var expected = new WatchedTrafficGate(system, gate.DestinationSystemId!.Value, gate.Id, gate.DestinationGateId!.Value);
        Assert.Equal(expected, Assert.Single(watch.Read(activity)));
        using (var owner = live.Enter())
        {
            using var cost = SessionPerformance.Begin("occupied gate monitor", output.WriteLine, force: true)!;
            live.Combat!.PublishPilotPosition(
                pilot.ShipId,
                system,
                near + new SpacePosition(CapsuleerEncounterRules.GateWatchDistanceMeters, 0, 0)
            );
            Assert.Empty(watch.Read(activity));
            live.Combat.PublishPilotPosition(pilot.ShipId, system, near);
            Assert.Equal(expected, Assert.Single(watch.Read(activity)));
            Assert.Empty(watch.Read(activity with { Pilots = [] }));
            Assert.Equal(0, cost.Commands);
            Assert.Equal(0, cost.Connections);
            Assert.Equal(0, cost.FittingCalculations);
        }
        world.SetPosition(pilot.ShipId, null, system);
        Assert.Empty(watch.Read(activity));
        using var docked = SessionPerformance.Begin("docked gate monitor", output.WriteLine, force: true)!;
        Assert.Empty(watch.Read(activity));
        Assert.Equal(0, docked.Commands);
        Assert.Equal(0, docked.Connections);
    }
}
