using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
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
    private const int HabitatType = 19715;

    [Theory]
    [InlineData(319, true)]
    [InlineData(494, true)]
    [InlineData(226, false)]
    [InlineData(227, false)]
    [InlineData(12, false)]
    public void MissionStructuresAdmitDestructibleGroupsAndRejectScenery(int groupId, bool expected) =>
        Assert.Equal(expected, MissionStructureRules.IsDestructibleGroup(groupId));

    [Theory]
    [InlineData(1180, 1, 14)]
    [InlineData(1181, 1, 14)]
    [InlineData(1177, 2, 19)]
    [InlineData(1179, 2, 18)]
    [InlineData(1199, 2, 18)]
    [InlineData(1201, 2, 16)]
    [InlineData(1566, 2, 16)]
    public async Task HabitatCompletionRequiresTheStructureAndSurvivesResidentRollbackAndCheckpoint(int id, int level, int guards)
    {
        using var f = Create(id, level, MissionRules.SecurityDivision, live: true);
        var offer = f.Accept(f.Request());
        var sites = f.Missions.Sites(offer.SystemId);
        Assert.Equal(2, sites.Count);
        foreach (var site in sites)
        {
            f.Missions.Arrive(offer.SystemId, site.Position);
        }
        long habitat;
        long[] enemies;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var structure = Assert.Single(world.Set<SavedMissionStructure>());
            Assert.True(structure.Required);
            Assert.True(structure.CompletesMission);
            habitat = structure.NpcId;
            var npc = world.Set<WorldNpc>().Find(habitat)!;
            Assert.Equal(HabitatType, npc.TypeId);
            Assert.Equal("Habitat", npc.Name);
            enemies = world.Set<SavedMissionMember>().Select(m => m.NpcId).ToArray();
            Assert.Equal(guards, enemies.Length);
            Assert.All(world.Set<SavedMissionMember>(), m => Assert.False(m.Required));
        }
        var entry = Assert.Single(f.Missions.Tracking(MissionFixture.PilotId));
        var target = Assert.Single(entry.Targets);
        Assert.Equal(habitat, target.ItemId);
        Assert.Equal(HabitatType, target.TypeId);
        Move(f, offer.SystemId, entry.Rooms.OrderBy(room => room.DistanceTo(target.Position)).First());
        var tracker = f.Handler<MissionTrackerHandler>();
        tracker.Advance(f.Context);
        var wire = Assert.IsType<List<object?>>(
            MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [MissionFixture.AgentId]))
        );
        Assert.Equal("Destroy", BlueMarshal.Text(wire[0]));
        Assert.Single(f.Context.Notifications.OfType<MissionsUpdated>());
        foreach (long enemy in enemies)
        {
            f.Kill(enemy);
        }
        Assert.False(Current(f).ObjectiveComplete);
        f.Dock(MissionFixture.StationId);
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        Assert.Equal(MissionState.Accepted, Current(f).State);
        f.Kill(habitat, commit: false);
        Assert.False(Current(f).ObjectiveComplete);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Null(world.Set<SavedMissionStructure>().Find(habitat)!.DestroyedAt);
            Assert.Null(world.Set<WorldNpc>().Find(habitat)!.DestroyedAt);
        }
        f.Context.Notifications.Clear();
        f.Kill(habitat);
        tracker.Advance(f.Context);
        Assert.Single(f.Context.Notifications.OfType<MissionsUpdated>());
        wire = Assert.IsType<List<object?>>(
            MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [MissionFixture.AgentId]))
        );
        Assert.Equal("AllObjectivesComplete", BlueMarshal.Text(wire[0]));
        f.Context.Notifications.Clear();
        tracker.Advance(f.Context);
        Assert.Empty(f.Context.Notifications);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        var saved = missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;
        Assert.True(saved.ObjectiveComplete);
        Assert.Equal(offer.Id, saved.Id);
        Assert.Equal(offer.Rewards, saved.Rewards);
        foreach (var site in missions.Sites(saved.SystemId))
        {
            missions.Arrive(saved.SystemId, site.Position);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(guards, world.Set<SavedMissionMember>().Count());
            Assert.NotNull(world.Set<SavedMissionStructure>().Find(habitat)!.DestroyedAt);
            Assert.NotNull(world.Set<WorldNpc>().Find(habitat)!.DestroyedAt);
        }
        Assert.Equal(
            MissionState.Completed,
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id).Offer!.State
        );
        Assert.Throws<InvalidDataException>(() =>
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
    }
}
