using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class NpcEwarTests
{
    [Theory]
    [InlineData(1000, 0, 1)]
    [InlineData(1000, .99999, 1)]
    [InlineData(300000, 0, 0)]
    public void GuardTriggersTrackActualInRangeAttemptsIncludingFailedJams(double distance, double roll, int attempts)
    {
        using Fixture fixture = new(2385);
        var combat = fixture.Combat;
        combat.Db.Store.SetPosition(
            NpcCombatTests.Fixture.ShipId,
            combat.Npc.Position + new SpacePosition(distance, 0, 0),
            combat.Npc.SystemId
        );
        fixture.Roll.Value = roll;
        fixture.Advance();
        var triggers = DroneCombatTriggerTests.Read(combat.Db.Path, NpcCombatTests.Fixture.ShipId);
        Assert.Equal(attempts, triggers.Incoming.Revision);
        Assert.Equal(1, triggers.Offensive.Revision);
        if (attempts > 0)
        {
            Assert.Equal(combat.Npc.Id, triggers.Incoming.OtherId);
            Assert.Equal(fixture.Now, triggers.Incoming.At);
        }
        fixture.Advance();
        Assert.Equal(triggers, DroneCombatTriggerTests.Read(combat.Db.Path, NpcCombatTests.Fixture.ShipId));
    }
}
