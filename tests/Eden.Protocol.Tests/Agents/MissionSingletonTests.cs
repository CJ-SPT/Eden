using System.Globalization;
using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Agents;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionSingletonTests
{
    private const int SalveForSis = 4837;
    private const int TransactionDataDelivery = 2305;
    private const int BaselineCourier = 2912;

    [Theory]
    [InlineData(2305)]
    [InlineData(2306)]
    [InlineData(2307)]
    [InlineData(2308)]
    [InlineData(2309)]
    [InlineData(2310)]
    [InlineData(2311)]
    [InlineData(2312)]
    [InlineData(SalveForSis)]
    public async Task EverySingletonDeliveryIssuesDistinctCargoAndSettlesOnceAfterCheckpoint(int id)
    {
        using var f = Create(id);
        var offer = Offer(f, id);
        var live = LiveWorld.Find(f.Db.Path)!;
        long oldSingleton;
        long packaged;
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            oldSingleton = AddCargo(action, offer.Cargo!.TypeId, true).Id;
            packaged = AddCargo(action, offer.Cargo.TypeId, false).Id;
            action.Commit();
        }
        var item = Accept(f, offer);
        Assert.NotEqual(oldSingleton, item.Id);
        Assert.NotEqual(packaged, item.Id);
        Assert.Throws<InvalidDataException>(() => f.Accept(offer));
        var inventory = f.Services.GetRequiredService<IPilotInventory>();
        var originals = inventory.List(MissionFixture.PilotId, MissionFixture.StationId).Where(i => i.TypeId == item.TypeId).ToArray();
        Assert.Equal(3, originals.Length);
        var stacks = f.Services.GetRequiredService<IPilotInventoryStacks>();
        Assert.Throws<InvalidDataException>(() =>
            stacks.Merge(MissionFixture.PilotId, MissionFixture.StationId, MissionFixture.StationId, [new(item.Id, oldSingleton, 1)])
        );
        _ = stacks.StackAll(MissionFixture.PilotId, MissionFixture.StationId, (int)InventoryFlag.Hangar);
        Assert.Equal(originals, inventory.List(MissionFixture.PilotId, MissionFixture.StationId).Where(i => i.TypeId == item.TypeId));
        Assert.Throws<InvalidDataException>(() =>
            f
                .Services.GetRequiredService<IPilotRepackaging>()
                .Repackage(MissionFixture.PilotId, MissionFixture.StationId, [new(item.Id, MissionFixture.StationId)])
        );
        Transport(f, offer, item);
        Assert.True(f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.ObjectiveComplete);
        long before = live.WalletBalance(MissionFixture.PilotId);
        await live.FlushAsync();
        f.StopResident();
        using (var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true))
        {
            var missions = reopened.GetRequiredService<IPilotMissions>();
            var accepted = missions.Current(MissionFixture.PilotId, offer.Agent.Id)!;
            Assert.Equal(MissionState.Accepted, accepted.State);
            Assert.True(accepted.ObjectiveComplete);
            var result = missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id);
            Assert.Equal(MissionState.Completed, result.Offer!.State);
            var consumed = Assert.Single(result.Items);
            Assert.Equal(item.Id, consumed.Item.Id);
            Assert.True(consumed.Item.Singleton);
            Assert.Equal(0, consumed.Item.Quantity);
            Assert.Equal(1, consumed.Previous!.Quantity);
            Assert.Equal(before + offer.Rewards.IskCents + accepted.Bonus!.Payable(f.Clock.Now), result.BalanceCents);
            Assert.Throws<InvalidDataException>(() =>
                missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id)
            );
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<OwnedItem>().Find(item.Id));
        Assert.True(saved.Set<OwnedItem>().Find(oldSingleton)!.Singleton);
        Assert.False(saved.Set<OwnedItem>().Find(packaged)!.Singleton);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(oldSingleton)!.Quantity);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(packaged)!.Quantity);
        Assert.Equal(MissionState.Completed, saved.Set<SavedMission>().Find(offer.Id)!.State);
        Assert.Single(saved.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id && p.Kind == WalletEntryType.AgentMissionReward));
    }

    private static MissionFixture Create(int id) =>
        new(
            live: true,
            configure: catalog =>
            {
                string key = id.ToString(CultureInfo.InvariantCulture);
                string baseline = (id == SalveForSis ? id : BaselineCourier).ToString(CultureInfo.InvariantCulture);
                catalog.Database.ExecuteSql($"DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key NOT IN ({key},{baseline})");
            }
        );

    private static MissionOffer Offer(MissionFixture f, int id)
    {
        if (id == SalveForSis)
        {
            var ordinary = f.Request();
            Assert.Equal(id, ordinary.MissionId);
            return ordinary;
        }
        var live = LiveWorld.Find(f.Db.Path)!;
        int faction = f.Services.GetRequiredService<INpcDirectory>().Agent(MissionFixture.AgentId)!.FactionId!.Value;
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(
                new SavedMissionProgress
                {
                    PilotId = MissionFixture.PilotId,
                    FactionId = faction,
                    Level = 1,
                    Completed = MissionRules.StorylineInterval - 1,
                }
            );
            action.Commit();
        }
        var prerequisite = f.Accept(f.Request());
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Set<OwnedItem>().Single(i => i.TypeId == prerequisite.Cargo!.TypeId).LocationId = prerequisite.Cargo!.DestinationStation;
            action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = prerequisite.Cargo.DestinationStation;
            action.Commit();
        }
        f.Missions.Act(MissionFixture.PilotId, prerequisite.Agent.Id, MissionAction.Complete, prerequisite.Id);
        var offer = Assert.Single(f.Missions.Journal(MissionFixture.PilotId));
        Assert.Equal(id, offer.MissionId);
        Assert.True(offer.Storyline);
        Assert.Equal(MissionFixture.StorylineId, offer.Agent.Id);
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = MissionFixture.StationId;
            action.Commit();
        }
        return offer;
    }

    private static InventoryItem Accept(MissionFixture f, MissionOffer offer)
    {
        int before = f.Context.Notifications.Count;
        long token = offer.Id * MissionRules.ActionStride + (int)MissionAction.Accept;
        Assert.IsType<PilotCallResult.Success>(f.Call($"@agent:{offer.Agent.Id}", "DoAction", [token]));
        var notice = Assert.Single(f.Context.Notifications.Skip(before).OfType<ItemChanged>());
        Assert.Equal(offer.Cargo!.TypeId, notice.Item.TypeId);
        Assert.Equal(InventoryWireDefinitions.AssembledQuantity, notice.Item.Quantity);
        Assert.Equal(0, notice.Previous[InventoryColumn.Location]);
        Assert.False(notice.Previous.ContainsKey(InventoryColumn.Quantity));
        Assert.Contains(f.Context.Notifications.Skip(before), n => n is AgentMissionChanged { Action: "accepted" });
        var item = Assert.Single(
            f.Services.GetRequiredService<IPilotInventory>().List(MissionFixture.PilotId, MissionFixture.StationId),
            i => i.Id == notice.Item.Id
        );
        Assert.True(item.Singleton);
        Assert.Equal(1, item.Quantity);
        return item;
    }

    private static void Transport(MissionFixture f, MissionOffer offer, InventoryItem item)
    {
        var changes = f
            .Services.GetRequiredService<IPilotInventory>()
            .Move(MissionFixture.PilotId, f.Context.Pilot.ShipId, new([item.Id], MissionFixture.StationId, (int)InventoryFlag.Cargo, null));
        var moved = Assert.Single(changes);
        Assert.Equal(item.Id, moved.Item.Id);
        Assert.True(moved.Item.Singleton);
        Assert.Equal(1, moved.Item.Quantity);
        using var action = LiveWorld.Find(f.Db.Path)!.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = offer.Cargo!.DestinationStation;
        action.Commit();
    }

    private static OwnedItem AddCargo(WorldAction action, int type, bool singleton) =>
        action
            .Add(
                new OwnedItem
                {
                    Id = action.NextItemId(),
                    OwnerId = MissionFixture.PilotId,
                    LocationId = MissionFixture.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = type,
                    Quantity = 1,
                    Singleton = singleton,
                }
            )
            .Entity;
}
