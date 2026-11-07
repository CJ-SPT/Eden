using Eden.Gameplay.Drones;
using Eden.Gameplay.Fleets;

namespace Eden.Protocol.Tests.Combat;

public sealed class DroneDelegationRulesTests
{
    private static DroneDelegateMember Owner => new(1, 11, 1, new(0, 0, 0), 10, true, false, new(100, 10));
    private static DroneDelegateMember Member => new(2, 22, 1, new(1000, 0, 0), 10, true, false, new(100, 11));

    [Fact]
    public void AssignmentRequiresCurrentPeerAndCombinedTwentyDroneCapacity()
    {
        foreach (var mode in new[] { DroneDelegationMode.Assist, DroneDelegationMode.Guard })
        {
            DroneDelegationRules.CheckAssignment(mode, Owner, Member, 1000, 15, 5);
            Assert.Throws<InvalidDataException>(() => DroneDelegationRules.CheckAssignment(mode, Owner, Member, 1000, 16, 5));
            Assert.Throws<InvalidDataException>(() => DroneDelegationRules.CheckAssignment(mode, Owner, Member, 999, 0, 5));
        }
        Assert.Throws<InvalidDataException>(() =>
            DroneDelegationRules.CheckAssignment(DroneDelegationMode.None, Owner, Member, 1000, 0, 5)
        );
        Assert.Throws<InvalidDataException>(() =>
            DroneDelegationRules.CheckAssignment(DroneDelegationMode.Assist, Owner, Member, double.NaN, 0, 5)
        );
    }

    [Theory]
    [InlineData("self")]
    [InlineData("capsule")]
    [InlineData("offline")]
    [InlineData("docked")]
    [InlineData("system")]
    [InlineData("fleet")]
    [InlineData("nonmember")]
    [InlineData("nan")]
    [InlineData("infinite")]
    public void InvalidPeersCannotReceiveAnAssignment(string reason)
    {
        var member = reason switch
        {
            "self" => Owner,
            "capsule" => Member with { Capsule = true },
            "offline" => Member with { Available = false },
            "docked" => Member with { Position = null },
            "system" => Member with { SystemId = 2 },
            "fleet" => Member with { Membership = new(101, 11) },
            "nan" => Member with { Position = new(double.NaN, 0, 0) },
            "infinite" => Member with { Position = new(double.PositiveInfinity, 0, 0) },
            _ => Member with { Membership = null },
        };
        Assert.Throws<InvalidDataException>(() =>
            DroneDelegationRules.CheckAssignment(DroneDelegationMode.Assist, Owner, member, 1000, 0, 5)
        );
    }

    [Fact]
    public void LeavingAndRejoiningTheSameFleetCannotReviveAFormerAssignment()
    {
        DroneDelegation delegation = new(DroneDelegationMode.Assist, Member.PilotId, Member.ShipId, 100, 10, 11, 0);
        Assert.True(DroneDelegationRules.Maintained(delegation, Owner, Member));
        Assert.False(DroneDelegationRules.Maintained(delegation, Owner, Member with { Membership = new(100, 12) }));
        Assert.False(DroneDelegationRules.Maintained(delegation, Owner with { Membership = new(100, 12) }, Member));
        Assert.False(DroneDelegationRules.Maintained(delegation, Owner, Member with { ShipId = 23 }));
        Assert.False(DroneDelegationRules.Maintained(delegation, Owner, Member with { Position = new(FleetRules.CommandRange + 1, 0, 0) }));
    }
}
