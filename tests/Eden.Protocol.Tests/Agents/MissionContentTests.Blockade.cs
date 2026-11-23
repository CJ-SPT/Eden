using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(2925, 1, 17608, new[] { 17001 }, 34, 1)]
    [InlineData(2926, 1, 17606, new[] { 17072 }, 32, 1)]
    [InlineData(2940, 2, 17612, new[] { 24087 }, 51, 1)]
    [InlineData(2933, 3, 17606, new[] { 24007 }, 60, 1)]
    [InlineData(2934, 3, 17612, new[] { 24017, 24018 }, 64, 5)]
    [InlineData(2935, 3, 17609, new[] { 24022 }, 57, 1)]
    [InlineData(2937, 3, 17609, new[] { 24013, 24012 }, 55, 5)]
    public void BlockadeVariantsSpawnEachReinforcementOnceAndFinishOnReportedTargets(
        int id,
        int level,
        int towerType,
        int[] objectiveTypes,
        int expectedNpcCount,
        int targetCount
    )
    {
        using var fixture = Create(id, level, MissionRules.SecurityDivision, live: true);
        var mission = fixture.Accept(fixture.Request());
        Assert.Equal(id, mission.MissionId);
        var missions = fixture.Missions;
        var site = Assert.Single(missions.Sites(mission.SystemId));
        Assert.Empty(site.Gates);
        missions.Arrive(mission.SystemId, mission.Position);

        var world = LiveWorld.Find(fixture.Db.Path)!;
        using (var action = world.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var members = action.Set<SavedMissionMember>().Where(member => member.MissionId == mission.Id).ToArray();
            var tower = Assert.Single(members, member => action.Set<WorldNpc>().Find(member.NpcId)!.TypeId == towerType);
            Assert.False(tower.Trigger);
            Assert.False(tower.Required);
        }

        for (int wave = 0; wave < 4; wave++)
        {
            Assert.False(Current(fixture).ObjectiveComplete);
            fixture.Kill(Trigger(wave));
            missions.Arrive(mission.SystemId, mission.Position);
        }

        long[] targets;
        using (var action = world.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var members = action.Set<SavedMissionMember>().Where(member => member.MissionId == mission.Id).ToArray();
            Assert.Equal(expectedNpcCount, members.Length);
            var finalWave = members.Where(member => member.Wave == 4).ToArray();
            var leaders = finalWave.Where(member => member.CompletesMission).ToArray();
            targets = (leaders.Length > 0 ? leaders : finalWave.Where(member => member.Required)).Select(member => member.NpcId).ToArray();
            Assert.Equal(targetCount, targets.Length);
            Assert.All(targets, id => Assert.Contains(action.Set<WorldNpc>().Find(id)!.TypeId, objectiveTypes));
        }
        foreach (long target in targets)
        {
            Assert.False(Current(fixture).ObjectiveComplete);
            fixture.Kill(target);
        }
        Assert.True(Current(fixture).ObjectiveComplete);

        long Trigger(int wave)
        {
            using var action = world.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
            return Assert
                .Single(
                    action
                        .Set<SavedMissionMember>()
                        .Where(member => member.MissionId == mission.Id && member.Wave == wave && member.Trigger)
                )
                .NpcId;
        }
    }
}
