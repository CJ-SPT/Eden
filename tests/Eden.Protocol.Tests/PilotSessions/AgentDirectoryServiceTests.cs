using Eden.Gameplay.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class AgentDirectoryServiceTests
{
    [Theory]
    [InlineData("GetAgentStaticInfo")]
    [InlineData("GetAgentByID")]
    [InlineData("GetSolarSystemOfAgent")]
    public void SupplementaryAgentReadsRequireSelectionAndRejectInvalidTargetsAndKeywords(string method)
    {
        using Fixture fixture = new();
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("agentMgr", method, [AgentTestData.AgentId]));
        fixture.Select();
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [null],
                [true],
                ["3008416"],
                [0L],
                [long.MaxValue],
                [AgentTestData.NonAgent],
                [AgentTestData.DeletedAgent],
                [AgentTestData.AgentId, 90000002L],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call("agentMgr", method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("agentMgr", method, [AgentTestData.AgentId], new() { ["characterID"] = 90000002L })
        );
        Assert.IsType<PilotCallResult.Success>(fixture.Call("agentMgr", method, [AgentTestData.AgentId], new() { ["machoVersion"] = 1L }));
        Assert.Empty(fixture.Context.Notifications);
    }

    [Theory]
    [InlineData("GetMyEpicArcStatus")]
    [InlineData("GetMessagesForEpicArcMissions")]
    public void EpicArcReadsReturnNativeEmptyMapsWithoutAcceptingAnotherPilotsIdentity(string method)
    {
        using Fixture fixture = new();
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("agentMgr", method, []));
        fixture.Select();
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(Reply(fixture.Call("agentMgr", method, []))));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("agentMgr", method, [90000002L]));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("agentMgr", method, [], new() { ["characterID"] = 90000002L }));
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void CareerCompletionPrimesEveryDistinctRequestedAgentWithFalseAndRejectsUnboundedOrForgedInput()
    {
        using Fixture fixture = new();
        fixture.Select();
        var completed = Assert.IsType<Dictionary<long, object?>>(
            Reply(
                fixture.Call(
                    "agentMgr",
                    "GetCompletedCareerAgentIDs",
                    [new List<object?> { AgentTestData.OtherAgent, AgentTestData.OtherAgent }]
                )
            )
        );
        Assert.Single(completed);
        Assert.False(Assert.IsType<bool>(completed[AgentTestData.OtherAgent]));
        Assert.Empty(
            Assert.IsType<Dictionary<string, object?>>(Reply(fixture.Call("agentMgr", "GetCompletedCareerAgentIDs", [new List<object?>()])))
        );
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [null],
                [new object?[] { AgentTestData.OtherAgent }],
                [new List<object?> { 90000002L }],
                [new List<object?> { AgentTestData.DeletedAgent }],
                [new List<object?> { "3019370" }],
                [Enumerable.Repeat<object?>(AgentTestData.OtherAgent, 1001).ToList()],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call("agentMgr", "GetCompletedCareerAgentIDs", args));
        }
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void DirectoryUsesNativeRowsetFieldsAndRequiresSelectedPilotWithoutAcceptingForgedFilters()
    {
        using Fixture fixture = new();
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("agentMgr", "GetAgents", []));
        fixture.Select();
        var rows = Fields(Reply(fixture.Call("agentMgr", "GetAgents", [])));
        Assert.Equal(
            new object?[]
            {
                "agentID",
                "stationID",
                "corporationID",
                "solarsystemID",
                "agentTypeID",
                "factionID",
                "divisionID",
                "level",
                "quality",
                "isLocator",
            },
            Assert.IsType<List<object?>>(rows["header"]).Select(BlueMarshal.Text)
        );
        var lines = Assert.IsType<List<object?>>(rows["lines"]);
        Assert.Equal(2, lines.Count);
        Assert.Equal(
            new object?[] { AgentTestData.AgentId, 60003760L, 1000044L, 30000142L, 2L, 500001L, 22L, 3L, 20L, true },
            Assert.IsType<object?[]>(lines[0])
        );
        Assert.Equal(AgentTestData.OtherAgent, Assert.IsType<object?[]>(lines[1])[0]);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("agentMgr", "GetAgents", [60003760L]));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("agentMgr", "GetAgents", [], new() { ["stationID"] = 60003760L }));
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("agentMgr", "UnknownAgentMethod", []));
        var directory = fixture.Db.Services.GetRequiredService<INpcDirectory>();
        Assert.Same(directory, fixture.Db.Services.GetRequiredService<INpcDirectory>());
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void NativeAgentAddReadDeleteAndNpcOwnersAndPublicInformationUseCatalogIdentity()
    {
        using Fixture fixture = new();
        // Owner metadata is public and can be primed before selecting a pilot.
        var owners = Assert.IsType<object?[]>(
            Reply(
                fixture.Call(
                    "config",
                    "GetMultiOwnersEx",
                    [new List<object?> { AgentTestData.AgentId, AgentTestData.NonAgent, AgentTestData.DeletedAgent, 90000001L }]
                )
            )
        );
        var names = Assert.IsType<List<object?>>(owners[1]).Cast<object?[]>().ToArray();
        Assert.Equal(3, names.Length);
        var agent = names.Single(row => Equals(row[0], AgentTestData.AgentId));
        Assert.Equal("Agent Étoile", BlueMarshal.Text(agent[1]));
        Assert.Equal(AgentTestData.AgentId + 1_000_000, agent[4]);
        fixture.Select();
        Assert.Null(Reply(fixture.Call("charMgr", "AddContact", [AgentTestData.AgentId])));
        Assert.Null(Reply(fixture.Call("charMgr", "AddContact", [AgentTestData.AgentId])));
        var state = Fields(Reply(fixture.Call("charMgr", "GetContactList", [])));
        var saved = Fields(Assert.Single(Assert.IsType<List<object?>>(state["addresses"])));
        Assert.Equal(AgentTestData.AgentId, saved["contactID"]);
        Assert.Equal(false, saved["inWatchlist"]);
        Assert.Equal(0d, saved["relationshipID"]);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("charMgr", "AddContact", [AgentTestData.NonAgent]));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("charMgr", "AddContact", [AgentTestData.DeletedAgent]));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("charMgr", "AddContact", [AgentTestData.AgentId, 10d, true, false, ""]));
        var profile = Fields(Reply(fixture.Call("charMgr", "GetPublicInfo", [AgentTestData.AgentId])));
        Assert.Equal(AgentTestData.AgentId, profile["characterID"]);
        Assert.Equal("Agent Étoile", BlueMarshal.Text(profile["characterName"]));
        Assert.Equal(1000044L, profile["corporationID"]);
        Assert.Equal(19L, profile["schoolID"]);
        var publicRows = Fields(Reply(fixture.Call("charMgr", "GetPublicInfo3", [AgentTestData.AgentId])));
        var publicRow = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(publicRows["lines"])));
        Assert.Equal(AgentTestData.AgentId, publicRow[0]);
        Assert.Equal("Agent Étoile", publicRow[1]);
        Assert.Equal("Pinned NPC biography", publicRow[^1]);
        var columns = Assert.IsType<List<object?>>(publicRows["header"]).Select(BlueMarshal.Text).ToList();
        Assert.Null(publicRow[columns.IndexOf("militiaFactionID")]);
        var corporation = Fields(Reply(fixture.Call("corpRegistry", "GetInfoWindowDataForChar", [AgentTestData.AgentId])));
        Assert.Equal(1000044L, corporation["corpID"]);
        Assert.Equal(new DateTime(2003, 5, 4, 0, 32, 0, DateTimeKind.Utc).ToFileTimeUtc(), profile["createDateTime"]);
        Assert.Equal(
            "Pinned NPC biography",
            BlueMarshal.Text(Reply(fixture.Call("charMgr", "GetCharacterDescription", [AgentTestData.AgentId])))
        );
        Assert.Equal(false, Reply(fixture.Call("onlineStatus", "GetOnlineStatus", [AgentTestData.AgentId])));
        Assert.Null(Reply(fixture.Call("charMgr", "DeleteContacts", [new List<object?> { AgentTestData.AgentId }])));
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(fixture.Call("charMgr", "GetContactList", [])))["addresses"]));
        Assert.Empty(fixture.Context.Notifications);
    }

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        public PilotCallContext Context { get; }
        private readonly IServiceScope _scope;

        public Fixture()
        {
            Db = new(clock: Clock);
            AgentTestData.Prepare(Db);
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public void Select() => Context.Select(Db.Store.FindPilot(1000001)!);

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
