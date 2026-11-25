using Eden.Gameplay.Fleets;

namespace Eden.Protocol.Tests.Fleets;

public sealed partial class FleetRulesTests
{
    [Fact]
    public void MassMoveUsesOriginalCommandAuthorityAndPublishesOneBatch()
    {
        var (world, fleet) = Create(4);
        long wing = fleet.Wings[0].Id;
        long destination = world.CreateSquad(1, fleet.Id, wing);
        long otherWing = world.CreateWing(1, fleet.Id);
        long otherSquad = world.CreateSquad(1, fleet.Id, otherWing);
        world.Move(1, fleet.Id, 2, new(wing, FleetRules.NoGroup, FleetRole.WingCommander));
        world.Move(1, fleet.Id, 4, new(otherWing, otherSquad, FleetRole.Member));
        for (long id = 1; id <= 4; id++)
        {
            world.Drain(id);
        }

        FleetPlacement placement = new(wing, destination, FleetRole.Member);
        Assert.Equal(new[] { 2L, 3L }, world.MoveMany(2, fleet.Id, [2, 3, 4, 9000], placement));
        Assert.Equal(FleetRole.Member, world.Identity(2)!.Role);
        Assert.Equal(destination, world.Identity(3)!.SquadId);
        Assert.Equal(otherSquad, world.Identity(4)!.SquadId);
        Assert.IsType<FleetEvent.MembersMoved>(Assert.Single(world.Drain(4)).Event);
        var actorEvents = world.Drain(2);
        Assert.Collection(
            actorEvents,
            delivery => Assert.Equal(4L, Assert.IsType<FleetEvent.MoveFailed>(delivery.Event).PilotId),
            delivery => Assert.Equal(2, Assert.IsType<FleetEvent.MembersMoved>(delivery.Event).Changes.Count)
        );
        Assert.Throws<InvalidDataException>(() => world.MoveMany(2, fleet.Id, [3], placement));
    }
}
