using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HabitatDefinitionsRejectNonDestructibleGroupsAndMissingHullHealth(bool missingHealth)
    {
        using MissionFixture f = new();
        string path = new WorldDatabase(f.Db.Path).CatalogPath;
        using (CatalogDbContext catalog = new(path, writable: true))
        {
            if (missingHealth)
            {
                catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=19715 AND AttributeId=9");
            }
            else
            {
                catalog.Database.ExecuteSqlRaw("UPDATE ItemType SET GroupId=226 WHERE Id=19715");
            }
        }
        using CatalogDbContext check = new(path);
        Assert.Throws<InvalidDataException>(() => MissionCatalog.Find(check, 1180));
    }

    [Theory]
    [InlineData(2470)]
    [InlineData(2473)]
    public void ExpandedScoreGatesRequireFirstRoomClearanceAndFinalTargetsSurviveRestart(int id)
    {
        using var f = Create(id, 3, MissionRules.SecurityDivision);
        var mission = f.Accept(f.Request());
        var sites = f.Missions.Sites(mission.SystemId);
        var first = sites.Single(s => s.Gates.Any(g => g.RequiresClear));
        var gate = Assert.Single(first.Gates);
        var last = sites.Single(s => s.Position == gate.Destination);
        Assert.False(first.Started);
        Assert.False(last.Started);
        Move(f, mission.SystemId, gate.Position);
        Assert.Throws<InvalidDataException>(() => f.Missions.Gate(MissionFixture.PilotId, gate.Id));
        f.Missions.Arrive(mission.SystemId, first.Position);
        long[] targets;
        using (WorldDbContext world = new(f.Db.Path))
        {
            targets = world.Set<SavedMissionMember>().Where(m => m.Required).OrderBy(m => m.NpcId).Select(m => m.NpcId).ToArray();
        }
        foreach (long target in targets[..^1])
        {
            f.Kill(target);
        }
        Move(f, mission.SystemId, gate.Position);
        Assert.Throws<InvalidDataException>(() => f.Missions.Gate(MissionFixture.PilotId, gate.Id));
        f.Kill(targets[^1], commit: false);
        Assert.Throws<InvalidDataException>(() => f.Missions.Gate(MissionFixture.PilotId, gate.Id));
        f.Kill(targets[^1]);
        Assert.Equal(gate.Id, f.Missions.Gate(MissionFixture.PilotId, gate.Id).Id);
        Assert.False(Current(f).ObjectiveComplete);
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.False(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        Assert.Equal(gate.Id, missions.Gate(MissionFixture.PilotId, gate.Id).Id);
        missions.Arrive(mission.SystemId, last.Position);
        missions.Arrive(mission.SystemId, last.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            targets = world.Set<SavedMissionMember>().Where(m => m.Required && m.Wave == 1).Select(m => m.NpcId).ToArray();
            Assert.NotEmpty(targets);
        }
        foreach (long target in targets)
        {
            f.Kill(target);
        }
        Assert.True(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        using WorldDbContext check = new(f.Db.Path);
        Assert.True(check.Set<WorldNpc>().Any(n => n.DestroyedAt == null && n.RetiredAt == null));
    }

    [Theory]
    [InlineData(1180, 1)]
    [InlineData(1181, 1)]
    [InlineData(1177, 2)]
    [InlineData(1179, 2)]
    [InlineData(1199, 2)]
    public void HabitatObjectivesRejectEarlyTurnInAndRollbackThenPersistDestruction(int id, int level)
    {
        using var f = Create(id, level, MissionRules.SecurityDivision);
        var mission = f.Accept(f.Request());
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id)
        );
        long target;
        using (WorldDbContext world = new(f.Db.Path))
        {
            target = world.Set<SavedMissionStructure>().Single(s => s.Required).NpcId;
        }
        f.Kill(target, commit: false);
        Assert.False(Current(f).ObjectiveComplete);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Null(world.Set<SavedMissionStructure>().Find(target)!.DestroyedAt);
        }
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.False(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        f.Kill(target);
        Assert.True(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
    }
}
