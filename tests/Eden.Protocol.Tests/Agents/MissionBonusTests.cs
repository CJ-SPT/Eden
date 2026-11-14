using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Market;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionBonusTests
{
    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public void BonusRequiresAcceptanceAndTurnInStrictlyBeforeDeadline(long offset, long expected)
    {
        MissionBonus offered = new(100, TimeSpan.TicksPerHour, null);
        Assert.Equal(TimeSpan.TicksPerHour, offered.Remaining(long.MaxValue));
        Assert.Equal(0, offered.Payable(0));
        var accepted = offered with { Deadline = TimeSpan.TicksPerDay };
        Assert.Equal(expected, accepted.Payable(TimeSpan.TicksPerDay + offset));
        Assert.Equal(Math.Max(0, -offset), accepted.Remaining(TimeSpan.TicksPerDay + offset));
        Assert.Equal(100, MissionBonusRules.Quote(201));
        Assert.Equal(0, MissionBonusRules.Quote(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ResidentBonusSurvivesCheckpointAndSettlesAtTheExactDeadline(long offset)
    {
        using MissionFixture f = new(live: true);
        var offer = f.Request();
        Assert.Equal(offer.Rewards.IskCents / 2, offer.Bonus!.IskCents);
        Assert.Null(offer.Bonus.Deadline);
        AssertBonusWire(f, offer, MissionBonusRules.Window.Ticks);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Equal(offer.Bonus, f.Request().Bonus);
        var accepted = f.Accept(offer);
        Assert.Equal(f.Clock.Now + MissionBonusRules.Window.Ticks, accepted.Bonus!.Deadline);
        AssertBonusWire(f, accepted, MissionBonusRules.Window.Ticks);
        f.Clock.AdvanceWallTime(MissionBonusRules.Window + TimeSpan.FromTicks(offset));
        AssertBonusWire(f, accepted, Math.Max(0, -offset));
        var live = LiveWorld.Find(f.Db.Path)!;
        await live.FlushAsync();
        f.StopResident();
        using (var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true))
        {
            live = LiveWorld.Find(f.Db.Path)!;
            var missions = reopened.GetRequiredService<IPilotMissions>();
            Assert.Equal(accepted.Bonus, missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.Bonus);
            Assert.Throws<InvalidDataException>(() =>
                missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id)
            );
            Assert.Equal(0, live.WalletBalance(MissionFixture.PilotId));
            Assert.Equal(MissionState.Accepted, missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.State);
            Deliver(live, accepted);
            var paid = missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id);
            long bonus = offset < 0 ? offer.Bonus.IskCents : 0;
            Assert.Equal(offer.Rewards.IskCents + bonus, paid.BalanceCents);
            Assert.Equal(offer.Rewards.LoyaltyPoints, paid.PointsAfter);
            Assert.Throws<InvalidDataException>(() =>
                missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id)
            );
            await live.FlushAsync();
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(MissionState.Completed, saved.Set<SavedMission>().Find(offer.Id)!.State);
        var payments = saved.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id).OrderBy(p => p.Id).ToArray();
        Assert.Equal(offset < 0 ? 2 : 1, payments.Length);
        Assert.Equal(WalletEntryType.AgentMissionReward, payments[0].Kind);
        Assert.Equal(offer.Rewards.IskCents, payments[0].AmountCents);
        if (offset < 0)
        {
            Assert.Equal(WalletEntryType.AgentMissionTimeBonusReward, payments[1].Kind);
            Assert.Equal(offer.Bonus.IskCents, payments[1].AmountCents);
            Assert.Equal(offer.Rewards.IskCents + offer.Bonus.IskCents, payments[1].BalanceCents);
        }
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.TypeId == offer.Cargo!.TypeId));
    }

    [Fact]
    public void BonusOverflowRollsBackBasePaymentCargoAndMissionClosure()
    {
        using MissionFixture f = new(live: true);
        var offer = f.Accept(f.Request());
        var live = LiveWorld.Find(f.Db.Path)!;
        Deliver(live, offer);
        long starting = long.MaxValue - offer.Rewards.IskCents;
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(new PilotWallet { PilotId = MissionFixture.PilotId, Cents = starting });
            action.Commit();
        }
        Assert.Throws<OverflowException>(() => f.Missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id));
        Assert.Equal(starting, live.WalletBalance(MissionFixture.PilotId));
        var unchanged = f.Missions.Current(MissionFixture.PilotId, offer.Agent.Id)!;
        Assert.Equal(MissionState.Accepted, unchanged.State);
        Assert.True(unchanged.ObjectiveComplete);
        live.Economy.Inspect(world =>
        {
            Assert.Empty(world.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id));
            Assert.Equal(offer.Cargo!.Quantity, world.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo.TypeId).Quantity);
            return true;
        });
    }

    [Fact]
    public async Task UpgradePreservesLegacyOfferWithoutInventingABonus()
    {
        using MissionFixture f = new(live: true);
        var offer = f.Request();
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldMission DROP COLUMN BonusIskCents;
                ALTER TABLE WorldMission DROP COLUMN BonusWindowTicks;
                PRAGMA user_version=157;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path));
        WorldInitialization.Initialize(new(f.Db.Path));
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        var retained = missions.Current(MissionFixture.PilotId, offer.Agent.Id)!;
        Assert.Null(retained.Bonus);
        Assert.Equal(offer.Rewards, retained.Rewards);
        var accepted = missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Accept, offer.Id).Offer!;
        Assert.Null(accepted.Bonus);
        Deliver(LiveWorld.Find(f.Db.Path)!, accepted);
        var paid = missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id);
        Assert.Equal(offer.Rewards.IskCents, paid.BalanceCents);
    }

    private static void Deliver(LiveWorld live, MissionOffer offer)
    {
        using var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        action.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo!.TypeId).LocationId = MissionFixture.DestinationId;
        action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = MissionFixture.DestinationId;
        action.Commit();
    }

    private static void AssertBonusWire(MissionFixture f, MissionOffer offer, long remaining)
    {
        string service = $"@agent:{offer.Agent.Id}";
        var objectives = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionObjectiveInfo", [])));
        var entry = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objectives["bonusRewards"])));
        Assert.Equal(5, entry.Length);
        Assert.Equal(remaining, entry[0]);
        Assert.Equal(29L, entry[1]);
        Assert.Equal(offer.Bonus!.IskCents / (double)MarketDefinitions.CentsPerIsk, entry[2]);
        Assert.Null(entry[3]);
        Assert.Equal(360L, entry[4]);
        var journal = BlueRead.Fields(MissionFixture.Reply(f.Call(service, "GetMissionJournalInfo", [null, null])));
        var nested = BlueRead.Fields(journal["objectives"]);
        var journalEntry = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(nested["bonusRewards"])));
        Assert.Equal(entry, journalEntry);
    }
}
