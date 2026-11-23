using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Characters;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionStandingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedFailureSettlesDirectStandingsOnceAcrossCheckpoint(bool expires)
    {
        using MissionFixture fixture = new(live: true);
        var expiredOffer = fixture.Request();
        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(expiredOffer.ExpiresAt);
        Assert.Empty(fixture.Missions.Journal(MissionFixture.PilotId));
        var history = fixture.Services.GetRequiredService<IPilotInteractionHistory>();
        Assert.Empty(history.Standings(MissionFixture.PilotId));

        var mission = fixture.Accept(fixture.Request());
        if (expires)
        {
            var notifications = fixture.Handler<CooperativeFleetHandler>();
            notifications.Advance(fixture.Context);
            fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(mission.ExpiresAt - 1);
            Assert.Equal(MissionState.Accepted, fixture.Missions.Current(MissionFixture.PilotId, mission.Agent.Id)!.State);
            Assert.Empty(history.Standings(MissionFixture.PilotId));
            fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(mission.ExpiresAt);
            Assert.Empty(fixture.Missions.Journal(MissionFixture.PilotId));
            notifications.Advance(fixture.Context);
            Assert.Equal(2, fixture.Context.Notifications.OfType<NpcStandingSet>().Count());
        }
        else
        {
            var update = fixture.Missions.Act(MissionFixture.PilotId, mission.Agent.Id, MissionAction.Quit, mission.Id);
            Assert.Equal(MissionState.Quit, update.Offer!.State);
            Assert.Equal(2, update.Standings.Count);
            Assert.Null(update.BalanceCents);
        }
        AssertFailureTransactions(history, mission, fixture.Clock.Now);
        await LiveWorld.Find(fixture.Db.Path)!.FlushAsync();
        fixture.StopResident();

        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        var afterRestart = missions.Current(MissionFixture.PilotId, mission.Agent.Id)!;
        Assert.Equal(mission.Id, afterRestart.Id);
        Assert.Equal(expires ? MissionState.Failed : MissionState.Quit, afterRestart.State);
        Assert.Throws<InvalidDataException>(() => missions.Act(MissionFixture.PilotId, mission.Agent.Id, MissionAction.Quit, mission.Id));
        AssertFailureTransactions(reopened.GetRequiredService<IPilotInteractionHistory>(), mission, fixture.Clock.Now);
    }

    [Fact]
    public void RepeatDeclinePublishesPenaltyAndNativeRemainingTimeUntilExactFreeBoundary()
    {
        using MissionFixture fixture = new(live: true);
        var history = fixture.Services.GetRequiredService<IPilotInteractionHistory>();
        var offer = fixture.Request();
        long agent = offer.Agent.Id;
        long corporation = offer.Agent.CorporationId;
        string service = $"@agent:{agent}";
        long firstDeclineAt = fixture.Clock.Now;
        Assert.Equal(-1L, DeclineTime());
        Assert.Empty(fixture.Missions.Act(MissionFixture.PilotId, agent, MissionAction.Decline, offer.Id).Standings);

        var repeated = fixture.Request();
        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(firstDeclineAt + MissionRules.FreeDeclineInterval.Ticks - 1);
        Assert.Equal(1L, DeclineTime());
        long action = repeated.Id * MissionRules.ActionStride + (int)MissionAction.Decline;
        Assert.IsType<PilotCallResult.Success>(fixture.Call(service, "DoAction", [action]));
        Assert.Equal(MissionState.Declined, fixture.Missions.Current(MissionFixture.PilotId, agent)!.State);
        Assert.Equal(2, fixture.Context.Notifications.OfType<NpcStandingSet>().Count());
        var historyReply = fixture.Call("standingMgr", "GetStandingTransactions", [agent, MissionFixture.PilotId]);
        var historyRow = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(MissionFixture.Reply(historyReply))));
        Assert.Equal((long)repeated.NameId, BlueRead.Fields(historyRow.State)["int_1"]);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, "DoAction", [action]));
        AssertTransaction(history, agent, StandingEvent.AgentMissionDeclined, MissionStandingRules.DeclineAgentFraction, fixture.Clock.Now);
        AssertTransaction(
            history,
            corporation,
            StandingEvent.AgentMissionDeclined,
            MissionStandingRules.DeclineCorporationFraction,
            fixture.Clock.Now
        );

        var free = fixture.Request();
        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(firstDeclineAt + MissionRules.FreeDeclineInterval.Ticks);
        Assert.Equal(-1L, DeclineTime());
        Assert.Empty(fixture.Missions.Act(MissionFixture.PilotId, agent, MissionAction.Decline, free.Id).Standings);
        Assert.Single(history.Transactions(MissionFixture.PilotId, agent));
        Assert.Single(history.Transactions(MissionFixture.PilotId, corporation));

        object? DeclineTime() => BlueRead.Fields(MissionFixture.Reply(fixture.Call(service, "GetMissionBriefingInfo", [])))["Decline Time"];
    }

    [Fact]
    public async Task StorylineDerivedChangesUseTheIncomingRelationshipSizeRatioAndDirectionalCaps()
    {
        const int caldari = 500001;
        const int minmatar = 500002;
        const int amarr = 500003;
        const int gallente = 500004;
        const int ammatar = 500007;
        const int mordusLegion = 500018;
        const int veldspar = 1230;
        using MissionFixture fixture = new(
            live: true,
            configure: catalog =>
                catalog.Database.ExecuteSqlRaw(
                    """
                    DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key NOT IN ('2912','15553');
                    INSERT OR REPLACE INTO SourceRecord (Dataset, Key, Data) VALUES
                        ('client/factions','500001',json_object('sizeFactor',5)),
                        ('client/factions','500002',json_object('sizeFactor',5)),
                        ('client/factions','500003',json_object('sizeFactor',5)),
                        ('client/factions','500004',json_object('sizeFactor',5)),
                        ('client/factions','500007',json_object('sizeFactor',4)),
                        ('client/factions','500018',json_object('sizeFactor',3));
                    """
                )
        );
        var live = LiveWorld.Find(fixture.Db.Path)!;
        var regular = fixture.Accept(fixture.Request());
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(
                new SavedMissionProgress
                {
                    PilotId = MissionFixture.PilotId,
                    FactionId = caldari,
                    Level = 1,
                    Completed = 15,
                }
            );
            var cargo = action.Set<OwnedItem>().Single(item => item.TypeId == regular.Cargo!.TypeId);
            cargo.LocationId = regular.Cargo!.DestinationStation;
            action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = regular.Cargo.DestinationStation;
            action.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = amarr,
                    Value = 4.99,
                }
            );
            action.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = gallente,
                    Value = -4.99,
                }
            );
            action.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = ammatar,
                    Value = 7,
                }
            );
            action.Commit();
        }
        var ordinary = fixture.Missions.Act(MissionFixture.PilotId, regular.Agent.Id, MissionAction.Complete, regular.Id);
        Assert.Equal(2, ordinary.Standings.Count);
        var storyline = Assert.Single(fixture.Missions.Journal(MissionFixture.PilotId));
        Assert.Equal(15553, storyline.MissionId);
        Assert.True(storyline.Storyline);
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = MissionFixture.StationId;
            action.Add(
                new OwnedItem
                {
                    Id = action.NextItemId(),
                    OwnerId = MissionFixture.PilotId,
                    LocationId = MissionFixture.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = veldspar,
                    Quantity = 1_000,
                }
            );
            action.Commit();
        }
        fixture.Accept(storyline);
        var update = fixture.Missions.Act(MissionFixture.PilotId, storyline.Agent.Id, MissionAction.Complete, storyline.Id);
        Assert.Equal(5, Assert.Single(update.Standings, s => s.SourceId == amarr).After);
        Assert.Equal(-5, Assert.Single(update.Standings, s => s.SourceId == gallente).After);
        Assert.DoesNotContain(update.Standings, s => s.SourceId is minmatar or ammatar);
        double expectedMordusChange = 10 * storyline.Rewards.FactionFraction * (9d / 10) * (5d / 3);
        Assert.Equal(expectedMordusChange, Assert.Single(update.Standings, s => s.SourceId == mordusLegion).After, 12);
        await live.FlushAsync();
        fixture.StopResident();

        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var history = reopened.GetRequiredService<IPilotInteractionHistory>();
        Assert.Equal(StandingEvent.AgentMissionCompleted, Assert.Single(history.Transactions(MissionFixture.PilotId, caldari)).Event);
        Assert.Equal(StandingEvent.DerivedModificationPositive, Assert.Single(history.Transactions(MissionFixture.PilotId, amarr)).Event);
        Assert.Equal(
            StandingEvent.DerivedModificationNegative,
            Assert.Single(history.Transactions(MissionFixture.PilotId, gallente)).Event
        );
        Assert.Equal(expectedMordusChange, Assert.Single(history.Transactions(MissionFixture.PilotId, mordusLegion)).After, 12);
        Assert.Empty(history.Transactions(MissionFixture.PilotId, minmatar));
        Assert.Empty(history.Transactions(MissionFixture.PilotId, ammatar));
    }

    private static void AssertFailureTransactions(IPilotInteractionHistory history, MissionOffer mission, long now)
    {
        Assert.Equal(2, history.Standings(MissionFixture.PilotId).Count);
        AssertTransaction(history, mission.Agent.Id, StandingEvent.AgentMissionFailed, MissionStandingRules.QuitAgentFraction, now);
        AssertTransaction(
            history,
            mission.Agent.CorporationId,
            StandingEvent.AgentMissionFailed,
            MissionStandingRules.QuitCorporationFraction,
            now
        );
    }

    private static void AssertTransaction(IPilotInteractionHistory history, long source, StandingEvent reason, double fraction, long now)
    {
        var transaction = Assert.Single(history.Transactions(MissionFixture.PilotId, source));
        Assert.Equal(reason, transaction.Event);
        Assert.Equal(now, transaction.Time);
        Assert.Equal(0, transaction.Before);
        Assert.Equal(StandingRules.FactionLoss(0, fraction), transaction.After);
        Assert.Equal(transaction.After, Assert.Single(history.Standings(MissionFixture.PilotId), s => s.SourceId == source).Value);
    }
}
