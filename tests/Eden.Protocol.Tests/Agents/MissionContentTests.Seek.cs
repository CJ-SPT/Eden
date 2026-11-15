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
    [Theory]
    [InlineData(924, 11, 5, false)]
    [InlineData(924, 11, 5, true)]
    [InlineData(927, 9, 9, false)]
    [InlineData(930, 10, 4, false)]
    [InlineData(930, 10, 4, true)]
    [InlineData(936, 7, 7, false)]
    public async Task LevelThreeSeekRequiresItsReportedTargetsAndResumesIncompleteCombatAcrossCheckpoint(
        int id,
        int total,
        int requiredCount,
        bool clearOptional
    )
    {
        using var f = Create(id, 3, MissionRules.SecurityDivision, live: true);
        var offer = f.Accept(f.Request());
        Assert.Equal(id, offer.MissionId);
        Assert.Null(offer.Cargo);
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
        long[] required;
        long[] optional;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var members = world.Set<SavedMissionMember>().OrderBy(m => m.NpcId).ToArray();
            Assert.Equal(total, members.Length);
            required = members.Where(m => m.Required).Select(m => m.NpcId).ToArray();
            optional = members.Where(m => !m.Required).Select(m => m.NpcId).ToArray();
            Assert.Equal(requiredCount, required.Length);
            Assert.All(members, m => Assert.False(m.CompletesMission || m.Trigger));
            Assert.Empty(world.Set<SavedMissionStructure>());
        }
        Move(f, offer.SystemId, pocket.Position);
        var tracker = f.Handler<MissionTrackerHandler>();
        tracker.Advance(f.Context);
        var wire = Assert.IsType<List<object?>>(
            MissionFixture.Reply(f.Call("missionTrackerMgr", "GetMissionInfoItems", [MissionFixture.AgentId]))
        );
        Assert.Equal("DestroyAll", BlueMarshal.Text(wire[0]));
        if (clearOptional)
        {
            Assert.NotEmpty(optional);
            foreach (long guard in optional)
            {
                f.Kill(guard);
            }
        }
        f.Dock(MissionFixture.StationId);
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        foreach (long target in required[..^1])
        {
            f.Kill(target);
            Assert.False(Current(f).ObjectiveComplete);
        }
        f.Kill(required[^1], commit: false);
        Assert.False(Current(f).ObjectiveComplete);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        var saved = missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;
        Assert.False(saved.ObjectiveComplete);
        Assert.Equal(offer.Rewards, saved.Rewards);
        Assert.Throws<InvalidDataException>(() =>
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        missions.Arrive(offer.SystemId, pocket.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(required.Concat(optional).Order(), world.Set<SavedMissionMember>().Select(m => m.NpcId).Order());
            Assert.Null(world.Set<WorldNpc>().Find(required[^1])!.DestroyedAt);
            Assert.All(required[..^1], target => Assert.NotNull(world.Set<WorldNpc>().Find(target)!.DestroyedAt));
        }
        f.Kill(required[^1]);
        Assert.True(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(total, world.Set<SavedMissionMember>().Count());
            Assert.Equal(
                clearOptional ? 0 : optional.Length,
                world.Set<WorldNpc>().Count(n => optional.Contains(n.Id) && n.DestroyedAt == null)
            );
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
