using System.Globalization;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Market;
using Eden.Protocol.Agents;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionDeadlineTests
{
    // Build 3662168: A Change in Management (two variants), Medical Delivery,
    // Wee Bug Problem, and the existing seven-day courier baseline.
    public static TheoryData<int, int, int, long> Deadlines
    {
        get
        {
            TheoryData<int, int, int, long> cases = new();
            foreach (
                var (id, level, minutes) in new[] { (2723, 1, 120), (2742, 4, 240), (13771, 1, 480), (13773, 2, 720), (2912, 1, 10080) }
            )
            {
                foreach (long offset in new long[] { -1, 0, 1 })
                {
                    cases.Add(id, level, minutes, offset);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Deadlines))]
    public async Task ResidentCatalogDeadlineSurvivesCheckpointsAndGatesTurnIn(int id, int level, int minutes, long offset)
    {
        using var f = Create(id, level);
        var offer = f.Request();
        long duration = TimeSpan.FromMinutes(minutes).Ticks;
        Assert.Equal(id, offer.MissionId);
        Assert.Equal(f.Clock.Now + duration, offer.ExpiresAt);
        var native = BlueRead.Fields(MissionFixture.Reply(f.Call($"@agent:{offer.Agent.Id}", "GetMissionJournalInfo", [null, null])));
        Assert.Equal(offer.ExpiresAt, native["expirationTime"]);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(duration / 2));
        MissionOffer accepted;
        using (var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true))
        {
            var missions = reopened.GetRequiredService<IPilotMissions>();
            Assert.Equal(offer.ExpiresAt, missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.ExpiresAt);
            accepted = missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Accept, offer.Id).Offer!;
            Assert.Equal(f.Clock.Now + duration, accepted.ExpiresAt);
            Assert.Equal(accepted.ExpiresAt, BlueRead.Fields(new MissionJournalInfo(accepted, f.Clock.Now).ToBlue())["expirationTime"]);
            var live = LiveWorld.Find(f.Db.Path)!;
            using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
            {
                action.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo!.TypeId).LocationId = offer.Cargo!.DestinationStation;
                action.Pilots.Find(MissionFixture.PilotId)!.ActiveShip.StationId = offer.Cargo.DestinationStation;
                action.Commit();
            }
            await live.FlushAsync();
        }
        using (var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true))
        {
            var missions = reopened.GetRequiredService<IPilotMissions>();
            Assert.Equal(accepted.ExpiresAt, missions.Current(MissionFixture.PilotId, offer.Agent.Id)!.ExpiresAt);
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(duration + offset));
            var result = missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id);
            Assert.Equal(offset < 0 ? MissionState.Completed : MissionState.Failed, result.Offer!.State);
            Assert.Equal(offset < 0 ? "completed" : "failed", result.Event);
            if (offset >= 0)
            {
                Assert.Empty(result.Items);
                Assert.Null(result.BalanceCents);
            }
            Assert.Throws<InvalidDataException>(() =>
                missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id)
            );
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        }
        using WorldDbContext saved = new(f.Db.Path);
        var row = saved.Set<SavedMission>().Find(offer.Id)!;
        Assert.Equal(accepted.ExpiresAt, row.ExpiresAt);
        Assert.Equal(f.Clock.Now, row.ClosedAt);
        Assert.Equal(offset < 0 ? MissionState.Completed : MissionState.Failed, row.State);
        var payments = saved.Set<SavedMarketPayment>().Where(p => p.OrderId == offer.Id).ToArray();
        long expected = offset < 0 ? offer.Rewards.IskCents + accepted.Bonus!.Payable(f.Clock.Now) : 0;
        Assert.Equal(expected, payments.Sum(p => p.AmountCents));
        Assert.Equal(offset < 0 ? 1 : 0, payments.Count(p => p.Kind == WalletEntryType.AgentMissionReward));
        Assert.Equal(
            offset < 0 ? 0 : offer.Cargo!.Quantity,
            saved.Set<OwnedItem>().Where(i => i.TypeId == offer.Cargo!.TypeId).Sum(i => i.Quantity)
        );
    }

    [Theory]
    [MemberData(nameof(Deadlines))]
    public void OfferAcceptanceHonorsExactCatalogDeadline(int id, int level, int minutes, long offset)
    {
        using var f = Create(id, level);
        var offer = f.Request();
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(minutes) + TimeSpan.FromTicks(offset));
        var update = f.Missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Accept, offer.Id);
        Assert.Equal(offset < 0 ? MissionState.Accepted : MissionState.Failed, update.Offer!.State);
        Assert.Equal(offset < 0 ? "accepted" : "failed", update.Event);
        if (offset >= 0)
        {
            Assert.Null(update.Offer.AcceptedAt);
            Assert.Empty(update.Items);
            LiveWorld
                .Find(f.Db.Path)!
                .Economy.Inspect(world =>
                {
                    Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == offer.Cargo!.TypeId));
                    Assert.Empty(world.Set<SavedMarketPayment>());
                    return true;
                });
        }
    }

    [Theory]
    [InlineData(null, 10080)]
    [InlineData(1L, 1L)]
    [InlineData(120L, 120L)]
    [InlineData(10080L, 10080L)]
    public void LifetimeUsesCatalogMinutesOrSevenDayDefault(long? minutes, long expected)
    {
        Assert.Equal(TimeSpan.FromMinutes(expected), MissionRules.Lifetime(minutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10081)]
    [InlineData(long.MaxValue)]
    public void UnsupportedLifecyclesAreRejected(long minutes)
    {
        Assert.Throws<InvalidDataException>(() => MissionRules.Lifetime(minutes));
    }

    private static MissionFixture Create(int id, int level)
    {
        var fixture = new MissionFixture(
            level: level,
            live: true,
            configure: catalog =>
            {
                string key = id.ToString(CultureInfo.InvariantCulture);
                catalog.Database.ExecuteSql($"DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key<>{key}");
            }
        );
        using var action = LiveWorld.Find(fixture.Db.Path)!.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        action.Add(
            new SavedNpcStanding
            {
                PilotId = MissionFixture.PilotId,
                SourceId = MissionFixture.AgentId,
                Value = 8,
            }
        );
        action.Commit();
        return fixture;
    }
}
