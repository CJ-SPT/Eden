using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Agents;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionTrackerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CourierTracksCargoDeliveryClosureAndReopenWithoutDuplicateUpdates(bool resident)
    {
        using MissionFixture f = new(live: resident);
        var accepted = f.Accept(f.Request());
        var tracker = f.Handler<MissionTrackerHandler>();
        tracker.Advance(f.Context);
        Assert.Equal("TransportItemsMissing", BlueMarshal.Text(Current(f)[0]));
        Assert.Single(f.Context.Notifications.OfType<MissionsUpdated>());
        f.Context.Notifications.Clear();
        tracker.Advance(f.Context);
        Assert.Empty(f.Context.Notifications);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var cargo = world.Set<OwnedItem>().Single(i => i.OwnerId == MissionFixture.PilotId && i.TypeId == accepted.Cargo!.TypeId);
            cargo.LocationId = f.Context.Pilot.ShipId;
            cargo.FlagId = (int)InventoryFlag.Cargo;
            world.SaveChanges();
        }
        tracker.Advance(f.Context);
        Assert.Equal("TransportItemsPresent", BlueMarshal.Text(Current(f)[0]));
        Assert.Single(f.Context.Notifications.OfType<MissionsUpdated>());
        f.StopResident();
        tracker = f.Handler<MissionTrackerHandler>();
        Assert.Equal("TransportItemsPresent", BlueMarshal.Text(Current(f)[0]));
        f.Context.Notifications.Clear();
        f.Dock(accepted.Cargo!.DestinationStation);
        tracker.Advance(f.Context);
        Assert.Equal("AllObjectivesComplete", BlueMarshal.Text(Current(f)[0]));
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, SpacePosition.Zero, accepted.SystemId);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        Assert.Equal("TransportItemsPresent", BlueMarshal.Text(Current(f)[0]));
        f.Dock(accepted.Cargo.DestinationStation);
        f.Missions.Act(MissionFixture.PilotId, accepted.Agent.Id, MissionAction.Complete, accepted.Id);
        tracker.Advance(f.Context);
        Assert.Null(MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [accepted.Agent.Id])));
        Assert.False(tracker.IsMonitoring);
        f.StopResident();
        Assert.Empty(f.Missions.Tracking(MissionFixture.PilotId));
    }

    [Fact]
    public void CombatTracksRoomsCompletionAndExactExpiryWithWarmReadCounts()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var accepted = f.Accept(f.Request());
        var tracker = f.Handler<MissionTrackerHandler>();
        tracker.Advance(f.Context);
        Assert.Equal("TravelTo", BlueMarshal.Text(Current(f)[0]));
        var entry = Assert.Single(f.Missions.Tracking(MissionFixture.PilotId));
        var room = entry.Rooms[0];
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, room, accepted.SystemId);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        tracker.Advance(f.Context);
        Assert.Equal("DestroyAll", BlueMarshal.Text(Current(f)[0]));
        Assert.Equal(true, MissionFixture.Reply(f.Call("missionTrackerMgr", "IsInActiveDungeonID", [accepted.Agent.Id])));
        f.Context.Notifications.Clear();
        using (var measured = SessionPerformance.Begin("warm tracker", _ => { }, force: true)!)
        {
            for (int i = 0; i < 4; i++)
            {
                tracker.Advance(f.Context);
            }
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        Assert.Empty(f.Context.Notifications);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedMission>().Single(m => m.Id == accepted.Id).ObjectiveComplete = true;
            world.SaveChanges();
        }
        tracker.Advance(f.Context);
        Assert.Equal("AllObjectivesComplete", BlueMarshal.Text(Current(f)[0]));
        f.Clock.Seconds += (accepted.ExpiresAt - f.Clock.Now) / TimeSpan.TicksPerSecond;
        tracker.Advance(f.Context);
        Assert.Null(MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [accepted.Agent.Id])));
        Assert.False(tracker.IsMonitoring);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(MissionRules.ArrivalRange, true)]
    [InlineData(MissionRules.ArrivalRange + 0.01, false)]
    public void DungeonMembershipUsesActualRoomSystemAndBoundary(double distance, bool inside)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var accepted = f.Accept(f.Request());
        MissionTrackingEntry entry = new(accepted, [SpacePosition.Zero]);
        var position = new SpacePosition(distance, 0, 0);
        Assert.Equal(inside, MissionTrackingRules.Read(entry, accepted.SystemId, null, position).InDungeon);
        Assert.False(MissionTrackingRules.Read(entry, accepted.SystemId + 1, null, position).InDungeon);
        Assert.False(MissionTrackingRules.Read(entry, accepted.SystemId, MissionFixture.StationId, null).InDungeon);
    }

    [Fact]
    public void TrackerRefreshUsesEncodedUpdatesAndDoesNotRepeatOnTheFollowingFrame()
    {
        using MissionFixture f = new();
        var accepted = f.Accept(f.Request());
        Assert.Null(MissionFixture.Reply(f.Call("missionTrackerMgr", "UpdateAllMissions", [new List<object?> { accepted.Agent.Id }])));
        var notification = Assert.Single(f.Context.Notifications.OfType<MissionsUpdated>());
        var arguments = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)));
        var update = BlueRead.Fields(Assert.Single(Assert.IsType<List<object?>>(arguments[0])));
        Assert.Equal(accepted.Agent.Id, update["agentID"]);
        var info = Assert.IsType<List<object?>>(update["info"]);
        Assert.Equal("TransportItemsMissing", BlueMarshal.Text(info[0]));
        Assert.Equal(accepted.Cargo!.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture), BlueMarshal.Text(info[1]));
        f.Context.Notifications.Clear();
        f.Handler<MissionTrackerHandler>().Advance(f.Context);
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void TargetAndRetrievalRulesPreserveCurrentGoalAndNumericHistory()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var accepted = f.Accept(f.Request());
        MissionTrackingEntry entry = new(accepted, [SpacePosition.Zero]) { Targets = [new(123, 456, SpacePosition.Zero)] };
        var state = MissionTrackingRules.Read(entry, accepted.SystemId, null, SpacePosition.Zero);
        Assert.Equal(MissionObjectiveKind.Destroy, state.Current.Kind);
        var encoded = Assert.IsType<object?[]>(MissionFixture.Reply(new MissionTrackerObjectiveReply(state.Current)));
        Assert.Equal("Destroy", BlueMarshal.Text(encoded[0]));
        Assert.Equal(123L, encoded[1]);
        Assert.Equal(456L, encoded[2]);
        Assert.Null(encoded[3]);
        entry = entry with
        {
            Offer = accepted with { CombatComplete = true, ObjectiveComplete = false, Retrieval = new(789, 3, 1, false, 1) },
        };
        var fetch = MissionTrackingRules.Read(entry, accepted.SystemId, null, SpacePosition.Zero);
        Assert.Equal(MissionObjectiveKind.Fetch, fetch.Current.Kind);
        Assert.Equal(789, fetch.Current.TypeId);
        Assert.Equal(3, fetch.Current.Quantity);
        entry = entry with { Offer = entry.Offer with { ObjectiveComplete = true } };
        Assert.Equal(
            MissionObjectiveKind.Complete,
            MissionTrackingRules.Read(entry, accepted.SystemId, null, SpacePosition.Zero).Current.Kind
        );
        Assert.Throws<InvalidDataException>(() =>
            MissionTrackingRules.Read(
                entry with
                {
                    Offer = entry.Offer with { State = MissionState.Offered },
                },
                accepted.SystemId,
                null,
                SpacePosition.Zero
            )
        );
    }

    [Fact]
    public void EmptyTrackerReusesAbsentSnapshotWithoutDatabaseOrFittingWork()
    {
        using MissionFixture f = new(live: true);
        var tracker = f.Handler<MissionTrackerHandler>();
        tracker.Advance(f.Context);
        var live = f.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin())
        {
            action.Add(
                new OwnedItem
                {
                    Id = action.NextItemId(),
                    OwnerId = MissionFixture.PilotId,
                    LocationId = MissionFixture.StationId,
                    TypeId = 34,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                }
            );
            action.Commit();
        }
        using var measured = SessionPerformance.Begin("empty tracker after inventory change", _ => { }, force: true)!;
        for (int i = 0; i < 4; i++)
        {
            tracker.Advance(f.Context);
        }
        Assert.Equal(0, measured.Commands);
        Assert.Equal(0, measured.Connections);
        Assert.Equal(0, measured.FittingCalculations);
        Assert.Empty(f.Context.Notifications);
        Assert.False(tracker.IsMonitoring);
    }

    [Fact]
    public void EmptyTrackerStillExpiresAnUnacceptedOfferAtItsDeadline()
    {
        using MissionFixture f = new(live: true);
        var offer = f.Request();
        Assert.Empty(f.Missions.Tracking(MissionFixture.PilotId));
        f.Clock.UtcNow = DateTimeOffset.FromFileTime(offer.ExpiresAt - 1);
        Assert.Empty(f.Missions.Tracking(MissionFixture.PilotId));
        Assert.Null(ClosedAt());
        f.Clock.UtcNow = DateTimeOffset.FromFileTime(offer.ExpiresAt);
        Assert.Empty(f.Missions.Tracking(MissionFixture.PilotId));
        Assert.Equal(offer.ExpiresAt, ClosedAt());

        long? ClosedAt()
        {
            using WorldDbContext world = new(f.Db.Path);
            return world.Set<SavedMission>().Single(m => m.Id == offer.Id).ClosedAt;
        }
    }

    private static List<object?> Current(MissionFixture f) =>
        Assert.IsType<List<object?>>(MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [MissionFixture.AgentId])));
}
