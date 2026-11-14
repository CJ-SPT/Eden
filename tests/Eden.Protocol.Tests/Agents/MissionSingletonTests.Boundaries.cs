using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Agents;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionSingletonTests
{
    private const int CargoContainerType = 23;
    private const int SecureCodedPackageType = 26904;

    public enum CargoRejection
    {
        Packaged,
        ForeignOwner,
        OtherStation,
        NestedContainer,
        SpecializedHold,
        InvalidQuantity,
    }

    [Theory]
    [InlineData(SalveForSis, CargoRejection.Packaged)]
    [InlineData(SalveForSis, CargoRejection.ForeignOwner)]
    [InlineData(SalveForSis, CargoRejection.OtherStation)]
    [InlineData(SalveForSis, CargoRejection.NestedContainer)]
    [InlineData(SalveForSis, CargoRejection.SpecializedHold)]
    [InlineData(SalveForSis, CargoRejection.InvalidQuantity)]
    [InlineData(TransactionDataDelivery, CargoRejection.Packaged)]
    [InlineData(TransactionDataDelivery, CargoRejection.ForeignOwner)]
    [InlineData(TransactionDataDelivery, CargoRejection.OtherStation)]
    [InlineData(TransactionDataDelivery, CargoRejection.NestedContainer)]
    [InlineData(TransactionDataDelivery, CargoRejection.SpecializedHold)]
    [InlineData(TransactionDataDelivery, CargoRejection.InvalidQuantity)]
    public void InvalidSingletonCargoCannotCompleteOrChangeInventoryAndRewards(int id, CargoRejection rejection)
    {
        using var f = Create(id);
        var offer = Offer(f, id);
        var item = Accept(f, offer);
        Transport(f, offer, item);
        var live = LiveWorld.Find(f.Db.Path)!;
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var cargo = action.Set<OwnedItem>().Find(item.Id)!;
            switch (rejection)
            {
                case CargoRejection.Packaged:
                    cargo.Singleton = false;
                    break;
                case CargoRejection.ForeignOwner:
                    action.Add(new InventoryOwner { Id = offer.Agent.CorporationId });
                    cargo.OwnerId = offer.Agent.CorporationId;
                    break;
                case CargoRejection.OtherStation:
                    cargo.LocationId = MissionFixture.StationId;
                    cargo.FlagId = (int)InventoryFlag.Hangar;
                    break;
                case CargoRejection.NestedContainer:
                    var container = AddCargo(action, CargoContainerType, true);
                    container.LocationId = f.Context.Pilot.ShipId;
                    container.FlagId = (int)InventoryFlag.Cargo;
                    cargo.LocationId = container.Id;
                    break;
                case CargoRejection.SpecializedHold:
                    cargo.FlagId = (int)InventoryFlag.FleetHangar;
                    break;
                case CargoRejection.InvalidQuantity:
                    cargo.Quantity = 2;
                    break;
            }
            action.Commit();
        }
        var unchanged = Snapshot(live, item.Id);
        long balance = live.WalletBalance(MissionFixture.PilotId);
        Assert.False(f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.ObjectiveComplete);
        Assert.Throws<InvalidDataException>(() => f.Missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id));
        Assert.Equal(unchanged, Snapshot(live, item.Id));
        Assert.Equal(balance, live.WalletBalance(MissionFixture.PilotId));
        Assert.Equal(MissionState.Accepted, f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.State);
        AssertNoPayment(live, offer.Id);
    }

    [Theory]
    [InlineData(SalveForSis)]
    [InlineData(TransactionDataDelivery)]
    public void RewardFailureRollsBackSingletonConsumptionAndNativeCompletionRemovesItOnce(int id)
    {
        using var f = Create(id);
        var offer = Offer(f, id);
        var item = Accept(f, offer);
        Transport(f, offer, item);
        var unloaded = Assert.Single(
            f.Services.GetRequiredService<IPilotInventory>()
                .Move(
                    MissionFixture.PilotId,
                    offer.Cargo!.DestinationStation,
                    new([item.Id], f.Context.Pilot.ShipId, (int)InventoryFlag.Hangar, null)
                )
        );
        Assert.True(unloaded.Item.Singleton);
        Assert.Equal(item.Id, unloaded.Item.Id);
        var live = LiveWorld.Find(f.Db.Path)!;
        SetWallet(live, long.MaxValue);
        var original = Snapshot(live, item.Id);
        Assert.Throws<OverflowException>(() => f.Missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id));
        Assert.Equal(original, Snapshot(live, item.Id));
        Assert.Equal(long.MaxValue, live.WalletBalance(MissionFixture.PilotId));
        Assert.Equal(MissionState.Accepted, f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.State);
        AssertNoPayment(live, offer.Id);
        SetWallet(live, 0);
        int before = f.Context.Notifications.Count;
        long token = offer.Id * MissionRules.ActionStride + (int)MissionAction.Complete;
        Assert.IsType<PilotCallResult.Success>(f.Call($"@agent:{offer.Agent.Id}", "DoAction", [token]));
        var removed = Assert.Single(f.Context.Notifications.Skip(before).OfType<ItemChanged>());
        Assert.Equal(item.Id, removed.Item.Id);
        Assert.Equal((int)InventoryLocation.Graveyard, removed.Item.LocationId);
        Assert.Equal(InventoryWireDefinitions.AssembledQuantity, removed.Item.Quantity);
        Assert.False(removed.Previous.ContainsKey(InventoryColumn.Quantity));
        Assert.Contains(f.Context.Notifications.Skip(before), n => n is AgentMissionChanged { Action: "completed" });
        Assert.IsType<PilotCallResult.Failure>(f.Call($"@agent:{offer.Agent.Id}", "DoAction", [token]));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(-1, 1)]
    public void InvalidCatalogSingletonFormsRejectBeforeIssuingCargo(int singleton, int quantity)
    {
        using MissionFixture f = new(
            live: true,
            configure: catalog =>
            {
                catalog.Database.ExecuteSql(
                    $"""
                    UPDATE SourceRecord SET Data=json_set(Data,'$.courierMission.objectiveSingleton',{singleton},
                        '$.courierMission.objectiveQuantity',{quantity})
                    WHERE Dataset='client/missions' AND Key='4837'
                    """
                );
            }
        );
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        Assert.Throws<InvalidDataException>(() => MissionCatalog.Find(catalog, SalveForSis));
        Assert.DoesNotContain(
            f.Services.GetRequiredService<IPilotInventory>().List(MissionFixture.PilotId, MissionFixture.StationId),
            item => item.TypeId == SecureCodedPackageType
        );
    }

    private static void SetWallet(LiveWorld live, long cents)
    {
        using var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        var wallet = action.Set<PilotWallet>().Find(MissionFixture.PilotId);
        if (wallet is null)
        {
            wallet = action.Add(new PilotWallet { PilotId = MissionFixture.PilotId }).Entity;
        }
        wallet.Cents = cents;
        action.Commit();
    }

    private static void AssertNoPayment(LiveWorld live, long offer) =>
        live.Economy.Inspect(world =>
        {
            Assert.Empty(world.Set<SavedMarketPayment>().Where(p => p.OrderId == offer));
            return true;
        });

    private sealed record CargoSnapshot(long Owner, long Location, int Flag, bool Singleton, int Quantity);

    private static CargoSnapshot Snapshot(LiveWorld live, long id) =>
        live.Economy.Inspect(world =>
        {
            var item = world.Set<OwnedItem>().Find(id)!;
            return new CargoSnapshot(item.OwnerId, item.LocationId, item.FlagId, item.Singleton, item.Quantity);
        });
}
