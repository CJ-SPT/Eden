using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Market;
using Eden.Protocol.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionStorylineProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingEligibleStorylineContentCannotRollbackEarnedMissionRewards(bool restricted)
    {
        using MissionFixture fixture = new(
            live: true,
            configure: catalog =>
            {
                catalog.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key NOT IN ('2912','2919')");
                if (restricted)
                {
                    catalog.Database.ExecuteSqlRaw(
                        """
                        UPDATE SourceRecord SET Data=json_set(Data,'$.corporationID',1000045)
                        WHERE Dataset='client/missions' AND Key='2919'
                        """
                    );
                }
                else
                {
                    catalog.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key='2919'");
                }
            }
        );
        var mission = PrepareCompletion(fixture);
        var reply = fixture.Call(
            "@agent:" + mission.Agent.Id,
            "DoAction",
            [mission.Id * MissionRules.ActionStride + (long)MissionAction.Complete]
        );
        Assert.IsType<PilotCallResult.Success>(reply);
        Assert.Contains(fixture.Context.Notifications.OfType<AgentMissionChanged>(), n => n.Action == "completed");
        var live = LiveWorld.Find(fixture.Db.Path)!;
        Check(live);
        await live.FlushAsync();
        fixture.StopResident();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        Check(reopened.GetRequiredService<LiveWorld>());
        Assert.Throws<InvalidDataException>(() =>
            reopened.GetRequiredService<IPilotMissions>().Act(MissionFixture.PilotId, mission.Agent.Id, MissionAction.Complete, mission.Id)
        );

        void Check(LiveWorld world) =>
            world.Economy.Inspect(action =>
            {
                Assert.Equal(MissionState.Completed, action.Set<SavedMission>().Find(mission.Id)!.State);
                Assert.Equal(MissionRules.StorylineInterval, Assert.Single(action.Set<SavedMissionProgress>()).Completed);
                Assert.Empty(action.Set<OwnedItem>().Where(i => i.TypeId == mission.Cargo!.TypeId));
                Assert.Single(
                    action.Set<SavedMarketPayment>().Where(p => p.OrderId == mission.Id && p.Kind == WalletEntryType.AgentMissionReward)
                );
                Assert.DoesNotContain(action.Set<SavedMission>(), m => m.Storyline);
                return true;
            });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ExpiredStorylineOffersReleaseTheirAgentAtTheExactDeadline(long expiryOffset)
    {
        using MissionFixture fixture = new(live: true);
        var mission = PrepareCompletion(fixture);
        var live = LiveWorld.Find(fixture.Db.Path)!;
        long oldId;
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var old = action
                .Add(
                    new SavedMission
                    {
                        PilotId = MissionFixture.PilotId,
                        AgentId = MissionFixture.StorylineId,
                        MissionId = 2919,
                        Level = 1,
                        Storyline = true,
                        State = MissionState.Offered,
                        OfferedAt = fixture.Clock.Now - TimeSpan.TicksPerDay,
                        ExpiresAt = fixture.Clock.Now + expiryOffset,
                        DestinationStation = MissionFixture.DestinationId,
                        SystemId = mission.SystemId,
                    }
                )
                .Entity;
            action.SaveChanges();
            oldId = old.Id;
            action.Commit();
        }
        fixture.Missions.Act(MissionFixture.PilotId, mission.Agent.Id, MissionAction.Complete, mission.Id);
        live.Economy.Inspect(action =>
        {
            var old = action.Set<SavedMission>().Find(oldId)!;
            Assert.Equal(expiryOffset <= 0 ? MissionState.Failed : MissionState.Offered, old.State);
            Assert.Equal(expiryOffset <= 0 ? fixture.Clock.Now : (long?)null, old.ClosedAt);
            Assert.Equal(
                expiryOffset <= 0 ? 0 : MissionRules.StorylineInterval,
                Assert.Single(action.Set<SavedMissionProgress>()).Completed
            );
            var offered = Assert.Single(action.Set<SavedMission>().Where(m => m.Storyline && m.ClosedAt == null));
            Assert.Equal(expiryOffset > 0, offered.Id == oldId);
            return true;
        });
    }

    private static MissionOffer PrepareCompletion(MissionFixture fixture)
    {
        var mission = fixture.Accept(fixture.Request());
        using var action = LiveWorld.Find(fixture.Db.Path)!.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        action.Add(
            new SavedMissionProgress
            {
                PilotId = MissionFixture.PilotId,
                FactionId = mission.Agent.FactionId!.Value,
                Level = mission.Level,
                Completed = MissionRules.StorylineInterval - 1,
            }
        );
        action.Set<OwnedItem>().Single(i => i.TypeId == mission.Cargo!.TypeId).LocationId = mission.Cargo!.DestinationStation;
        action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = mission.Cargo.DestinationStation;
        action.Commit();
        return mission;
    }
}
