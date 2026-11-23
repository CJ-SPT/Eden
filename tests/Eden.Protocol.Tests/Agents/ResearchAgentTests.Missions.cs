using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class ResearchAgentTests
{
    private const int Tritanium = 34;
    private const int SpecialDeliveryPackage = 11068;
    private const int MineralAcquisitionLevelOne = 2792;
    private const int SpecialDeliveryMission = 2804;

    [Fact]
    public async Task DailyMissionsUseNativeActionsAndSettlePersonalResearchAcrossCheckpoint()
    {
        using var f = Create(live: true);
        long pilot = MissionFixture.PilotId;
        long agent = MissionFixture.AgentId;
        string service = $"@agent:{agent}";
        long request = (int)MissionAction.Request;
        Assert.Equal([(long)ResearchAction.Start], Buttons(f.Call(service, "DoAction", [null])));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [request]));

        var project = Start(f);
        Assert.Equal([request, (long)ResearchAction.Buy, (long)ResearchAction.Cancel], Buttons(f.Call(service, "DoAction", [null])));
        var offered = Buttons(f.Call(service, "DoAction", [request]));
        Assert.Contains((long)MissionAction.Accept, offered);
        Assert.Equal(
            [(long)MissionAction.View, (long)ResearchAction.Buy, (long)ResearchAction.Cancel],
            Buttons(f.Call(service, "DoAction", [null]))
        );
        var trade = f.Missions.Current(pilot, agent)!;
        Assert.Equal(MineralAcquisitionLevelOne, trade.MissionId);
        Assert.Equal(MissionKind.Trade, trade.Kind);
        Assert.Equal(300, trade.Retrieval!.Quantity);
        Assert.Equal(Tritanium, trade.Retrieval.TypeId);
        Assert.Equal(project.PointsPerDay, trade.ResearchPoints!.Value, 8);
        Assert.Null(trade.Bonus);
        _ = f.Accept(trade);
        var objectives = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
        Assert.Empty(Assert.IsType<List<object?>>(objectives["normalRewards"]));
        Assert.Empty(Assert.IsType<List<object?>>(objectives["agentGift"]));
        Assert.Equal(0L, objectives["loyaltyPoints"]);
        Assert.Equal(project.PointsPerDay, Assert.IsType<double>(objectives["researchPoints"]), 8);
        var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
        var missionRow = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
        Assert.Equal("UI/Agents/MissionTypes/ResearchTrade", BlueMarshal.Text(missionRow[2]));

        long complete = trade.Id * MissionRules.ActionStride + (int)MissionAction.Complete;
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [complete]));
        var live = LiveWorld.Find(f.Db.Path)!;
        using (var world = live.Economy.Begin(new(pilots: [pilot])))
        {
            Assert.Empty(world.Filter<OwnedItem>(i => i.OwnerId == pilot && i.TypeId == Tritanium));
            world.Add(
                new SavedMissionProgress
                {
                    PilotId = pilot,
                    FactionId = trade.Agent.FactionId!.Value,
                    Level = 1,
                    Completed = 15,
                }
            );
            world.Add(
                new OwnedItem
                {
                    Id = world.Next("WorldItem"),
                    OwnerId = pilot,
                    LocationId = MissionFixture.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = Tritanium,
                    Quantity = 307,
                }
            );
            world.Commit();
        }
        Assert.False(f.Missions.CanShareReward(pilot, agent, trade.Id));
        Assert.Throws<InvalidDataException>(() => f.Missions.CompleteWithFleet(pilot, agent, trade.Id));
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(12));
        Assert.IsType<PilotCallResult.Success>(f.Call(service, "DoAction", [complete]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [complete]));
        Assert.Contains(f.Context.Notifications, n => n is ResearchChanged { Action: "research_update_ppd" });
        Assert.Contains(f.Context.Notifications, n => n is AgentMissionChanged { Action: "offered", AgentId: MissionFixture.StorylineId });
        Assert.Equal([(long)ResearchAction.Buy, (long)ResearchAction.Cancel], Buttons(f.Call(service, "DoAction", [null])));
        Assert.Equal(project.PointsPerDay * 1.5, Store(f).Research(pilot, agent).Single().Points, 8);
        long next = ResearchRules.NextMissionAt(trade.Id, f.Clock.Now);
        await live.FlushAsync();
        f.StopResident();

        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        live = LiveWorld.Find(f.Db.Path)!;
        live.Economy.Inspect(world =>
        {
            Assert.Equal(7, Assert.Single(world.Filter<OwnedItem>(i => i.OwnerId == pilot && i.TypeId == Tritanium)).Quantity);
            Assert.Empty(world.Filter<SavedMarketPayment>(payment => payment.OrderId == trade.Id));
            return true;
        });
        Assert.Equal(StartingCents, live.WalletBalance(pilot));
        Assert.Equal(MissionState.Completed, missions.Current(pilot, agent)!.State);
        Assert.Equal(MissionState.Offered, missions.Current(pilot, MissionFixture.StorylineId)!.State);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(next - f.Clock.Now - 1));
        Assert.Throws<InvalidDataException>(() => missions.Act(pilot, agent, MissionAction.Request));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        var courier = missions.Act(pilot, agent, MissionAction.Request).Offer!;
        Assert.Equal(SpecialDeliveryMission, courier.MissionId);
        Assert.Equal(SpecialDeliveryPackage, courier.Cargo!.TypeId);
        var accepted = missions.Act(pilot, agent, MissionAction.Accept, courier.Id);
        Assert.True(Assert.Single(accepted.Items).Item.Singleton);
        Assert.Throws<InvalidDataException>(() => missions.Act(pilot, agent, MissionAction.Complete, courier.Id));
        using (var world = live.Economy.Begin(new(pilots: [pilot])))
        {
            world.Set<OwnedItem>().Find(accepted.Items[0].Item.Id)!.LocationId = courier.Cargo.DestinationStation;
            world.Pilots.Find(pilot)!.ActiveShip.StationId = courier.Cargo.DestinationStation;
            world.Commit();
        }
        var settled = missions.Act(pilot, agent, MissionAction.Complete, courier.Id);
        Assert.Null(settled.BalanceCents);
        Assert.Equal(2, settled.Standings.Count);
        Assert.Equal(MissionState.Completed, settled.Offer!.State);
        Assert.Equal(courier.ResearchPoints!.Value, settled.Offer.ResearchPoints!.Value, 8);
    }

    [Fact]
    public void CancelResearchClosesActiveMissionAndRestartingCannotBypassCooldown()
    {
        using var f = Create(live: true);
        long pilot = MissionFixture.PilotId;
        long agent = MissionFixture.AgentId;
        var project = Start(f);
        var trade = f.Accept(f.Request());
        var prompt = Assert.IsType<PilotCallResult.ClientCall>(f.Call($"@agent:{agent}", "DoAction", [(long)ResearchAction.Cancel]));
        _ = MissionFixture.Reply(prompt.Dialog.Complete(true));

        Assert.Equal(MissionState.Quit, f.Missions.Current(pilot, agent)!.State);
        Assert.Empty(f.Missions.Journal(pilot, agent));
        Assert.Collection(
            f.Context.Notifications,
            notification => Assert.Equal(new AgentMissionChanged("quit", agent), notification),
            notification => Assert.Equal(new ResearchChanged("research_update_ppd"), notification)
        );
        var replacement = Start(f);
        Assert.NotEqual(project.Id, replacement.Id);
        Assert.Throws<InvalidDataException>(() => f.Missions.Act(pilot, agent, MissionAction.Complete, trade.Id));
        Assert.Throws<InvalidDataException>(() => f.Request());
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(23));
        Assert.Equal(SpecialDeliveryMission, f.Request().MissionId);
    }

    private static long[] Buttons(PilotCallResult result)
    {
        var reply = Assert.IsType<object?[]>(MissionFixture.Reply(result));
        var conversation = Assert.IsType<object?[]>(reply[0]);
        return Assert.IsType<List<object?>>(conversation[1]).Select(button => (long)Assert.IsType<object?[]>(button)[1]!).ToArray();
    }
}
