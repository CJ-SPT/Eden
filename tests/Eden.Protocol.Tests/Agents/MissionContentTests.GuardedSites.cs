using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    [Theory]
    [InlineData(2250, 3, 8)]
    [InlineData(2251, 3, 8)]
    [InlineData(2256, 2, 7)]
    [InlineData(2257, 2, 7)]
    public async Task StargateCampersRequireEveryReportedDedShip(int id, int level, int total)
    {
        await VerifyReportedGroupCompletion(id, level, total, total, clearOptional: false, gated: false);
    }

    [Theory]
    [InlineData(1256, 3, 63988, 18, 14, 10, 0, true)]
    [InlineData(1398, 4, 845167, 20, 18, 32, 8, true)]
    [InlineData(1398, 4, 845167, 20, 18, 32, 8, false)]
    public void GuardedSiteRequiresFirstPocketAndEveryFinalObjective(
        int id,
        int level,
        long objective,
        int firstTotal,
        int firstRequired,
        int secondTotal,
        int secondRequired,
        bool structureLast
    )
    {
        using var fixture = Create(id, level, MissionRules.SecurityDivision, live: true);
        var offer = fixture.Accept(fixture.Request());
        var missions = fixture.Missions;
        var sites = missions.Sites(offer.SystemId);
        Assert.Equal(3, sites.Count);
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

        var guards = RequiredMembers(0, firstTotal, firstRequired);
        foreach (long guard in guards[..^1])
        {
            fixture.Kill(guard);
        }
        Assert.Throws<InvalidDataException>(() => missions.Gate(MissionFixture.PilotId, secondGate.Id));
        fixture.Kill(guards[^1]);
        Assert.False(Current(fixture).ObjectiveComplete);
        missions.Arrive(offer.SystemId, missions.Gate(MissionFixture.PilotId, secondGate.Id).Destination);

        long structure;
        using (var action = LiveWorld.Find(fixture.Db.Path)!.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            var saved = Assert.Single(action.Set<SavedMissionStructure>().Where(s => s.MissionId == offer.Id));
            Assert.Equal(objective, saved.ObjectId);
            Assert.True(saved.Required);
            Assert.False(saved.CompletesMission);
            structure = saved.NpcId;
        }
        var targets = RequiredMembers(1, secondTotal, secondRequired);
        if (!structureLast)
        {
            fixture.Kill(structure);
            Assert.False(Current(fixture).ObjectiveComplete);
        }
        foreach (long target in targets)
        {
            Assert.False(Current(fixture).ObjectiveComplete);
            fixture.Kill(target);
        }
        if (structureLast)
        {
            Assert.False(Current(fixture).ObjectiveComplete);
            fixture.Kill(structure);
        }
        Assert.True(Current(fixture).ObjectiveComplete);
        fixture.Dock(MissionFixture.StationId);
        Assert.Equal(
            MissionState.Completed,
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id).Offer!.State
        );
        Assert.Empty(missions.Sites(offer.SystemId));

        long[] RequiredMembers(int wave, int total, int required)
        {
            using var action = LiveWorld.Find(fixture.Db.Path)!.Economy.Begin(new(pilots: [MissionFixture.PilotId]));
            var members = action.Set<SavedMissionMember>().Where(m => m.MissionId == offer.Id && m.Wave == wave).ToArray();
            Assert.Equal(total, members.Length);
            var requiredMembers = members.Where(m => m.Required).Select(m => m.NpcId).ToArray();
            Assert.Equal(required, requiredMembers.Length);
            return requiredMembers;
        }
    }
}
