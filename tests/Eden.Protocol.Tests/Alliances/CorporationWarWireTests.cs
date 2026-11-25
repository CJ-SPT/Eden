using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationWarWireTests
{
    [Fact]
    public void NativeDeclarationUsesEligibleOwnerAndAdvertisesWarHeadquarters()
    {
        using TestWorld world = new(clock: new TestClock(), liveWorld: true);
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        CorporationWarTests.SeedHeadquarters(world, attacker, defender);
        AllianceBillingTests.Fund(world, attacker, CorporationWarRules.WeeklyFeeCents * 2);
        using CorporationTestClient first = new(world, 1000001);
        var owners = Assert.IsType<List<object?>>(Reply(first, "lookupSvc", "LookupWarableCorporationsOrAlliances", ["Defenders", true]));
        Assert.Equal(true, Fields(Assert.Single(owners))["warPermit"]);
        Assert.Equal(true, Fields(Reply(first, "corpmgr", "GetPublicInfo", [(long)defender]))["allowWar"]);
        var headquarters = Assert.IsType<List<object?>>(Reply(first, "structureDirectory", "GetValidWarHQs", [(long)attacker]));
        long hq = Assert.IsType<long>(Fields(Assert.Single(headquarters))["structureID"]);
        Assert.Equal(CorporationWarTests.Headquarters, hq);
        var declared = Fields(Reply(first, "corpRegistry", "DeclareWarAgainst", [(long)defender, hq]));
        Assert.Equal(hq, declared["warHQ"]);
        long warId = Assert.IsType<long>(declared["warID"]);
        var structure = Fields(Reply(first, "structureDirectory", "GetStructureInfo", [hq]));
        Assert.Equal(warId, Assert.Single(Assert.IsType<List<object?>>(structure["wars"])));
        var publicInfo = Assert.IsType<List<object?>>(Reply(first, "warsInfoMgr", "GetPublicWarInfo", [new List<object?> { warId }]));
        Assert.Equal((long)defender, Fields(Assert.Single(publicInfo))["againstID"]);
    }

    [Fact]
    public void NativeMutualWarUsesBoundRegistryDictionaryAndRefreshesBothParties()
    {
        using TestWorld world = new(clock: new TestClock(), liveWorld: true);
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        using CorporationTestClient first = new(world, 1000001);
        using CorporationTestClient second = new(world, 1000002);
        var bindResult = first.CallService(
            "warRegistry",
            "MachoBindObject",
            [new object?[] { (long)attacker, 1L }, new object?[] { "GetWars", new object?[] { (long)attacker } }]
        );
        var binding = Assert.IsType<BoundPilotReply>(Assert.IsType<PilotCallResult.Success>(bindResult).Reply);
        Assert.NotEmpty(BlueMarshal.Encode(binding));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(binding.Reply))));
        Assert.IsType<PilotCallResult.Failure>(
            first.CallService("warRegistry", "MachoBindObject", [new object?[] { (long)defender, 1L }, null])
        );
        Assert.Empty(Assert.IsType<List<object?>>(Reply(second, "mutualWarInviteMgr", "GetPendingInvitesForSession", [])));
        var search = Assert.IsType<List<object?>>(Reply(first, "lookupSvc", "LookupWarableCorporationsOrAlliances", ["Defenders", true]));
        var owner = Fields(Assert.Single(search));
        Assert.Equal((long)defender, owner["ownerID"]);
        Assert.Equal(false, owner["warPermit"]);

        Assert.Null(Reply(first, "mutualWarInviteMgr", "SendInviteByPlayer", [(long)defender]));
        second.Services.GetRequiredService<CorporationWarMonitor>().Poll(second.Context);
        Assert.Single(second.Context.Notifications, notification => notification.Name == "OnMutualWarsUpdated_Remote");
        var invites = Assert.IsType<List<object?>>(Reply(second, "mutualWarInviteMgr", "GetPendingInvitesForSession", []));
        var invite = Fields(Assert.Single(invites));
        Assert.Equal((long)attacker, invite["fromOwnerID"]);
        Assert.Equal((long)defender, invite["toOwnerID"]);
        Assert.Null(Reply(second, "mutualWarInviteMgr", "RespondToInviteByPlayer", [(long)attacker, true]));
        first.Services.GetRequiredService<CorporationWarMonitor>().Poll(first.Context);
        var notification = Assert.Single(first.Context.Notifications.OfType<CorporationWarChanged>());
        var arguments = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)), 3);
        Assert.True(Assert.IsType<HashSet<long>>(arguments[1]).SetEquals([attacker, defender]));
        var changes = BlueRead.Fields(arguments[2]);
        long warId = Assert.IsType<long>(BlueRead.Tuple(changes["warID"], 2)[1]);

        var registry = Assert.IsType<Dictionary<long, object?>>(Reply(first, "warRegistry", "GetWars", [(long)attacker]));
        Assert.Equal(true, Fields(registry[warId])["mutual"]);
        var publicInfo = Fields(Reply(first, "warsInfoMgr", "GetPublicWarInfo", [warId]));
        Assert.Equal(warId, publicInfo["warID"]);
        Assert.Null(publicInfo["warHQ"]);
        var cached = first.CallService("warsInfoMgr", "GetWarsByOwnerID", [(long)attacker]);
        var cacheReply = Assert.IsType<CorporationWarHistoryReply>(Assert.IsType<PilotCallResult.Success>(cached).Reply);
        var cachePacket = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(cacheReply)));
        var history = cacheReply.Rows();
        Assert.Equal("carbon.common.script.sys.crowset.CRowset", history.Name);
        var row = Assert.IsType<BlueConstructor>(Assert.Single(Assert.IsType<List<object?>>(history.Arguments[1])));
        Assert.Equal(warId, BlueRead.Tuple(row.Arguments[1], 17)[0]);
        Assert.Equal(BlueMarshal.Encode(history), BlueRead.Bytes(BlueRead.Tuple(cachePacket.State, 3)[1]));

        Assert.Null(Reply(second, "warRegistry", "RetractMutualWar", [warId]));
        var ending = Fields(Reply(first, "warsInfoMgr", "GetPublicWarInfo", [warId]));
        Assert.Equal((long)CorporationWarEndReason.MutualEnded, ending["reasonEnded"]);
        Assert.IsType<long>(ending["timeFinished"]);
    }

    private static object? Reply(CorporationTestClient client, string service, string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService(service, method, args)).Reply));

    private static Dictionary<string, object?> Fields(object? model) => BlueRead.Fields(Assert.IsType<BlueInstance>(model).State);
}
