using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Characters;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationCombatReportTests
{
    [Fact]
    public async Task NativeCorporationPagesUseCanonicalKillIdsAndSurviveCheckpoint()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Corporation(world);
        int opponent = Corporation(world, Other, "Combat Opponents", "COP");
        long time = DateTime.UnixEpoch.ToFileTimeUtc();
        CombatParty first = new(901, Ceo, corporation, null, 603, 3634, 0, 250);
        CombatParty second = new(902, Other, opponent, null, 587, 3634, 0, 250);
        CombatParty npc = new(903, null, 1000125, null, 16617, null, null, 250);
        long oldest;
        long newest;
        long npcLoss;
        long opposingKill;
        var live = world.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            oldest = PilotInteractionHistoryStore.Save(action, "ship:9010", Loss(second, first, Other)).Id;
            newest = PilotInteractionHistoryStore.Save(action, "ship:9011", Loss(second, first, Other)).Id;
            PilotInteractionHistoryStore.Save(action, $"ship:9011:pilot:{Ceo}", Loss(second, first, Ceo));
            opposingKill = PilotInteractionHistoryStore.Save(action, "ship:9012", Loss(first, second, Ceo)).Id;
            npcLoss = PilotInteractionHistoryStore.Save(action, "ship:9013", Loss(first, npc, Ceo)).Id;
            PilotInteractionHistoryStore.Save(action, "npc:9014:1", Loss(npc, first, Ceo));
            action.Commit();
        }

        using (CorporationTestClient client = new(world, 1000001))
        using (CorporationTestClient enemy = new(world, 1000002))
        {
            Assert.IsType<PilotCallResult.Failure>(
                client.Call("MachoBindObject", [(long)opponent, new object?[] { "GetRecentKills", new object?[] { 25L, null } }])
            );
            var success = Assert.IsType<PilotCallResult.Success>(
                client.Call("MachoBindObject", [(long)corporation, new object?[] { "GetRecentKills", new object?[] { 1L, null } }])
            );
            var bound = Assert.IsType<BoundPilotReply>(success.Reply);
            var firstPage = Assert.IsType<CorporationCombatReportReply>(bound.Reply);
            Assert.Equal(newest, Assert.Single(firstPage.Reports).Id);
            Assert.Equal(oldest, Assert.Single(Page(client, "GetRecentKills", [1L, newest]).Reports).Id);
            Assert.Empty(Page(client, "GetRecentKills", [1L, oldest]).Reports);
            Assert.Equal(new[] { newest, oldest }, Page(client, "GetRecentKills", [25L, 0L]).Reports.Select(row => row.Id));
            Assert.Equal(new[] { npcLoss, opposingKill }, Page(client, "GetRecentLosses", [25L, null]).Reports.Select(row => row.Id));
            Assert.Equal(opposingKill, Assert.Single(Page(enemy, "GetRecentKills", [25L, null]).Reports).Id);
            Assert.IsType<PilotCallResult.Failure>(client.Call("GetRecentKills", [0L, null]));
            Assert.IsType<PilotCallResult.Failure>(client.Call("GetRecentKills", [1L, -1L]));
            Assert.IsType<PilotCallResult.Failure>(client.Call("GetRecentKills", [1L, null, (long)opponent]));

            var packet = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(firstPage)));
            Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", packet.Name);
            var envelope = BlueRead.Tuple(packet.State, 3);
            Assert.Equal("corpid", BlueRead.Text(BlueRead.Fields(envelope[0])["sessionInfo"]));
            var rowset = firstPage.Rows();
            Assert.Equal("carbon.common.script.sys.crowset.CRowset", rowset.Name);
            byte[] payload = BlueRead.Bytes(envelope[1]);
            Assert.Equal(BlueMarshal.Encode(rowset), payload);
            Assert.Equal((long)CachedMethodPayload.Checksum(payload), BlueRead.Tuple(envelope[2], 2)[1]);
            var row = Assert.IsType<BlueConstructor>(Assert.Single(Assert.IsType<List<object?>>(rowset.Arguments[1])));
            Assert.Equal("blue.DBRow", row.Name);
            var fields = BlueRead.Tuple(row.Arguments[1], 25);
            Assert.Equal(newest, fields[0]);
            Assert.Equal(opponent, fields[3]);
            Assert.Equal(corporation, fields[8]);
            Assert.Contains("<items>", Assert.IsType<BlueUnicode>(fields[13]).Value);
        }

        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var reports = reopened.GetRequiredService<ICorporationCombatReports>();
        Assert.Equal(new[] { newest, oldest }, reports.Recent(Ceo, false, 25, null).Select(row => row.Id));
        Assert.Equal(new[] { npcLoss, opposingKill }, reports.Recent(Ceo, true, 25, null).Select(row => row.Id));

        ShipCombatReport Loss(CombatParty victim, CombatParty final, long recipient) =>
            new(0, recipient, time, 30000142, victim, final, [], [new(3634, 27, true, 0, 1, [])]);
    }

    private static CorporationCombatReportReply Page(CorporationTestClient client, string method, object?[] args) =>
        Assert.IsType<CorporationCombatReportReply>(Assert.IsType<PilotCallResult.Success>(client.Call(method, args)).Reply);
}
