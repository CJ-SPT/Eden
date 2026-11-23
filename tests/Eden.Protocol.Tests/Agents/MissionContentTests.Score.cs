using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Fact]
    public void LevelThreeGuristasScoreRequiresTheGateGuardsAndRadioTelescopeGroup()
    {
        using var fixture = Create(2470, 3, MissionRules.SecurityDivision, live: true);
        var offer = fixture.Accept(fixture.Request());
        var missions = fixture.Missions;
        var sites = missions.Sites(offer.SystemId);
        var lobby = sites.Single(site => site.Position == offer.Position);
        var firstGate = Assert.Single(lobby.Gates);
        var firstPocket = sites.Single(site => site.Position == firstGate.Destination);
        var secondGate = Assert.Single(firstPocket.Gates);
        Assert.False(firstGate.RequiresClear);
        Assert.True(secondGate.RequiresClear);

        Move(fixture, offer.SystemId, firstGate.Position);
        missions.Arrive(offer.SystemId, missions.Gate(MissionFixture.PilotId, firstGate.Id).Destination);
        Move(fixture, offer.SystemId, secondGate.Position);
        Assert.Throws<InvalidDataException>(() => missions.Gate(MissionFixture.PilotId, secondGate.Id));

        var guards = RequiredMembers(0, 8, 8);
        foreach (long guard in guards[..^1])
        {
            fixture.Kill(guard);
        }
        Assert.Throws<InvalidDataException>(() => missions.Gate(MissionFixture.PilotId, secondGate.Id));
        fixture.Kill(guards[^1]);
        Assert.False(Current(fixture).ObjectiveComplete);
        missions.Arrive(offer.SystemId, missions.Gate(MissionFixture.PilotId, secondGate.Id).Destination);

        var targets = RequiredMembers(1, 10, 4);
        foreach (long target in targets[..^1])
        {
            fixture.Kill(target);
            Assert.False(Current(fixture).ObjectiveComplete);
        }
        fixture.Kill(targets[^1]);
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
