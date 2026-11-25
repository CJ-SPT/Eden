using Eden.Gameplay.Fleets;

namespace Eden.Protocol.Tests.Fleets;

public sealed partial class FleetRulesTests
{
    [Fact]
    public void RegroupOrdersRespectSquadRangeAndCurrentMovementAuthority()
    {
        var (world, fleet) = Create(5);
        long wing = fleet.Wings[0].Id;
        long squad = fleet.Wings[0].Squads[0].Id;
        world.Move(1, fleet.Id, 2, new(wing, squad, FleetRole.SquadCommander));
        world.Move(1, fleet.Id, 3, new(wing, squad, FleetRole.Member));
        world.Move(1, fleet.Id, 4, new(wing, squad, FleetRole.Member));
        long otherSquad = world.CreateSquad(1, fleet.Id, wing);
        world.Move(1, fleet.Id, 5, new(wing, otherSquad, FleetRole.Member));
        world.Update(Guid.NewGuid(), Pilot(4, FleetRules.CommandRange + 1));
        for (long id = 1; id <= 5; id++)
        {
            world.Drain(id);
        }

        world.Regroup(2, fleet.Id, 0);
        var order = Assert.IsType<FleetEvent.Regroup>(Assert.Single(world.Drain(3)).Event).Order;
        Assert.Equal(1002, order.CommanderShipId);
        foreach (long id in new long[] { 1, 2, 4, 5 })
        {
            Assert.Empty(world.Drain(id));
        }
        Assert.True(world.CanExecuteRegroup(3, order, 0));
        Assert.False(world.CanExecuteRegroup(3, order, order.ExpiresAt));
        Assert.Throws<InvalidDataException>(() => world.Regroup(3, fleet.Id, 0));
        world.SetAcceptsRegroup(3, fleet.Id, false);
        Assert.False(world.CanExecuteRegroup(3, order, 0));
        world.SetAcceptsRegroup(3, fleet.Id, true);
        world.CancelMovementOrders(3);
        Assert.False(world.CanExecuteRegroup(3, order, 0));
        world.Regroup(2, fleet.Id, 0);
        order = Assert
            .IsType<FleetEvent.Regroup>(Assert.Single(world.Drain(3), delivery => delivery.Event is FleetEvent.Regroup).Event)
            .Order;
        Assert.True(world.CanExecuteRegroup(3, order, 0));
        world.Update(Guid.NewGuid(), Pilot(2) with { ShipId = 9999 });
        Assert.False(world.CanExecuteRegroup(3, order, 0));
    }
}
