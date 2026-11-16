using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionTradeTests
{
    [Theory]
    [InlineData(15553, 1, 1230, 1000)]
    [InlineData(15554, 2, 1228, 1500)]
    [InlineData(15555, 3, 1227, 10000)]
    [InlineData(15556, 4, 20, 8000)]
    public async Task TradeOffersQuoteOneImplantAndConsumePlayerOreOnceAcrossCheckpoint(int mission, int level, int ore, int quantity)
    {
        using var f = Create(mission, level);
        var offer = Offer(f);
        Assert.Equal(mission, offer.MissionId);
        Assert.Equal(level, offer.Level);
        Assert.True(offer.Storyline);
        Assert.Equal(MissionKind.Trade, offer.Kind);
        Assert.Null(offer.Cargo);
        Assert.Null(offer.DungeonId);
        Assert.Equal(ore, offer.Retrieval!.TypeId);
        Assert.Equal(quantity, offer.Retrieval.Quantity);
        Assert.False(offer.ObjectiveComplete);
        var quote = Assert.IsType<MissionItemReward>(offer.ItemReward);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            Assert.Contains(quote.TypeId, MissionCatalog.Find(catalog, mission).Definition.ItemReward!.Types);
        }
        await Live(f).FlushAsync();
        f.StopResident();
        ServiceProvider? reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        try
        {
            Assert.Equal(quote, f.Missions.Current(MissionFixture.PilotId, MissionFixture.StorylineId)!.ItemReward);
            f.Dock(MissionFixture.StationId);
            f.Accept(offer);
            Live(f)
                .Economy.Inspect(world =>
                {
                    Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == ore));
                    Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == quote.TypeId));
                    return true;
                });
            Assert.Empty(f.Missions.Sites(offer.SystemId));
            Assert.Null(f.Missions.EntryPoint(MissionFixture.PilotId, offer.Agent.Id));
            Assert.Throws<InvalidDataException>(() => Complete(f, offer));
            string service = $"@agent:{offer.Agent.Id}";
            var objective = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
            Assert.Empty(Assert.IsType<List<object?>>(objective["agentGift"]));
            Assert.Empty(Assert.IsType<List<object?>>(objective["dungeons"]));
            var fetch = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objective["objectives"])));
            Assert.Equal("fetch", BlueMarshal.Text(fetch[0]));
            var rewards = Assert.IsType<List<object?>>(objective["normalRewards"]);
            Assert.Equal(2, rewards.Count);
            Assert.Equal(new object?[] { (long)quote.TypeId, 1L, null }, Assert.IsType<object?[]>(rewards[1]));
            var keywords = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionKeywords", [(long)mission])));
            Assert.Equal((long)MissionFixture.StationId, keywords["objectiveLocationID"]);
            var journal = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call("agentMgr", "GetMyJournalDetails", [])));
            var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
            Assert.Equal("UI/Agents/MissionTypes/StorylineTrade", BlueMarshal.Text(row[2]));
            Assert.Empty(Assert.IsType<List<object?>>(row[6]));
            Assert.Equal(false, row[8]);
            Assert.Equal(
                "MissionFetch",
                BlueMarshal.Text(
                    Assert.IsType<List<object?>>(
                        MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [offer.Agent.Id]))
                    )[0]
                )
            );
            AddItem(f, ore, quantity + 7);
            Assert.True(f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.ObjectiveComplete);
            long balance = Live(f).WalletBalance(MissionFixture.PilotId);
            long token = offer.Id * MissionRules.ActionStride + (int)MissionAction.Complete;
            Assert.IsType<PilotCallResult.Success>(f.Call(service, "DoAction", [token]));
            Assert.Equal(balance + offer.Rewards.IskCents + offer.Bonus!.IskCents, Live(f).WalletBalance(MissionFixture.PilotId));
            Assert.Contains(f.Context.Notifications, n => n.Name == "OnItemChange");
            int notifications = f.Context.Notifications.Count;
            Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [token]));
            Assert.Equal(notifications, f.Context.Notifications.Count);
            Assert.Equal(0, offer.Rewards.LoyaltyPoints);
            await Live(f).FlushAsync();
            reopened.Dispose();
            reopened = null;
            f.StopResident();
            reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
            var completed = f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!;
            Assert.Equal(MissionState.Completed, completed.State);
            Assert.Equal(quote, completed.ItemReward);
            Assert.True(completed.ObjectiveComplete);
            Live(f)
                .Economy.Inspect(world =>
                {
                    Assert.Equal(7, world.Set<OwnedItem>().Single(i => i.TypeId == ore).Quantity);
                    var implant = Assert.Single(world.Set<OwnedItem>().Where(i => i.TypeId == quote.TypeId));
                    Assert.Equal(1, implant.Quantity);
                    Assert.Equal(MissionFixture.PilotId, implant.OwnerId);
                    Assert.Equal(MissionFixture.StationId, implant.LocationId);
                    Assert.Equal((int)InventoryFlag.Hangar, implant.FlagId);
                    Assert.Single(
                        world.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id && p.Kind == WalletEntryType.AgentMissionReward)
                    );
                    return true;
                });
            Assert.Throws<InvalidDataException>(() => Complete(f, offer));
        }
        finally
        {
            reopened?.Dispose();
        }
    }

    private static MissionFixture Create(int mission = 15553, int level = 1)
    {
        var fixture = new MissionFixture(
            level,
            live: true,
            configure: catalog =>
            {
                string selected = mission.ToString(CultureInfo.InvariantCulture);
                string regular = (2911 + level).ToString(CultureInfo.InvariantCulture);
                catalog.Database.ExecuteSql(
                    $"DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key NOT IN ({selected},{regular})"
                );
            }
        );
        using var action = Live(fixture).Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        action.Add(
            new SavedNpcStanding
            {
                PilotId = MissionFixture.PilotId,
                SourceId = MissionFixture.AgentId,
                Value = 8,
            }
        );
        action.Add(
            new SavedMissionProgress
            {
                PilotId = MissionFixture.PilotId,
                FactionId = 500001,
                Level = level,
                Completed = MissionRules.StorylineInterval - 1,
            }
        );
        action.Commit();
        return fixture;
    }

    private static MissionOffer Offer(MissionFixture f)
    {
        var regular = f.Accept(f.Request());
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var cargo = action.Set<OwnedItem>().Single(i => i.TypeId == regular.Cargo!.TypeId);
            cargo.LocationId = regular.Cargo!.DestinationStation;
            action.Commit();
        }
        f.Dock(regular.Cargo!.DestinationStation);
        f.Missions.Act(MissionFixture.PilotId, regular.Agent.Id, MissionAction.Complete, regular.Id);
        return Assert.Single(f.Missions.Journal(MissionFixture.PilotId));
    }

    private static LiveWorld Live(MissionFixture f) => LiveWorld.Find(f.Db.Path)!;

    private static MissionUpdate Complete(MissionFixture f, MissionOffer offer) =>
        f.Missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id);

    private static long AddItem(
        MissionFixture f,
        int type,
        int quantity,
        long? location = null,
        int? flag = null,
        long? owner = null,
        bool singleton = false
    )
    {
        using var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        var item = action
            .Add(
                new OwnedItem
                {
                    Id = action.NextItemId(),
                    OwnerId = owner ?? MissionFixture.PilotId,
                    LocationId = location ?? MissionFixture.StationId,
                    FlagId = flag ?? (int)InventoryFlag.Hangar,
                    TypeId = type,
                    Quantity = quantity,
                    Singleton = singleton,
                }
            )
            .Entity;
        action.Commit();
        return item.Id;
    }
}
