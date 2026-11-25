using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationWarReportWireTests
{
    [Fact]
    public void BoundReportsKeepSeparateWarIdsAndReturnNativeGroupsFiltersAndKillmail()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        const long currentWar = 401;
        const long historicalWar = 402;
        const int frigateGroup = 25;
        var report = new ShipCombatReport(
            1234,
            Other,
            clock.Now,
            30000142,
            new(11, Other, defender, null, 603, null, null, 250),
            new(12, Ceo, attacker, null, 587, 3634, 0, 250),
            [],
            [new(3634, 27, true, 0, 1, [])]
        );
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new SavedCorporationWar
                {
                    Id = currentWar,
                    AttackerId = attacker,
                    DefenderId = defender,
                    DeclaredAt = clock.Now - TimeSpan.TicksPerDay * 2,
                    StartsAt = clock.Now - TimeSpan.TicksPerDay,
                    Mutual = true,
                }
            );
            action.Add(
                new SavedCorporationWar
                {
                    Id = historicalWar,
                    AttackerId = attacker,
                    DefenderId = defender,
                    DeclaredAt = clock.Now - TimeSpan.TicksPerDay * 20,
                    StartsAt = clock.Now - TimeSpan.TicksPerDay * 19,
                    EndsAt = clock.Now - TimeSpan.TicksPerDay * 12,
                    Mutual = true,
                }
            );
            action.Add(
                new SavedCorporationWarKill
                {
                    Id = report.Id,
                    WarId = currentWar,
                    GroupId = frigateGroup,
                    Group = WarReportGroup.Frigates,
                    VictimOrganizationId = defender,
                    Time = report.Time,
                    IskLost = 12_345_678.5m,
                    IskDestroyed = 11_000_000m,
                    Report = JsonSerializer.Serialize(report),
                }
            );
            action.Commit();
        }

        using CorporationTestClient client = new(world, 1000001);
        Assert.IsType<PilotCallResult.Failure>(client.CallService("warStatisticMgr", "MachoResolveObject", [0L]));
        var bound = Bind(client, currentWar, ["GetBaseInfo", Array.Empty<object?>()]);
        var history = Bind(client, historicalWar, null);
        Assert.NotEqual(bound.Reference.Id, history.Reference.Id);
        string reportScope = client.Context.ResolveReference(bound.Reference.Id)!;
        string historyScope = client.Context.ResolveReference(history.Reference.Id)!;
        var summary = BlueRead.Tuple(RoundTrip(bound.Reply), 4);
        Assert.Equal(currentWar, Fields(summary[0])["warID"]);
        Assert.Equal(1L, NumericMap(summary[1])[attacker]);
        Assert.Equal(12_345_678.5, NumericMap(summary[2])[attacker]);
        var historicalSummary = BlueRead.Tuple(Reply(client, historyScope, "GetBaseInfo", []), 4);
        Assert.Equal(historicalWar, Fields(historicalSummary[0])["warID"]);

        object? groups = Reply(client, reportScope, "GetKillsByGroup", []);
        var group = BlueRead.Fields(NumericMap(groups)[frigateGroup]);
        Assert.Equal(1L, group["defenderShipLoss"]);
        Assert.Equal(12_345_678.5, group["defenderIskLoss"]);
        var kills = Assert.IsType<List<object?>>(Reply(client, reportScope, "GetKills", [(long)attacker, (long)WarReportGroup.Frigates]));
        Assert.Equal(report.Id, Fields(Assert.Single(kills))["killID"]);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(client, reportScope, "GetKills", [(long)defender, null])));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(client, reportScope, "GetKills", [null, (long)WarReportGroup.Cruisers])));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(client, historyScope, "GetKills", [null, null])));

        string hash = WarReportRules.Hash(report);
        Assert.Null(Reply(client, reportScope, "GetKillMail", [report.Id, new string('0', 40)]));
        Assert.Null(Reply(client, historyScope, "GetKillMail", [report.Id, hash]));
        var details = Fields(Reply(client, "warStatisticMgr", "GetKillMail", [report.Id, hash]));
        Assert.Equal(currentWar, details["warID"]);
        Assert.Equal(11_000_000d, details["iskDestroyed"]);
        Assert.Contains("<items>", BlueRead.Text(details["killBlob"]));
    }

    private static BoundPilotReply Bind(CorporationTestClient client, long id, object?[]? embedded) =>
        Assert.IsType<BoundPilotReply>(
            Assert.IsType<PilotCallResult.Success>(client.CallService("warStatisticMgr", "MachoBindObject", [id, embedded])).Reply
        );

    private static object? Reply(CorporationTestClient client, string service, string method, object?[] args) =>
        RoundTrip(Assert.IsType<PilotCallResult.Success>(client.CallService(service, method, args)).Reply);

    private static object? RoundTrip(object? value) => BlueMarshal.Decode(BlueMarshal.Encode(value));

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);

    private static Dictionary<long, object?> NumericMap(object? value) => Assert.IsType<Dictionary<long, object?>>(value);
}
