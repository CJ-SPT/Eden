using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(2925, 1, 17608, 17001, 34)]
    [InlineData(2926, 1, 17606, 17072, 32)]
    [InlineData(2940, 2, 17612, 24087, 51)]
    public void BlockadeVariantsSpawnEachReinforcementOnceAndFinishOnTheLeader(
        int id,
        int level,
        int towerType,
        int leaderType,
        int expectedNpcCount
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
            fixture.Kill(Member(wave, completion: false));
            missions.Arrive(mission.SystemId, mission.Position);
        }

        long leader = Member(4, completion: true);
        using (var action = world.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            Assert.Equal(leaderType, action.Set<WorldNpc>().Find(leader)!.TypeId);
            Assert.Equal(expectedNpcCount, action.Set<SavedMissionMember>().Count(member => member.MissionId == mission.Id));
        }
        Assert.False(Current(fixture).ObjectiveComplete);
        fixture.Kill(leader);
        Assert.True(Current(fixture).ObjectiveComplete);

        long Member(int wave, bool completion)
        {
            using var action = world.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
            return Assert
                .Single(
                    action
                        .Set<SavedMissionMember>()
                        .Where(member =>
                            member.MissionId == mission.Id && member.Wave == wave && (completion ? member.CompletesMission : member.Trigger)
                        )
                )
                .NpcId;
        }
    }
}
