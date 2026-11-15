using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionChainTests
{
    public static TheoryData<int, int, int> CourierChains =>
        new()
        {
            { 4838, 4839, 4840 },
            { 4841, 4842, 4843 },
            { 4844, 4845, 4846 },
            { 4847, 4849, 4853 },
        };

    [Theory]
    [MemberData(nameof(CourierChains))]
    public async Task EveryCourierChainOffersOnlyItsRootAndAdvancesOnceAcrossCheckpoint(int first, int second, int third)
    {
        using var f = Create(first, second, third);
        ServiceProvider? reopened = null;
        try
        {
            var offer = f.Request();
            Assert.Equal(first, offer.MissionId);
            long expectedPayment = 0;
            HashSet<int> cargoTypes = [];
            int[] parts = [first, second, third];
            for (int index = 0; index < parts.Length; index++)
            {
                Assert.Equal(parts[index], offer.MissionId);
                f.Dock(MissionFixture.StationId);
                f.Accept(offer);
                cargoTypes.Add(offer.Cargo!.TypeId);
                f.Dock(offer.Cargo!.DestinationStation);
                Assert.Throws<InvalidDataException>(() => Complete(f, offer));
                Deliver(f, offer);
                var paid = Complete(f, offer);
                Assert.Equal(MissionState.Completed, paid.Offer!.State);
                Assert.Equal(offer.Id, paid.Offer.Id);
                expectedPayment += offer.Rewards.IskCents + offer.Bonus!.IskCents;
                Assert.Equal(expectedPayment, Live(f).WalletBalance(MissionFixture.PilotId));
                Assert.Throws<InvalidDataException>(() => Complete(f, offer));
                if (index == parts.Length - 1)
                {
                    Assert.Null(paid.Continuation);
                    Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
                    break;
                }
                var next = Assert.IsType<MissionOffer>(paid.Continuation);
                Assert.Equal(parts[index + 1], next.MissionId);
                Assert.Equal(MissionState.Offered, next.State);
                Assert.Equal(offer.Agent.Id, next.Agent.Id);
                Assert.Equal(next.Id, Assert.Single(f.Missions.Journal(MissionFixture.PilotId)).Id);
                Assert.Equal(next.Id, f.Request().Id);
                if (index == 0)
                {
                    await Live(f).FlushAsync();
                    f.StopResident();
                    reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
                    var saved = f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;
                    Assert.Equal(next.Id, saved.Id);
                    Assert.Equal(next.Rewards, saved.Rewards);
                    Assert.Equal(next.ExpiresAt, saved.ExpiresAt);
                    Assert.Equal(MissionState.Offered, saved.State);
                }
                offer = next;
            }
            Live(f)
                .Economy.Inspect(world =>
                {
                    Assert.Equal(3, world.Set<SavedMission>().Count(m => m.State == MissionState.Completed));
                    Assert.Empty(world.Set<SavedMission>().Where(m => m.ClosedAt == null));
                    Assert.Equal(3, world.Set<SavedMarketPayment>().Count(p => p.Kind == WalletEntryType.AgentMissionReward));
                    Assert.Equal(3, Assert.Single(world.Set<SavedMissionProgress>()).Completed);
                    Assert.Empty(world.Set<OwnedItem>().Where(i => cargoTypes.Contains(i.TypeId)));
                    return true;
                });
            f.Dock(MissionFixture.StationId);
            Assert.Equal(first, f.Request().MissionId);
        }
        finally
        {
            reopened?.Dispose();
        }
    }

    [Fact]
    public void NativeCompletionPublishesTheNextOfferAfterCompletionAndRejectsTheOldToken()
    {
        using var f = Create(4838, 4839, 4840);
        var offer = f.Accept(f.Request());
        Deliver(f, offer);
        string service = $"@agent:{MissionFixture.AgentId}";
        long token = offer.Id * MissionRules.ActionStride + (int)MissionAction.Complete;
        var conversation = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call(service, "DoAction", [token])));
        Assert.Equal(true, BlueRead.Fields(conversation[1])["missionCompleted"]);
        Assert.Equal(new[] { "completed", "offered" }, f.Context.Notifications.OfType<AgentMissionChanged>().Select(n => n.Action));
        var next = f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;
        Assert.Equal(4839, next.MissionId);
        int notifications = f.Context.Notifications.Count;
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, "DoAction", [token]));
        Assert.Equal(notifications, f.Context.Notifications.Count);
        var response = Assert.IsType<object?[]>(MissionFixture.Reply(f.Call(service, "DoAction", [(long)MissionAction.Request])));
        var dialogue = Assert.IsType<object?[]>(response[0]);
        Assert.Equal(4839L, Assert.IsType<object?[]>(dialogue[0])[1]);
        var actions = Assert.IsType<List<object?>>(dialogue[1]);
        var accept = Assert.IsType<object?[]>(actions[0]);
        Assert.Equal(next.Id * MissionRules.ActionStride + (int)MissionAction.Accept, accept[0]);
    }

    [Fact]
    public async Task ConcurrentTurnInsCreateOneContinuationAndFailedPayoutPreservesCargoAndSequence()
    {
        using var f = Create(4838, 4839, 4840);
        var offer = f.Accept(f.Request());
        Deliver(f, offer);
        long starting = long.MaxValue - offer.Rewards.IskCents;
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(new PilotWallet { PilotId = MissionFixture.PilotId, Cents = starting });
            action.Commit();
        }
        Assert.Throws<OverflowException>(() => Complete(f, offer));
        Assert.Equal(starting, Live(f).WalletBalance(MissionFixture.PilotId));
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.Equal(offer.Id, Assert.Single(world.Set<SavedMission>()).Id);
                Assert.Equal(MissionState.Accepted, world.Set<SavedMission>().Find(offer.Id)!.State);
                Assert.Empty(world.Set<SavedMarketPayment>());
                Assert.Empty(world.Set<SavedMissionProgress>());
                Assert.Single(world.Set<OwnedItem>().Where(i => i.TypeId == offer.Cargo!.TypeId));
                return true;
            });
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Set<PilotWallet>().Find(MissionFixture.PilotId)!.Cents = 0;
            action.Commit();
        }
        var outcomes = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return Complete(f, offer).Continuation?.Id;
                        }
                        catch (InvalidDataException)
                        {
                            return null;
                        }
                    })
                )
        );
        long nextId = Assert.Single(outcomes.OfType<long>());
        Assert.Equal(nextId, Assert.Single(f.Missions.Journal(MissionFixture.PilotId)).Id);
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.Equal(2, world.Set<SavedMission>().Count());
                Assert.Single(world.Set<SavedMarketPayment>().Where(p => p.Kind == WalletEntryType.AgentMissionReward));
                return true;
            });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DecliningQuittingOrExpiringAContinuationReturnsToTheRootPool(bool accepted, bool expired)
    {
        using var f = Create(4838, 4839, 4840);
        var first = f.Accept(f.Request());
        Deliver(f, first);
        var next = Complete(f, first).Continuation!;
        f.Dock(MissionFixture.StationId);
        if (accepted)
        {
            next = f.Accept(next);
        }
        if (expired)
        {
            f.Clock.Seconds += (next.ExpiresAt - f.Clock.Now) / TimeSpan.TicksPerSecond;
            Assert.Equal(MissionState.Failed, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.State);
        }
        else
        {
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, accepted ? MissionAction.Quit : MissionAction.Decline, next.Id);
        }
        var restarted = f.Request();
        Assert.Equal(4838, restarted.MissionId);
        Assert.NotEqual(next.Id, restarted.Id);
        await Live(f).FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var saved = Assert.Single(reopened.GetRequiredService<IPilotMissions>().Journal(MissionFixture.PilotId));
        Assert.Equal(restarted.Id, saved.Id);
        Assert.Equal(4838, saved.MissionId);
    }

    [Fact]
    public async Task ContinuationAllocationFailureRollsBackClosureRewardsAndCargoAcrossCheckpoint()
    {
        using var f = Create(4838, 4839, 4840);
        var offer = f.Accept(f.Request());
        Deliver(f, offer);
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Next("WorldMission", long.MaxValue - 1);
            action.Commit();
        }
        Assert.Throws<OverflowException>(() => Complete(f, offer));
        Assert.Equal(0, Live(f).WalletBalance(MissionFixture.PilotId));
        Assert.Equal(offer.Id, Assert.Single(f.Missions.Journal(MissionFixture.PilotId)).Id);
        Assert.Equal(MissionState.Accepted, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.State);
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.Empty(world.Set<SavedMarketPayment>());
                Assert.Empty(world.Set<SavedMissionProgress>());
                Assert.Single(world.Set<OwnedItem>().Where(i => i.TypeId == offer.Cargo!.TypeId));
                return true;
            });
        await Live(f).FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var saved = Assert.Single(reopened.GetRequiredService<IPilotMissions>().Journal(MissionFixture.PilotId));
        Assert.Equal(offer.Id, saved.Id);
        Assert.Equal(MissionState.Accepted, saved.State);
        Assert.True(saved.ObjectiveComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrIneligibleLaterPartsPreventTheChainRootFromBeingOffered(bool ineligible)
    {
        using var f = Create(
            4838,
            4839,
            4840,
            catalog =>
            {
                if (ineligible)
                {
                    catalog.Database.ExecuteSqlRaw(
                        "UPDATE SourceRecord SET Data=json_set(Data,'$.factionID',500003) WHERE Dataset='client/missions' AND Key='4839'"
                    );
                }
                else
                {
                    catalog.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key='4839'");
                }
            }
        );
        Assert.Throws<InvalidDataException>(() => f.Request());
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
    }

    [Fact]
    public void AForeignOrUnfinishedPredecessorCannotAuthorizeASavedLaterPart()
    {
        using var f = Create(4838, 4839, 4840);
        var offer = f.Request();
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Set<SavedMission>().Find(offer.Id)!.MissionId = 4839;
            action.Add(
                new SavedMission
                {
                    PilotId = MissionFixture.PilotId + 1,
                    AgentId = MissionFixture.AgentId,
                    MissionId = 4838,
                    State = MissionState.Completed,
                    ClosedAt = f.Clock.Now,
                }
            );
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => f.Accept(offer));
        Live(f)
            .Economy.Inspect(world =>
            {
                Assert.Equal(MissionState.Offered, world.Set<SavedMission>().Find(offer.Id)!.State);
                Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == 28972));
                return true;
            });
    }

    private static MissionFixture Create(int first, int second, int third, Action<CatalogDbContext>? configure = null) =>
        new(
            live: true,
            configure: catalog =>
            {
                var source = catalog.Require(CatalogData.Client.Missions, (long)first);
                long corporation = catalog.Require(CatalogData.Client.Factions, source.FactionID!.Value).CorporationID!.Value;
                string agentKey = MissionFixture.AgentId.ToString(CultureInfo.InvariantCulture);
                catalog.Database.ExecuteSql(
                    $"""
                    UPDATE SourceRecord SET Data=json_set(Data,'$.corporationID',{corporation})
                    WHERE Dataset='client/npccharacters' AND Key={agentKey}
                    """
                );
                string firstKey = first.ToString(CultureInfo.InvariantCulture);
                string secondKey = second.ToString(CultureInfo.InvariantCulture);
                string thirdKey = third.ToString(CultureInfo.InvariantCulture);
                catalog.Database.ExecuteSql(
                    $"""
                    DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key NOT IN ({firstKey},{secondKey},{thirdKey})
                    """
                );
                configure?.Invoke(catalog);
            }
        );

    private static LiveWorld Live(MissionFixture f) => LiveWorld.Find(f.Db.Path)!;

    private static MissionUpdate Complete(MissionFixture f, MissionOffer offer) =>
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id);

    private static void Deliver(MissionFixture f, MissionOffer offer)
    {
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var cargo = action.Set<OwnedItem>().Single(i => i.OwnerId == MissionFixture.PilotId && i.TypeId == offer.Cargo!.TypeId);
            cargo.LocationId = offer.Cargo!.DestinationStation;
            cargo.FlagId = (int)InventoryFlag.Hangar;
            action.Commit();
        }
        f.Dock(offer.Cargo!.DestinationStation);
    }
}
