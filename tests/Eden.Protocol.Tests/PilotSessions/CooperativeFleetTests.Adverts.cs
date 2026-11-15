using Eden.Gameplay.Fleets;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CooperativeFleetTests
{
    [Fact]
    public void NativeFleetProxyCreatesEditsListsAndRemovesAdvertWithoutBoundReference()
    {
        using TestWorld world = new(liveWorld: true);
        using FleetClient boss = new(world, 1000001);
        using FleetClient observer = new(world, 1000002);
        Assert.Null(observer.ServiceReply("fleetProxy", "GetMyFleetFinderAdvert", []));
        Assert.IsType<PilotCallResult.Failure>(observer.CallService("fleetProxy", "AddFleetFinderAdvert", [AdvertData()]));
        long fleet = boss.Create();
        var data = AdvertData();
        data["description"] = new BlueUnicode("Corporation standing fleet \u00e9");
        var reply = BlueRead.Fields(boss.ServiceReply("fleetProxy", "AddFleetFinderAdvert", [data]));
        Assert.Equal(fleet, reply["fleetID"]);
        Assert.Equal("ZTT Standing", BlueMarshal.Text(reply["fleetName"]));
        Assert.Equal("Corporation standing fleet \u00e9", Assert.IsType<string>(reply["description"]));
        Assert.Empty(Assert.IsType<HashSet<long>>(reply["public_allowedEntities"]));
        Assert.True(Assert.IsType<bool>(BlueRead.Fields(Assert.IsType<BlueInstance>(boss.Reply("GetOptions", [])).State)["isRegistered"]));
        var notification = Assert.Single(boss.Context.Notifications.OfType<FleetRegistrationNotification>());
        var change = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)), 2);
        Assert.False(Assert.IsType<bool>(BlueRead.Fields(Assert.IsType<BlueInstance>(change[0]).State)["isRegistered"]));
        Assert.True(Assert.IsType<bool>(BlueRead.Fields(Assert.IsType<BlueInstance>(change[1]).State)["isRegistered"]));
        var listed = Assert.IsType<Dictionary<long, object?>>(observer.ServiceReply("fleetProxy", "GetAvailableFleetAds", []));
        Assert.Null(BlueRead.Fields(listed[fleet])["public_allowedEntities"]);
        Assert.Equal(1L, BlueRead.Fields(listed[fleet])["numMembers"]);
        data["fleetName"] = "Edited standing fleet";
        data["hideInfo"] = true;
        boss.ServiceReply("fleetProxy", "AddFleetFinderAdvert", [data]);
        var edited = BlueRead.Fields(boss.ServiceReply("fleetProxy", "GetMyFleetFinderAdvert", []));
        Assert.Equal("Edited standing fleet", BlueMarshal.Text(edited["fleetName"]));
        Assert.Equal(true, edited["hideInfo"]);
        Assert.Equal(reply["dateCreated"], edited["dateCreated"]);
        var allowed = new BlueConstructor("__builtin__.set", [new List<object?> { observer.PilotId }]);
        var entities = new BlueInstance("util.KeyVal", new Dictionary<string, object?> { ["public_allowedEntities"] = allowed });
        var updated = BlueRead.Fields(boss.ServiceReply("fleetProxy", "UpdateAdvertAllowedEntities", [entities]));
        Assert.Contains(observer.PilotId, Assert.IsType<HashSet<long>>(updated["public_allowedEntities"]));
        var diff = new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?> { ["publicToRemoveFromAllowed"] = allowed, ["publicToAddToDisallowed"] = allowed }
        );
        updated = BlueRead.Fields(boss.ServiceReply("fleetProxy", "UpdateAdvertInfo", [999L, diff]));
        Assert.Empty(Assert.IsType<HashSet<long>>(updated["public_allowedEntities"]));
        Assert.Contains(observer.PilotId, Assert.IsType<HashSet<long>>(updated["public_disallowedEntities"]));
        Assert.Equal(1L, updated["numMembers"]);
        Assert.IsType<PilotCallResult.Failure>(boss.CallService("fleetProxy", "GetMyFleetFinderAdvert", [fleet]));
        Assert.IsType<PilotCallResult.Failure>(
            boss.CallService(
                "fleetProxy",
                "AddFleetFinderAdvert",
                [
                    new Dictionary<string, object?>
                    {
                        ["fleetName"] = "Invalid",
                        ["description"] = "",
                        ["inviteScope"] = 1L,
                        ["joinNeedsApproval"] = 1L,
                    },
                ]
            )
        );
        Assert.Equal(
            "Edited standing fleet",
            BlueMarshal.Text(BlueRead.Fields(boss.ServiceReply("fleetProxy", "GetMyFleetFinderAdvert", []))["fleetName"])
        );
        boss.ServiceReply("fleetProxy", "RemoveFleetFinderAdvert", []);
        Assert.Null(boss.ServiceReply("fleetProxy", "GetMyFleetFinderAdvert", []));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(observer.ServiceReply("fleetProxy", "GetAvailableFleetAds", [])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinderApplicationsUseExistingInvitationsAndRequireBossOrCommanderApproval(bool approval)
    {
        using TestWorld world = new(liveWorld: true);
        using FleetClient boss = new(world, 1000001);
        using FleetClient applicant = new(world, 1000002);
        long id = boss.Create();
        var data = AdvertData();
        data["joinNeedsApproval"] = approval;
        boss.ServiceReply("fleetProxy", "AddFleetFinderAdvert", [data]);
        Assert.Equal(approval, applicant.ServiceReply("fleetProxy", "ApplyToJoinFleet", [id, true]));
        boss.Advance();
        if (approval)
        {
            Assert.Equal(applicant.PilotId, Assert.Single(boss.Context.Notifications.OfType<FleetJoinRequestNotification>()).Pilot.Id);
            var requests = Assert.IsType<Dictionary<long, object?>>(boss.Reply("GetJoinRequests", []));
            Assert.Contains(applicant.PilotId, requests.Keys);
            Assert.Null(boss.Reply("Invite", [applicant.PilotId, null, null, null]));
        }
        applicant.Advance();
        Assert.Equal(id, Assert.Single(applicant.Context.Notifications.OfType<FleetInviteNotification>()).FleetId);
        applicant.Bind(id, ["AcceptInvite", new object?[] { (long)applicant.Context.Pilot.ShipTypeId }, null]);
        Assert.Equal(id, applicant.Context.Pilot.Identity.Fleet!.FleetId);
        Assert.IsType<PilotCallResult.Failure>(applicant.CallService("fleetProxy", "RemoveFleetFinderAdvert", []));
        Assert.IsType<PilotCallResult.Failure>(applicant.CallService("fleetProxy", "AddFleetFinderAdvert", [data]));
        Assert.IsType<PilotCallResult.Failure>(
            applicant.CallService("fleetProxy", "UpdateAdvertAllowedEntities", [new Dictionary<string, object?>()])
        );
        Assert.IsType<PilotCallResult.Failure>(applicant.CallBound(id, "GetJoinRequests", []));
        var ad = BlueRead.Fields(boss.ServiceReply("fleetProxy", "UpdateAdvertInfo", [999L, new Dictionary<string, object?>()]));
        Assert.Equal(2L, ad["numMembers"]);
        Assert.Null(boss.Reply("MakeLeader", [applicant.PilotId]));
        var moved = BlueRead.Fields(applicant.ServiceReply("fleetProxy", "GetMyFleetFinderAdvert", []));
        Assert.Equal(applicant.PilotId, BlueRead.Fields(Assert.IsType<BlueInstance>(moved["leader"]).State)["charID"]);
        Assert.Null(applicant.Reply("DisbandFleet", []));
        Assert.Null(boss.ServiceReply("fleetProxy", "GetMyFleetFinderAdvert", []));
    }

    private static Dictionary<string, object?> AdvertData() =>
        new()
        {
            ["fleetName"] = "ZTT Standing",
            ["description"] = "",
            ["inviteScope"] = (long)FleetInviteScope.Corporation,
            ["joinNeedsApproval"] = false,
            ["hideInfo"] = false,
            ["updateOnBossChange"] = true,
        };
}
