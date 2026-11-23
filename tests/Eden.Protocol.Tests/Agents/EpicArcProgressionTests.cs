using System.Text.Json;
using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class EpicArcProgressionTests
{
    private const int Arc = 53;
    private const long StartingAgent = 3019411;
    private const long ReferralAgent = 3019442;
    private const long FinaleAgent = 3019443;
    private const long Pilot = MissionFixture.PilotId;
    private const int Root = 14851;
    private const int Referral = 14960;
    private const int Choice = 14966;
    private const int FirstBranch = 14967;
    private const int SecondBranch = 14968;
    private const int ExitFaction = 500002;

    [Fact]
    public async Task BranchSettlementSurvivesCheckpointAndReplaysOnlyAfterNinetyDays()
    {
        using var fixture = Create();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        Dock(live, MissionFixture.DestinationId);
        Seed(live, Root, StartingAgent, MissionState.Completed, fixture.Clock.Now);
        long branch = Seed(live, Choice, FinaleAgent, MissionState.Offered, fixture.Clock.Now);
        Assert.Empty(fixture.Missions.EpicArcChoices(Pilot, FinaleAgent));
        var active = fixture.Missions.Act(Pilot, FinaleAgent, MissionAction.Accept, branch).Offer!;
        Assert.Equal(MissionKind.Choice, active.Kind);
        Assert.Equal(long.MaxValue, active.ExpiresAt);
        Assert.Throws<InvalidDataException>(() => fixture.Missions.Act(Pilot, FinaleAgent, MissionAction.Complete, branch));
        Assert.Equal(new[] { SecondBranch, FirstBranch }, fixture.Missions.EpicArcChoices(Pilot, FinaleAgent).Select(c => c.MissionId));
        Assert.Throws<InvalidDataException>(() => fixture.Missions.ChooseEpicArc(Pilot, FinaleAgent, branch + 1, FirstBranch));
        Assert.Throws<InvalidDataException>(() => fixture.Missions.ChooseEpicArc(Pilot, FinaleAgent, branch, Root));

        await live.FlushAsync();
        fixture.StopResident();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        var terminal = missions.ChooseEpicArc(Pilot, FinaleAgent, branch, FirstBranch).Continuation!;
        Assert.Equal(FirstBranch, terminal.MissionId);
        Assert.Throws<InvalidDataException>(() => missions.ChooseEpicArc(Pilot, FinaleAgent, branch, SecondBranch));
        Assert.DoesNotContain(missions.ArcStatus(Pilot), row => row.MissionId == SecondBranch);

        missions.Act(Pilot, FinaleAgent, MissionAction.Accept, terminal.Id);
        var settled = missions.Act(Pilot, FinaleAgent, MissionAction.Complete, terminal.Id);
        Assert.Null(settled.Continuation);
        Assert.Equal(0.5, Assert.Single(settled.Standings, change => change.SourceId == ExitFaction).After, 8);
        Assert.Contains(missions.ArcStatus(Pilot), row => row.ArcId == Arc && row.MissionId == FirstBranch && row.CompletedAt.HasValue);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromDays(90).Ticks, missions.EpicArcReplayAt(Pilot, StartingAgent));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(90) - TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidDataException>(() => missions.Act(Pilot, StartingAgent, MissionAction.Request));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Dock(LiveWorld.Find(fixture.Db.Path)!, MissionFixture.StationId);
        Assert.Equal(Root, missions.Act(Pilot, StartingAgent, MissionAction.Request).Offer!.MissionId);
        Assert.Equal(Root, Assert.Single(missions.ArcStatus(Pilot)).MissionId);
    }

    [Fact]
    public void ReferralCompletesOnlyAtTheTargetAgentAndCreatesOneOffer()
    {
        using var fixture = Create();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        long offer = Seed(live, Referral, ReferralAgent, MissionState.Accepted, fixture.Clock.Now);
        Assert.Null(fixture.Missions.ArriveEpicArcAgent(Pilot, ReferralAgent));
        Assert.Throws<InvalidDataException>(() => fixture.Missions.Act(Pilot, ReferralAgent, MissionAction.Complete, offer));
        Dock(live, MissionFixture.DestinationId);
        var update = fixture.Missions.ArriveEpicArcAgent(Pilot, FinaleAgent)!;
        Assert.Equal(offer, update.Offer!.Id);
        Assert.Equal(14961, update.Continuation!.MissionId);
        Assert.Equal(FinaleAgent, update.Continuation.Agent.Id);
        Assert.Null(fixture.Missions.ArriveEpicArcAgent(Pilot, FinaleAgent));
        Assert.Equal(update.Continuation.Id, Assert.Single(fixture.Missions.Journal(Pilot)).Id);
    }

    private static MissionFixture Create() =>
        new(
            live: true,
            configure: catalog =>
            {
                MissionFixture.Agent(catalog, StartingAgent, AgentType.EpicArc, 1, MissionRules.SecurityDivision);
                MissionFixture.Agent(catalog, ReferralAgent, AgentType.EpicArc, 4, MissionRules.SecurityDivision);
                MissionFixture.Agent(catalog, FinaleAgent, AgentType.EpicArc, 4, MissionRules.SecurityDivision);
                string destination = MissionFixture.DestinationId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string finale = FinaleAgent.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (
                    catalog.Database.ExecuteSql(
                        $"""
                        UPDATE SourceRecord SET Data=json_set(Data,'$.stationID',cast({destination} AS INTEGER))
                        WHERE Dataset='client/npccharacters' AND Key={finale}
                        """
                    ) != 1
                )
                {
                    throw new InvalidOperationException("Failed to place the synthetic epic arc contact.");
                }

                var sequences = new (int Mission, long Agent, int[] Next)[]
                {
                    (14851, StartingAgent, [14852]),
                    (14852, StartingAgent, [14853]),
                    (14853, StartingAgent, [14854]),
                    (14854, StartingAgent, [14855]),
                    (14855, StartingAgent, [14856]),
                    (14856, StartingAgent, [14932]),
                    (14932, StartingAgent, [14934]),
                    (14934, ReferralAgent, [14935]),
                    (14935, ReferralAgent, [14936]),
                    (14936, ReferralAgent, [14937]),
                    (14937, ReferralAgent, [Referral]),
                    (Referral, ReferralAgent, [14961]),
                    (14961, FinaleAgent, [14962]),
                    (14962, FinaleAgent, [14963]),
                    (14963, FinaleAgent, [14965]),
                    (14965, FinaleAgent, [Choice]),
                    (Choice, FinaleAgent, [SecondBranch, FirstBranch]),
                    (SecondBranch, FinaleAgent, []),
                    (FirstBranch, FinaleAgent, []),
                };
                var graph = sequences.ToDictionary(row => row.Mission, row => new { agentID = row.Agent, nextMissions = row.Next });
                string rawGraph = JsonSerializer.Serialize(new { arcRestartInterval = 129600, missions = graph });
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/epicarcs','53',{rawGraph})");
                var room = EpicArcCatalog.Find(Root)!.Rooms.Single();
                string rooms = JsonSerializer.Serialize(
                    new
                    {
                        entryObjectID = room.EntryObjectId,
                        rooms = new Dictionary<int, object>
                        {
                            [room.RoomId] = new
                            {
                                objects = new Dictionary<long, object>
                                {
                                    [room.EntryObjectId] = new
                                    {
                                        objectID = room.EntryObjectId,
                                        typeID = 10124,
                                        position = new
                                        {
                                            x = 0,
                                            y = 0,
                                            z = 0,
                                        },
                                    },
                                },
                            },
                        },
                    }
                );
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/dungeons','3906',{rooms})");
                catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (32099,'Test trophy',12)");
                foreach (int mission in new[] { Root, Referral, Choice, 14961, FirstBranch, SecondBranch })
                {
                    string type = mission == Choice ? "EpicArcAgentInteractionMission" : "EpicArcTalkToAgentMission";
                    var standings = mission == FirstBranch ? new Dictionary<int, double> { [ExitFaction] = 5 } : null;
                    string raw = JsonSerializer.Serialize(
                        new { contentTemplate = "agent.missionTemplatizedContent_" + type, extraStandings = standings }
                    );
                    string key = mission.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/missions',{key},{raw})");
                    foreach (int typeId in EpicArcCatalog.Find(mission)!.ItemReward?.Types ?? [])
                    {
                        catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES ({typeId},'Test reward',12)");
                        string rewardKey = typeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        const string rewardType = """{"volume":1}""";
                        catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO SourceRecord VALUES ('client/types',{rewardKey},{rewardType})");
                    }
                }
            }
        );

    private static void Dock(LiveWorld live, int station)
    {
        using var world = live.Economy.Begin(new(pilots: [Pilot]));
        world.Pilots.Find(Pilot)!.ActiveShip.StationId = station;
        world.Commit();
    }

    private static long Seed(LiveWorld live, int mission, long agent, MissionState state, long now)
    {
        using var world = live.Economy.Begin(new(pilots: [Pilot]));
        var row = world
            .Add(
                new SavedMission
                {
                    PilotId = Pilot,
                    AgentId = agent,
                    MissionId = mission,
                    Level = 4,
                    State = state,
                    OfferedAt = now,
                    AcceptedAt = state != MissionState.Offered ? now : null,
                    ClosedAt = state == MissionState.Completed ? now : null,
                    ExpiresAt = long.MaxValue,
                    DestinationStation = MissionFixture.StationId,
                    SystemId = 30000142,
                }
            )
            .Entity;
        world.Commit();
        return row.Id;
    }
}
