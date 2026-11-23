using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(1219, 2, 11, 1, 1)]
    [InlineData(1222, 3, 15, 11, 10)]
    public void RogueDroneHarassmentClearsTheFirstPocketAndItsReportedFinalTargets(int id, int level, int guards, int final, int required)
    {
        const int rogueDroneFaction = 500025;
        using var fixture = Create(id, level, MissionRules.SecurityDivision, live: true);
        var offer = fixture.Accept(fixture.Request());
        var missions = fixture.Missions;
        var sites = missions.Sites(offer.SystemId);
        var firstGate = Assert.Single(sites.Single(site => site.Position == offer.Position).Gates);
        var secondGate = Assert.Single(sites.Single(site => site.Position == firstGate.Destination).Gates);

        Move(fixture, offer.SystemId, firstGate.Position);
        missions.Arrive(offer.SystemId, missions.Gate(MissionFixture.PilotId, firstGate.Id).Destination);
        Move(fixture, offer.SystemId, secondGate.Position);
        Assert.Throws<InvalidDataException>(() => missions.Gate(MissionFixture.PilotId, secondGate.Id));
        foreach (long npc in Targets(wave: 0, total: guards, minimum: guards))
        {
            fixture.Kill(npc);
        }
        Assert.False(Current(fixture).ObjectiveComplete);
        missions.Arrive(offer.SystemId, missions.Gate(MissionFixture.PilotId, secondGate.Id).Destination);
        foreach (long npc in Targets(wave: 1, total: final, minimum: required))
        {
            Assert.False(Current(fixture).ObjectiveComplete);
            fixture.Kill(npc);
        }
        Assert.True(Current(fixture).ObjectiveComplete);

        long[] Targets(int wave, int total, int minimum)
        {
            using var action = LiveWorld.Find(fixture.Db.Path)!.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
            var members = action.Set<SavedMissionMember>().Where(member => member.MissionId == offer.Id && member.Wave == wave).ToArray();
            Assert.Equal(total, members.Length);
            Assert.All(members, member => Assert.Equal(rogueDroneFaction, action.Set<WorldNpc>().Find(member.NpcId)!.FactionId));
            var targets = members.Where(member => member.Required).Select(member => member.NpcId).ToArray();
            Assert.Equal(minimum, targets.Length);
            return targets;
        }
    }
}
