using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Fact]
    public void LevelFourMordusHeadhuntersRequiresTheFirstPocketAndOnlyTheMammothGroupInTheSecond()
    {
        const int mammothTypeId = 16037;
        using var fixture = Create(2561, 4, MissionRules.SecurityDivision, live: true);
        var offer = fixture.Accept(fixture.Request());
        var missions = fixture.Missions;
        var sites = missions.Sites(offer.SystemId);
        var firstGate = Assert.Single(sites.Single(site => site.Position == offer.Position).Gates);
        var secondGate = Assert.Single(sites.Single(site => site.Position == firstGate.Destination).Gates);

        Move(fixture, offer.SystemId, firstGate.Position);
        missions.Arrive(offer.SystemId, missions.Gate(MissionFixture.PilotId, firstGate.Id).Destination);
        Move(fixture, offer.SystemId, secondGate.Position);
        Assert.Throws<InvalidDataException>(() => missions.Gate(MissionFixture.PilotId, secondGate.Id));

        foreach (long npc in RequiredMembers(wave: 0, total: 31, required: 31))
        {
            fixture.Kill(npc);
        }
        Assert.False(Current(fixture).ObjectiveComplete);
        missions.Arrive(offer.SystemId, missions.Gate(MissionFixture.PilotId, secondGate.Id).Destination);

        var targets = RequiredMembers(wave: 1, total: 59, required: 12);
        long mammoth;
        using (var action = LiveWorld.Find(fixture.Db.Path)!.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            mammoth = Assert.Single(targets, id => action.Set<WorldNpc>().Find(id)!.TypeId == mammothTypeId);
        }
        fixture.Kill(mammoth);
        foreach (long npc in targets.Where(id => id != mammoth).SkipLast(1))
        {
            fixture.Kill(npc);
        }
        Assert.False(Current(fixture).ObjectiveComplete);
        fixture.Kill(targets.Last(id => id != mammoth));
        Assert.True(Current(fixture).ObjectiveComplete);

        long[] RequiredMembers(int wave, int total, int required)
        {
            using var action = LiveWorld.Find(fixture.Db.Path)!.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
            var members = action.Set<SavedMissionMember>().Where(member => member.MissionId == offer.Id && member.Wave == wave).ToArray();
            Assert.Equal(total, members.Length);
            var targets = members.Where(member => member.Required).Select(member => member.NpcId).ToArray();
            Assert.Equal(required, targets.Length);
            return targets;
        }
    }
}
