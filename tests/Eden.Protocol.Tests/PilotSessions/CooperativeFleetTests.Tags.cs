using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Fleets;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CooperativeFleetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFleetTagsReachLateJoinersAndClearWhenTargetsDisappearOrMembershipEnds(bool resident)
    {
        using TestWorld world = new(liveWorld: resident);
        var source = world.Store.FindPilot(1000001)!;
        var target = world.Store.FindPilot(1000002)!;
        world.Store.SetPosition(source.ShipId, SpacePosition.Zero, source.SystemId);
        world.Store.SetPosition(target.ShipId, new(500, 0, 0), source.SystemId);
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        member.Context.Flight.Snapshot();
        leader.Context.Flight.Snapshot();
        long fleet = leader.Create();
        leader.Context.Notifications.Clear();
        member.Context.Notifications.Clear();
        Assert.Null(leader.ServiceReply("beyonce", "CmdFleetTagTarget", [target.ShipId, "A"]));
        Assert.Equal("A", Tags(leader)[target.ShipId]);
        member.Advance();
        Assert.Empty(member.Context.Notifications.OfType<FleetStateNotification>());
        leader.Reply("Invite", [member.PilotId, null, null, (long)FleetRole.Member]);
        member.Advance();
        member.Bind(fleet);
        member.Reply("AcceptInvite", [(long)member.Context.Pilot.ShipTypeId]);
        Assert.Equal("A", Tags(member)[target.ShipId]);
        Assert.IsType<PilotCallResult.Failure>(member.CallService("beyonce", "CmdFleetTagTarget", [source.ShipId, "B"]));
        foreach (
            var args in new object?[][]
            {
                [target.ShipId, "AA"],
                [long.MaxValue, "1"],
                [source.ShipId, "1"],
                [target.ShipId, 1L],
                [target.ShipId],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(leader.CallService("beyonce", "CmdFleetTagTarget", args));
        }
        Assert.Equal("A", Assert.Single(world.Services.GetRequiredService<ICooperativeFleets>().TargetTags(leader.PilotId)).Tag);
        leader.Context.Notifications.Clear();
        member.Context.Notifications.Clear();
        Assert.Null(leader.ServiceReply("beyonce", "CmdFleetTagTarget", [target.ShipId, "A"]));
        using (var metrics = SessionPerformance.Begin("unchanged fleet tags", _ => { }, force: true)!)
        {
            for (int i = 0; i < 10; i++)
            {
                leader.Advance();
                member.Advance();
            }
            if (resident)
            {
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
        }
        Assert.Empty(leader.Context.Notifications.OfType<FleetStateNotification>());
        Assert.Empty(member.Context.Notifications.OfType<FleetStateNotification>());
        Assert.Null(leader.ServiceReply("beyonce", "CmdFleetTagTarget", [target.ShipId, null]));
        member.Advance();
        Assert.Empty(Tags(member));
        Assert.Null(leader.ServiceReply("beyonce", "CmdFleetTagTarget", [target.ShipId, "2"]));
        member.Advance();
        Assert.Equal("2", Tags(member)[target.ShipId]);
        member.Reply("LeaveFleet", []);
        Assert.Empty(Tags(member));
        leader.Reply("Invite", [member.PilotId, null, null, (long)FleetRole.Member]);
        member.Advance();
        member.Bind(fleet);
        member.Reply("AcceptInvite", [(long)member.Context.Pilot.ShipTypeId]);
        Assert.Equal("2", Tags(member)[target.ShipId]);
        member.Context.Flight.Relocate(member.Context.Pilot with { Position = null }, []);
        member.Advance();
        Assert.Empty(Tags(member));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("beyonce", "CmdFleetTagTarget", [source.ShipId, "B"]));
        world.Services.GetRequiredService<SharedSpace>().Leave(target.ShipId);
        leader.Context.Flight.RefreshPeers();
        leader.Advance();
        Assert.Empty(Tags(leader));
    }

    private static Dictionary<long, string> Tags(FleetClient client)
    {
        var notification = client.Context.Notifications.OfType<FleetStateNotification>().Last();
        var args = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)));
        var state = Assert.IsType<BlueInstance>(args[0]);
        var tags = BlueRead.Fields(state.State)["targetTags"];
        if (tags is Dictionary<string, object?> empty)
        {
            Assert.Empty(empty);
            return [];
        }
        return Assert.IsType<Dictionary<long, object?>>(tags).ToDictionary(pair => pair.Key, pair => BlueMarshal.Text(pair.Value));
    }
}
