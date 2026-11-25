using Eden.Gameplay.Flight;
using Eden.Protocol.Fleets;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CooperativeFleetTests
{
    [Fact]
    public void NativeFleetRegroupUsesFlightFollowAndHonorsMemberOptOut()
    {
        using TestWorld world = new(liveWorld: true);
        var commander = world.Store.FindPilot(1000001)!;
        var subordinate = world.Store.FindPilot(1000002)!;
        world.Store.SetPosition(commander.ShipId, new(50_000_000, 0, 0), commander.SystemId);
        world.Store.SetPosition(subordinate.ShipId, new(50_005_000, 0, 0), commander.SystemId);
        using FleetClient leader = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        leader.Context.Flight.Snapshot();
        member.Context.Flight.Snapshot();
        Assert.True(member.Context.Flight.CanTarget);
        Assert.NotEqual(leader.Context.Pilot.Position, member.Context.Pilot.Position);
        Join(leader, member);

        var roster = Assert.IsType<Dictionary<long, object?>>(member.Reply("GetMembers", []));
        var entry = BlueRead.Fields(Assert.IsType<BlueInstance>(roster[member.PilotId]).State);
        var optOuts = BlueRead.Fields(Assert.IsType<BlueInstance>(entry["memberOptOuts"]).State);
        Assert.Equal(true, optOuts["acceptsFleetRegroups"]);
        Assert.IsType<PilotCallResult.Failure>(member.CallService("beyonce", "CmdFleetRegroup", []));
        Assert.IsType<PilotCallResult.Failure>(leader.CallService("beyonce", "CmdFleetRegroup", [subordinate.ShipId]));

        Assert.Null(member.Reply("SetAcceptsRegroupValue", [false]));
        leader.Advance();
        var changed = leader.Context.Notifications.OfType<FleetMemberNotification>().Last();
        var decoded = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(changed.Arguments)));
        Assert.Equal(false, BlueRead.Fields(Assert.IsType<BlueInstance>(decoded[11]).State)["acceptsFleetRegroups"]);

        member.Context.Flight.Updates.Clear();
        Assert.Null(leader.ServiceReply("beyonce", "CmdFleetRegroup", []));
        member.Advance();
        Assert.DoesNotContain(member.Context.Flight.Updates, update => update.Command is DestinyCommand.Follow);

        Assert.Null(member.Reply("SetAcceptsRegroupValue", [true]));
        Assert.Null(leader.ServiceReply("beyonce", "CmdFleetRegroup", []));
        member.Advance();
        var follow = Assert.Single(member.Context.Flight.Updates.Select(update => update.Command).OfType<DestinyCommand.Follow>());
        Assert.Equal(commander.ShipId, follow.TargetId);
        Assert.Contains(member.Context.Flight.Updates, update => update.Command is DestinyCommand.SetSpeedFraction { Fraction: 1d });
    }
}
