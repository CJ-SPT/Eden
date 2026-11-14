using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Fleets;

public sealed partial class FleetRulesTests
{
    [Fact]
    public void FleetTagsReassignClearAndPreserveImmutableBoundedSnapshots()
    {
        var (world, fleet) = Create();
        FleetTagTarget first = new(501, Pilot(1).SystemId, SpacePosition.Zero);
        FleetTagTarget second = first with { ItemId = 502 };
        world.TagTarget(1, fleet.Id, first, "A");
        var before = world.TargetTags(2);
        Assert.Equal(new(501, first.SystemId, "A"), Assert.Single(before));
        world.TagTarget(1, fleet.Id, first, "A");
        Assert.Same(before, world.TargetTags(1));
        world.TagTarget(1, fleet.Id, second, "A");
        Assert.Equal(502, Assert.Single(world.TargetTags(1)).ItemId);
        Assert.Equal(501, Assert.Single(before).ItemId);
        world.TagTarget(1, fleet.Id, second, null);
        Assert.Empty(world.TargetTags(1));
        var empty = world.TargetTags(1);
        world.TagTarget(1, fleet.Id, second, "");
        Assert.Same(empty, world.TargetTags(1));
        foreach (char tag in FleetRules.TargetTagAlphabet)
        {
            world.TagTarget(1, fleet.Id, first with { ItemId = tag }, tag.ToString());
        }
        Assert.Equal(FleetRules.TargetTagAlphabet.Length, world.TargetTags(2).Count);
        Assert.Empty(world.TargetTags(999));
        world.Leave(2, fleet.Id);
        Assert.Empty(world.TargetTags(2));
        world.Disband(1, fleet.Id);
        Assert.Empty(world.TargetTags(1));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("K")]
    [InlineData("AA")]
    [InlineData(" ")]
    [InlineData("é")]
    public void InvalidFleetTagsPreserveExistingAssignments(string tag)
    {
        var (world, fleet) = Create();
        FleetTagTarget target = new(501, Pilot(1).SystemId, SpacePosition.Zero);
        world.TagTarget(1, fleet.Id, target, "1");
        var before = world.TargetTags(1);
        Assert.Throws<InvalidDataException>(() => world.TagTarget(1, fleet.Id, target, tag));
        Assert.Same(before, world.TargetTags(1));
    }

    [Fact]
    public void FleetTagAuthorityRequiresCurrentCommandRoleAndLocalSpaceTarget()
    {
        var (world, fleet) = Create();
        FleetTagTarget target = new(501, Pilot(1).SystemId, new(FleetRules.CommandRange, 0, 0));
        Assert.Throws<InvalidDataException>(() => world.TagTarget(2, fleet.Id, target, "A"));
        world.MakeBoss(1, fleet.Id, 2);
        Assert.Throws<InvalidDataException>(() => world.TagTarget(2, fleet.Id, target, "A"));
        var wing = fleet.Wings[0];
        world.Move(2, fleet.Id, 3, new(wing.Id, wing.Squads[0].Id, FleetRole.SquadCommander));
        world.TagTarget(3, fleet.Id, target, "A");
        world.Move(2, fleet.Id, 3, new(wing.Id, FleetRules.NoGroup, FleetRole.WingCommander));
        world.TagTarget(3, fleet.Id, target, "B");
        world.Move(2, fleet.Id, 3, Member);
        Assert.Throws<InvalidDataException>(() => world.TagTarget(3, fleet.Id, target, null));
        var before = world.TargetTags(1);
        foreach (
            var invalid in new[]
            {
                target with
                {
                    ItemId = 0,
                },
                target with
                {
                    ItemId = Pilot(1).ShipId,
                },
                target with
                {
                    SystemId = target.SystemId + 1,
                },
                target with
                {
                    Position = new(FleetRules.CommandRange + 1, 0, 0),
                },
                target with
                {
                    Position = new(double.NaN, 0, 0),
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => world.TagTarget(1, fleet.Id, invalid, "1"));
            Assert.Same(before, world.TargetTags(1));
        }
        Guid connection = Guid.NewGuid();
        world.Update(connection, Pilot(1) with { InTransit = true });
        Assert.Throws<InvalidDataException>(() => world.TagTarget(1, fleet.Id, target, "1"));
        world.Update(connection, Pilot(1) with { Position = null });
        Assert.Throws<InvalidDataException>(() => world.TagTarget(1, fleet.Id, target, "1"));
        Assert.Same(before, world.TargetTags(1));
    }
}
