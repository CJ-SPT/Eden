using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Protocol.Agents;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(1186, 17381, 593786, 17, false)]
    [InlineData(1186, 17381, 593786, 17, true)]
    [InlineData(1187, 17382, 60753, 16, false)]
    [InlineData(1187, 17382, 60753, 16, true)]
    [InlineData(1188, 16859, 60799, 20, false)]
    [InlineData(1188, 16859, 60799, 20, true)]
    [InlineData(1189, 17381, 593786, 17, false)]
    [InlineData(1189, 17381, 593786, 17, true)]
    [InlineData(1190, 16855, 56336, 21, false)]
    [InlineData(1190, 16855, 56336, 21, true)]
    [InlineData(1191, 16855, 56336, 21, false)]
    [InlineData(1191, 16855, 56336, 21, true)]
    public async Task RetributionRequiresItsOutpostAndPreservesBlitzOrGuardClearAcrossCheckpoint(
        int id,
        int type,
        long objectId,
        int guardCount,
        bool clearGuards
    )
    {
        using var f = Create(id, 3, MissionRules.SecurityDivision, live: true);
        var offer = f.Accept(f.Request());
        Assert.Equal(id, offer.MissionId);
        var sites = f.Missions.Sites(offer.SystemId);
        Assert.Equal(2, sites.Count);
        var entrance = sites.Single(s => s.Position == offer.Position);
        var pocket = sites.Single(s => s.Position != offer.Position);
        Assert.Equal(pocket.Position, Assert.Single(entrance.Gates).Destination);
        f.Missions.Arrive(offer.SystemId, entrance.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedMissionMember>());
        }
        f.Missions.Arrive(offer.SystemId, pocket.Position);
        f.Missions.Arrive(offer.SystemId, pocket.Position);
        long outpost;
        long[] guards;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var structure = Assert.Single(world.Set<SavedMissionStructure>());
            Assert.Equal(objectId, structure.ObjectId);
            Assert.True(structure.Required);
            Assert.True(structure.CompletesMission);
            outpost = structure.NpcId;
            Assert.Equal(type, world.Set<WorldNpc>().Find(outpost)!.TypeId);
            guards = world.Set<SavedMissionMember>().Select(m => m.NpcId).ToArray();
            Assert.Equal(guardCount, guards.Length);
            Assert.All(world.Set<SavedMissionMember>(), m => Assert.False(m.Required));
        }
        var entry = Assert.Single(f.Missions.Tracking(MissionFixture.PilotId));
        var target = Assert.Single(entry.Targets);
        Assert.Equal(outpost, target.ItemId);
        Assert.Equal(type, target.TypeId);
        Move(f, offer.SystemId, pocket.Position);
        var tracker = f.Handler<MissionTrackerHandler>();
        tracker.Advance(f.Context);
        var wire = Assert.IsType<List<object?>>(
            MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [MissionFixture.AgentId]))
        );
        Assert.Equal("Destroy", BlueMarshal.Text(wire[0]));
        if (clearGuards)
        {
            foreach (long guard in guards)
            {
                f.Kill(guard);
            }
        }
        Assert.False(Current(f).ObjectiveComplete);
        f.Dock(MissionFixture.StationId);
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        Assert.Equal(offer.Rewards, Current(f).Rewards);
        f.Kill(outpost, commit: false);
        Assert.False(Current(f).ObjectiveComplete);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Null(world.Set<SavedMissionStructure>().Find(outpost)!.DestroyedAt);
            Assert.Null(world.Set<WorldNpc>().Find(outpost)!.DestroyedAt);
        }
        f.Context.Notifications.Clear();
        f.Kill(outpost);
        Assert.True(Current(f).ObjectiveComplete);
        tracker.Advance(f.Context);
        Assert.Single(f.Context.Notifications.OfType<MissionsUpdated>());
        wire = Assert.IsType<List<object?>>(
            MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [MissionFixture.AgentId]))
        );
        Assert.Equal("AllObjectivesComplete", BlueMarshal.Text(wire[0]));
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.True(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        missions.Arrive(offer.SystemId, pocket.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(guardCount, world.Set<SavedMissionMember>().Count());
            Assert.NotNull(world.Set<SavedMissionStructure>().Find(outpost)!.DestroyedAt);
            Assert.NotNull(world.Set<WorldNpc>().Find(outpost)!.DestroyedAt);
            Assert.Equal(clearGuards ? 0 : guardCount, world.Set<WorldNpc>().Count(n => guards.Contains(n.Id) && n.DestroyedAt == null));
            Assert.Empty(world.Set<OwnedItem>().Where(i => i.OwnerId == MissionFixture.PilotId && i.LocationId == outpost));
        }
        Assert.Equal(
            MissionState.Completed,
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id).Offer!.State
        );
        Assert.Throws<InvalidDataException>(() =>
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        Assert.Empty(missions.Sites(offer.SystemId));
    }
}
