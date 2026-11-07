using Eden.Gameplay.Drones;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Xunit;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentDroneTests
{
    [Fact]
    public void WarmObserversAndControllerChecksShareContactsAndSeeCommittedOrders()
    {
        using Fixture f = new();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var ids = f.Launch();
        long ship = f.Ship;
        var contacts = f.Drones.List(30000144, f.Now);
        Assert.True(f.Drones.HasControlled(f.Pilot, ship));
        using (var metrics = SessionPerformance.Begin("warm drones", _ => { }, force: true))
        {
            for (int observer = 0; observer < 10; observer++)
            {
                Assert.Same(contacts, f.Drones.List(30000144, f.Now));
                Assert.True(f.Drones.HasControlled(f.Pilot, ship));
                Assert.False(f.Drones.HasControlled(f.Pilot + 1, ship));
            }
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        f.Drones.Order(f.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
        Assert.All(f.Drones.List(30000144, f.Now), d => Assert.Equal(DroneActivity.ReturningToBay, d.Activity));
        f.Tick();
        Assert.Empty(f.Drones.List(30000144, f.Now));
        Assert.False(f.Drones.HasControlled(f.Pilot, ship));
    }
}
