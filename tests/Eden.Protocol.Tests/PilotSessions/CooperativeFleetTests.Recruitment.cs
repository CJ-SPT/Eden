using Eden.Gameplay.Fleets;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CooperativeFleetTests
{
    [Fact]
    public void NativeFleetSizeLimitsNewApplicationsAndRechecksPendingInvitations()
    {
        using TestWorld world = new(liveWorld: true);
        using FleetClient boss = new(world, 1000001);
        using FleetClient applicant = new(world, 1000002);
        long fleet = boss.Create();
        Assert.Equal((long)FleetRules.MaximumMembers, boss.Reply("GetFleetMaxSize", []));
        Assert.IsType<PilotCallResult.Failure>(boss.CallBound(fleet, "SetFleetMaxSize", [0L]));
        Assert.IsType<PilotCallResult.Failure>(boss.CallBound(fleet, "SetFleetMaxSize", [257L]));
        Assert.Null(boss.Reply("Invite", [applicant.PilotId, null, null, null]));
        applicant.Bind(fleet);

        Assert.Equal(1L, boss.Reply("SetFleetMaxSize", [1L]));
        Assert.IsType<PilotCallResult.Failure>(boss.CallBound(fleet, "Invite", [applicant.PilotId, null, null, null]));
        Assert.IsType<PilotCallResult.Failure>(applicant.CallBound(fleet, "AcceptInvite", [(long)applicant.Context.Pilot.ShipTypeId]));
        boss.ServiceReply("fleetProxy", "AddFleetFinderAdvert", [AdvertData()]);
        Assert.IsType<PilotCallResult.Failure>(applicant.CallService("fleetProxy", "ApplyToJoinFleet", [fleet, true]));

        Assert.Equal(2L, boss.Reply("SetFleetMaxSize", [2L]));
        Assert.Null(applicant.Reply("AcceptInvite", [(long)applicant.Context.Pilot.ShipTypeId]));
        Assert.IsType<PilotCallResult.Failure>(applicant.CallBound(fleet, "SetFleetMaxSize", [3L]));
        Assert.Equal(2L, applicant.Reply("GetFleetMaxSize", []));
        Assert.Equal(1L, boss.Reply("SetFleetMaxSize", [1L]));
        Assert.Equal(fleet, applicant.Context.Pilot.Identity.Fleet!.FleetId);
        Assert.Equal(2, Assert.IsType<Dictionary<long, object?>>(boss.Reply("GetMembers", [])).Count);
    }

    [Fact]
    public void NativeDefaultSquadFollowsOpenFinderApplicationsAndClearsWhenDeleted()
    {
        using TestWorld world = new(liveWorld: true);
        using FleetClient boss = new(world, 1000001);
        using FleetClient applicant = new(world, 1000002);
        long fleet = boss.Create();
        long wing = Assert.IsType<long>(boss.Reply("CreateWing", []));
        long squad = Assert.IsType<long>(boss.Reply("CreateSquad", [wing]));

        boss.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(boss.CallBound(fleet, "SetAutoJoinSquadID", [wing]));
        Assert.Equal(true, boss.Reply("SetAutoJoinSquadID", [squad]));
        var optionsUpdate = Assert.Single(boss.Context.Notifications.OfType<FleetOptionsNotification>());
        var update = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(optionsUpdate.Arguments)), 2);
        Assert.Null(Options(update[0])["autoJoinSquadID"]);
        Assert.Equal(squad, Options(update[1])["autoJoinSquadID"]);
        Assert.Equal(squad, Options(boss.Reply("GetOptions", []))["autoJoinSquadID"]);

        boss.ServiceReply("fleetProxy", "AddFleetFinderAdvert", [AdvertData()]);
        var registration = Assert.Single(boss.Context.Notifications.OfType<FleetRegistrationNotification>());
        var change = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(registration.Arguments)), 2);
        Assert.Equal(squad, Options(change[1])["autoJoinSquadID"]);
        var options = Options(boss.Reply("GetOptions", []));
        options["isFreeMove"] = true;
        Assert.Null(boss.Reply("SetOptions", [options]));

        Assert.Equal(false, applicant.ServiceReply("fleetProxy", "ApplyToJoinFleet", [fleet, true]));
        applicant.Bind(fleet, ["AcceptInvite", new object?[] { (long)applicant.Context.Pilot.ShipTypeId }, null]);
        Assert.Equal(new FleetIdentity(fleet, wing, squad, FleetRole.Member, 0), applicant.Context.Pilot.Identity.Fleet);
        Assert.IsType<PilotCallResult.Failure>(applicant.CallBound(fleet, "SetAutoJoinSquadID", [null]));
        Assert.IsType<PilotCallResult.Failure>(boss.CallBound(fleet, "DeleteSquad", [squad]));

        applicant.Reply("LeaveFleet", []);
        Assert.Null(boss.Reply("DeleteSquad", [squad]));
        Assert.Null(Options(boss.Reply("GetOptions", []))["autoJoinSquadID"]);
        Assert.Equal(false, applicant.ServiceReply("fleetProxy", "ApplyToJoinFleet", [fleet, true]));
        applicant.Bind(fleet, ["AcceptInvite", new object?[] { (long)applicant.Context.Pilot.ShipTypeId }, null]);
        Assert.NotEqual(wing, applicant.Context.Pilot.Identity.Fleet!.WingId);

        static Dictionary<string, object?> Options(object? wire) => BlueRead.Fields(Assert.IsType<BlueInstance>(wire).State);
    }
}
