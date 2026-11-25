using Eden.Gameplay.Fleets;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CooperativeFleetTests
{
    [Fact]
    public void NativeMassMoveReturnsChangedMembersAndUpdatesTheirRoster()
    {
        using TestWorld world = new(liveWorld: true);
        using FleetClient boss = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long fleet = Join(boss, member);
        long wing = Assert.IsType<long>(boss.Reply("CreateWing", []));
        long squad = Assert.IsType<long>(boss.Reply("CreateSquad", [wing]));
        boss.Context.Notifications.Clear();
        member.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(
            member.CallBound(fleet, "MassMoveMembers", [new List<object?> { member.PilotId }, wing, squad, (long)FleetRole.Member])
        );
        Assert.IsType<PilotCallResult.Failure>(
            boss.CallBound(
                fleet,
                "MassMoveMembers",
                [new List<object?> { boss.PilotId, boss.PilotId }, wing, squad, (long)FleetRole.Member]
            )
        );
        Assert.Empty(boss.Context.Notifications.OfType<FleetMemberNotification>());

        object?[] request = [new List<object?> { boss.PilotId, member.PilotId }, wing, squad, (long)FleetRole.Member];
        var moved = Assert.IsType<List<object?>>(boss.Reply("MassMoveMembers", request));
        Assert.Equal(new object?[] { boss.PilotId, member.PilotId }, moved);
        member.Advance();
        foreach (var observer in new[] { boss, member })
        {
            var notifications = observer.Context.Notifications.OfType<FleetMemberNotification>().ToArray();
            Assert.Equal(new[] { boss.PilotId, member.PilotId }, notifications.Select(change => change.After.Pilot.Id));
            Assert.All(notifications, change => Assert.Equal(new FleetPlacement(wing, squad, FleetRole.Member), change.After.Placement));
            Assert.Equal(squad, observer.Context.Pilot.Identity.Fleet!.SquadId);
        }
        Assert.Empty(Assert.IsType<List<object?>>(boss.Reply("MassMoveMembers", request)));
    }
}
