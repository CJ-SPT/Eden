using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class DataCenterMissionTests
{
    [Fact]
    public void ChangedNativeAssignmentCannotUseTheCompiledBindingOrFallBackToAnOrdinaryMission()
    {
        using MissionFixture f = new(
            live: true,
            configure: catalog =>
                catalog.Database.ExecuteSqlRaw(
                    """
                    UPDATE SourceRecord SET Data=json_set(Data,'$.agent.storyLineMissions',json('[1754]'))
                    WHERE Dataset='client/npccharacters' AND Key='3018480'
                    """
                )
        );
        var agent = Directory(f).Agent(FirstAgent)!;
        Assert.Null(agent.Space);
        Assert.False(f.Missions.Access(MissionFixture.PilotId, FirstAgent).Allowed);
        Assert.Throws<InvalidDataException>(() => f.Request(FirstAgent));
        Assert.Empty(Live(f).Economy.Inspect(world => world.Set<SavedMission>().ToArray()));
    }

    [Fact]
    public void SharedCopperDefinitionRemainsOneTimePerAgentRatherThanOneTimePerMissionId()
    {
        using MissionFixture f = new(live: true);
        long? firstOffer = null;
        foreach (long id in new long[] { 3018480, 3018481 })
        {
            var agent = Directory(f).Agent(id)!;
            Prepare(f, agent);
            var offer = f.Accept(f.Request(id));
            Assert.Equal(1752, offer.MissionId);
            Assert.NotEqual(firstOffer, offer.Id);
            AddTags(f, offer, offer.Retrieval!.Quantity);
            Complete(f, offer);
            Assert.Throws<InvalidDataException>(() => f.Request(id));
            firstOffer = offer.Id;
        }
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("quit")]
    [InlineData("offer-expiry")]
    [InlineData("accepted-expiry")]
    public async Task EveryTerminalStatePermanentlyClosesTheAssignmentAcrossCheckpoint(string terminal)
    {
        using MissionFixture f = new(live: true);
        var agent = Directory(f).Agent(FirstAgent)!;
        Prepare(f, agent);
        var offer = f.Request(agent.Id);
        if (terminal is "quit" or "accepted-expiry")
        {
            offer = f.Accept(offer);
        }
        long balance = Live(f).WalletBalance(MissionFixture.PilotId);
        if (terminal.EndsWith("expiry", StringComparison.Ordinal))
        {
            f.Clock.Seconds += (offer.ExpiresAt - f.Clock.Now) / TimeSpan.TicksPerSecond;
            Assert.Equal(MissionState.Failed, f.Missions.Current(MissionFixture.PilotId, agent.Id)!.State);
        }
        else
        {
            f.Missions.Act(MissionFixture.PilotId, agent.Id, terminal == "quit" ? MissionAction.Quit : MissionAction.Decline, offer.Id);
        }
        Assert.Equal(balance, Live(f).WalletBalance(MissionFixture.PilotId));
        Assert.Throws<InvalidDataException>(() => f.Request(agent.Id));
        await Live(f).FlushAsync();
        f.StopResident();
        using var reopened = Eden.Server.ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IPilotMissions>(
            reopened
        );
        Assert.False(missions.Access(MissionFixture.PilotId, agent.Id).Allowed);
        Assert.Throws<InvalidDataException>(() => missions.Act(MissionFixture.PilotId, agent.Id, MissionAction.Request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RangeAndSystemChecksCannotConsumeTagsOrPayAwayFromTheAgent(bool wrongSystem)
    {
        using MissionFixture f = new(live: true);
        var agent = Directory(f).Agent(FirstAgent)!;
        Prepare(f, agent);
        var offer = f.Accept(f.Request(agent.Id));
        AddTags(f, offer, offer.Retrieval!.Quantity);
        long balance = Live(f).WalletBalance(MissionFixture.PilotId);
        var point = agent.Space!;
        Move(
            f,
            wrongSystem ? point.SystemId + 1 : point.SystemId,
            wrongSystem ? point.Position : point.Position + new SpacePosition(point.Radius + FixedAgentRules.InteractionRange + 1, 0, 0)
        );
        Assert.Throws<InvalidDataException>(() => Complete(f, offer));
        Assert.Equal(balance, Live(f).WalletBalance(MissionFixture.PilotId));
        Assert.Equal(
            offer.Retrieval.Quantity,
            Live(f).Economy.Inspect(world => world.Set<OwnedItem>().Single(i => i.TypeId == offer.Retrieval.TypeId).Quantity)
        );
        // At these large system coordinates the exact float-radius sum is not representable as a double offset.
        // Pure rules test the exact boundary at zero; persistence tests one centimetre inside it.
        Move(f, point.SystemId, point.Position + new SpacePosition(point.Radius + FixedAgentRules.InteractionRange - 0.01, 0, 0));
        Assert.Equal(MissionState.Completed, Complete(f, offer).Offer!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteStationTagsAndForeignCargoCannotSatisfyAnInSpaceTurnIn(bool foreignOwner)
    {
        using MissionFixture f = new(live: true);
        var agent = Directory(f).Agent(FirstAgent)!;
        Prepare(f, agent);
        var offer = f.Accept(f.Request(agent.Id));
        AddTags(
            f,
            offer,
            offer.Retrieval!.Quantity,
            location: foreignOwner ? null : MissionFixture.StationId,
            owner: foreignOwner ? MissionFixture.PilotId + 1 : null
        );
        Assert.False(f.Missions.Current(MissionFixture.PilotId, agent.Id)!.ObjectiveComplete);
        Assert.Throws<InvalidDataException>(() => Complete(f, offer));
    }

    [Fact]
    public async Task ConcurrentRequestsAndTurnInsProduceOneOfferAndOnePayment()
    {
        using MissionFixture f = new(live: true);
        var agent = Directory(f).Agent(FirstAgent)!;
        Prepare(f, agent);
        var offers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => f.Request(agent.Id))));
        Assert.Single(offers.DistinctBy(offer => offer.Id));
        var offer = f.Accept(offers[0]);
        AddTags(f, offer, offer.Retrieval!.Quantity);
        long balance = Live(f).WalletBalance(MissionFixture.PilotId);
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
        Assert.Single(results, value => value);
        Assert.Equal(balance + offer.Rewards.IskCents + offer.Bonus!.IskCents, Live(f).WalletBalance(MissionFixture.PilotId));
    }

    [Fact]
    public void CrossAgentMissionForgeryAndRewardSharingAreRejectedWithoutSettlement()
    {
        using MissionFixture f = new(live: true);
        var agent = Directory(f).Agent(FirstAgent)!;
        Prepare(f, agent);
        var offer = f.Request(agent.Id);
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Set<SavedMission>().Find(offer.Id)!.MissionId = 1754;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => f.Accept(offer));
        Assert.Throws<InvalidDataException>(() => f.Missions.CompleteWithFleet(MissionFixture.PilotId, agent.Id, offer.Id));
        Assert.Equal(MissionState.Offered, f.Missions.Current(MissionFixture.PilotId, agent.Id)!.State);
    }

    [Fact]
    public void HigherLevelAssignmentsRetainStandingGatesAndDoNotBorrowOtherAgentsContent()
    {
        using MissionFixture f = new(live: true);
        var definition = Content().Missions.First(m => !m.FixedAgentIds.IsEmpty && m.Level == 4);
        var agent = Directory(f).Agent(definition.FixedAgentIds[0])!;
        Move(f, agent.Space!.SystemId, agent.Space.Position);
        Assert.False(f.Missions.Access(MissionFixture.PilotId, agent.Id).Allowed);
        Assert.Throws<InvalidDataException>(() => f.Request(agent.Id));
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = agent.Id,
                    Value = 5,
                }
            );
            action.Commit();
        }
        Assert.Equal(definition.MissionId, f.Request(agent.Id).MissionId);
        Assert.Throws<InvalidDataException>(() => f.Request(3018344));
    }
}
