using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionTradeTests
{
    [Theory]
    [InlineData("foreign-owner")]
    [InlineData("foreign-station")]
    [InlineData("mining-hold")]
    [InlineData("nested")]
    [InlineData("singleton")]
    [InlineData("short")]
    [InlineData("wrong-type")]
    public void TradeTurnInRejectsIneligibleOreWithoutPayingOrConsumingIt(string boundary)
    {
        using var f = Create();
        var offer = Offer(f);
        f.Dock(MissionFixture.StationId);
        f.Accept(offer);
        long balance = Live(f).WalletBalance(MissionFixture.PilotId);
        long id = AddItem(
            f,
            boundary == "wrong-type" ? 1228 : offer.Retrieval!.TypeId,
            boundary == "short" ? offer.Retrieval!.Quantity - 1 : offer.Retrieval!.Quantity,
            boundary == "foreign-station" ? MissionFixture.DestinationId
                : boundary == "mining-hold" ? f.Context.Pilot.ShipId
                : boundary == "nested" ? f.Context.Pilot.ShipId + 1000
                : null,
            boundary == "mining-hold" ? (int)InventoryFlag.GeneralMiningHold : null,
            boundary == "foreign-owner" ? MissionFixture.PilotId + 1 : null,
            singleton: boundary == "singleton"
        );
        Assert.False(f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.ObjectiveComplete);
        Assert.Throws<InvalidDataException>(() => Complete(f, offer));
        Assert.Equal(balance, Live(f).WalletBalance(MissionFixture.PilotId));
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.NotNull(world.Set<OwnedItem>().Find(id));
                Assert.Equal(MissionState.Accepted, world.Set<SavedMission>().Find(offer.Id)!.State);
                Assert.Empty(world.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id));
                Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == offer.ItemReward!.TypeId));
                return true;
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentTradeTurnInsConsumeSplitStacksAndAwardOneImplant(bool shipCargo)
    {
        using var f = Create();
        var offer = Offer(f);
        f.Dock(MissionFixture.StationId);
        f.Accept(offer);
        int half = offer.Retrieval!.Quantity / 2;
        AddItem(f, offer.Retrieval.TypeId, half);
        AddItem(f, offer.Retrieval.TypeId, half, shipCargo ? f.Context.Pilot.ShipId : null, shipCargo ? (int)InventoryFlag.Cargo : null);
        AddItem(f, offer.ItemReward!.TypeId, 3);
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            Complete(f, offer);
                            return true;
                        }
                        catch (InvalidDataException)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(results, success => success);
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == offer.Retrieval.TypeId));
                Assert.Equal(4, world.Set<OwnedItem>().Single(i => i.TypeId == offer.ItemReward.TypeId).Quantity);
                Assert.Single(
                    world.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id && p.Kind == WalletEntryType.AgentMissionReward)
                );
                return true;
            });
    }

    [Fact]
    public async Task ImplantAllocationFailureRollsBackOreCurrencyStandingAndClosureAcrossCheckpoint()
    {
        using var f = Create();
        var offer = Offer(f);
        f.Dock(MissionFixture.StationId);
        f.Accept(offer);
        AddItem(f, offer.Retrieval!.TypeId, offer.Retrieval.Quantity);
        long balance = Live(f).WalletBalance(MissionFixture.PilotId);
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Next("WorldItem", long.MaxValue - 1);
            action.Commit();
        }
        var before = Live(f)
            .Economy.Inspect(world =>
                world
                    .Set<Eden.Data.World.Characters.SavedNpcStanding>()
                    .Where(s => s.PilotId == MissionFixture.PilotId)
                    .OrderBy(s => s.SourceId)
                    .Select(s => s.Value)
                    .ToArray()
            );
        Assert.Throws<OverflowException>(() => Complete(f, offer));
        Assert.Equal(balance, Live(f).WalletBalance(MissionFixture.PilotId));
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.Equal(offer.Retrieval.Quantity, world.Set<OwnedItem>().Single(i => i.TypeId == offer.Retrieval.TypeId).Quantity);
                Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == offer.ItemReward!.TypeId));
                Assert.Empty(world.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id));
                Assert.Equal(
                    before,
                    world
                        .Set<Eden.Data.World.Characters.SavedNpcStanding>()
                        .Where(s => s.PilotId == MissionFixture.PilotId)
                        .OrderBy(s => s.SourceId)
                        .Select(s => s.Value)
                );
                return true;
            });
        await Live(f).FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var saved = f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!;
        Assert.Equal(MissionState.Accepted, saved.State);
        Assert.Equal(offer.ItemReward, saved.ItemReward);
        Assert.True(saved.ObjectiveComplete);
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("quit")]
    [InlineData("offer-expiry")]
    [InlineData("accepted-expiry")]
    public void ClosingWithoutTurnInNeverGrantsTheQuotedImplant(string closure)
    {
        using var f = Create();
        var offer = Offer(f);
        f.Dock(MissionFixture.StationId);
        if (closure is "quit" or "accepted-expiry")
        {
            offer = f.Accept(offer);
            AddItem(f, offer.Retrieval!.TypeId, offer.Retrieval.Quantity);
        }
        long balance = Live(f).WalletBalance(MissionFixture.PilotId);
        if (closure.EndsWith("expiry", StringComparison.Ordinal))
        {
            f.Clock.Seconds += (offer.ExpiresAt - f.Clock.Now) / TimeSpan.TicksPerSecond;
            Assert.Equal(MissionState.Failed, f.Missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.View).Offer!.State);
        }
        else
        {
            f.Missions.Act(
                MissionFixture.PilotId,
                offer.Agent.Id,
                closure == "quit" ? MissionAction.Quit : MissionAction.Decline,
                offer.Id
            );
        }
        Assert.Equal(balance, Live(f).WalletBalance(MissionFixture.PilotId));
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == offer.ItemReward!.TypeId));
                Assert.Empty(world.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id));
                return true;
            });
    }

    [Fact]
    public async Task UpgradePreservesLegacyOffersWithoutInventingItemRewards()
    {
        using MissionFixture f = new(live: true);
        var offer = f.Request();
        await Live(f).FlushAsync();
        f.StopResident();
        using (WorldDbContext old = new(f.Db.Path))
        {
            old.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldMission DROP COLUMN ItemRewardTypeId;
                ALTER TABLE WorldMission DROP COLUMN ItemRewardQuantity;
                PRAGMA user_version=168;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path));
        WorldInitialization.Initialize(new(f.Db.Path));
        using (var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true))
        {
            var saved = reopened.GetRequiredService<IPilotMissions>().Current(MissionFixture.PilotId, offer.Agent.Id)!;
            Assert.Equal(offer.Id, saved.Id);
            Assert.Null(saved.ItemReward);
            Assert.Equal(offer.Rewards, saved.Rewards);
        }
        using var twice = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        Assert.Null(twice.GetRequiredService<IPilotMissions>().Current(MissionFixture.PilotId, offer.Agent.Id)!.ItemReward);
    }

    [Fact]
    public void LateTradeCompletionKeepsTheImplantAndBaseRewardAfterTheIskBonusExpires()
    {
        using var f = Create();
        var offer = Offer(f);
        f.Dock(MissionFixture.StationId);
        f.Accept(offer);
        AddItem(f, offer.Retrieval!.TypeId, offer.Retrieval.Quantity);
        long balance = Live(f).WalletBalance(MissionFixture.PilotId);
        f.Clock.Seconds += MissionBonusRules.Window.Ticks / TimeSpan.TicksPerSecond;
        Complete(f, offer);
        Assert.Equal(balance + offer.Rewards.IskCents, Live(f).WalletBalance(MissionFixture.PilotId));
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.Single(world.Set<OwnedItem>().Where(i => i.TypeId == offer.ItemReward!.TypeId));
                Assert.DoesNotContain(
                    world.Set<SavedMarketPayment>(),
                    p => p.OrderId == offer.Id && p.Kind == WalletEntryType.AgentMissionTimeBonusReward
                );
                return true;
            });
    }

    [Fact]
    public void TradeCompletionRequiresTheAgentStationEvenWhenOreIsAlreadyEligible()
    {
        using var f = Create();
        var offer = Offer(f);
        f.Dock(MissionFixture.StationId);
        f.Accept(offer);
        AddItem(f, offer.Retrieval!.TypeId, offer.Retrieval.Quantity);
        f.Dock(MissionFixture.DestinationId);
        Assert.True(f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.ObjectiveComplete);
        Assert.Throws<InvalidDataException>(() => Complete(f, offer));
        Assert.Equal(MissionState.Accepted, f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.State);
    }

    [Fact]
    public void MissingSavedItemRewardQuoteRejectsAcceptanceWithoutRerollingOrIssuingItems()
    {
        using var f = Create();
        var offer = Offer(f);
        f.Dock(MissionFixture.StationId);
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var row = action.Set<SavedMission>().Find(offer.Id)!;
            row.ItemRewardTypeId = 0;
            row.ItemRewardQuantity = 0;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => f.Accept(offer));
        var unchanged = f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!;
        Assert.Equal(MissionState.Offered, unchanged.State);
        Assert.Null(unchanged.ItemReward);
    }
}
