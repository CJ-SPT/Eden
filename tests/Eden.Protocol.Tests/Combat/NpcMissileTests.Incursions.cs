using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class NpcMissileTests
{
    [Theory]
    [InlineData(NpcAnchorKind.Belt)]
    [InlineData(NpcAnchorKind.Incursion)]
    [InlineData(NpcAnchorKind.IncursionCamp)]
    [InlineData(NpcAnchorKind.IncursionBelt)]
    public void IncursionExpiryStopsOutgoingNpcLaunchAndPendingMissileImpact(NpcAnchorKind anchor)
    {
        using Fixture fixture = new(useTestClock: true);
        Prepare(fixture);
        var clock = fixture.Clock;
        long taleId = IncursionRules.TaleId(clock.Now);
        long deadline = IncursionRules.EndsAt(taleId);
        clock.UtcNow = DateTimeOffset.FromFileTime(deadline - TimeSpan.TicksPerSecond).ToUniversalTime();
        using LiveWorld live = new(new(fixture.Db.Path), clock, _ => { });
        if (anchor == NpcAnchorKind.Incursion)
        {
            using var action = live.Economy.Begin();
            long siteId = CombatAnomalyRules.FirstSiteId;
            action.Add(
                new WorldCombatAnomaly
                {
                    Id = siteId,
                    SystemId = fixture.Npc.SystemId,
                    IncursionTaleId = taleId,
                }
            );
            var npc = action.Set<WorldNpc>().Find(fixture.Npc.Id)!;
            npc.EncounterId = siteId;
            npc.AnchorKind = NpcAnchorKind.Incursion;
            action.Commit();
        }

        if (NpcPopulationRules.IsIncursionPatrol(anchor))
        {
            using var action = live.Economy.Begin();
            var npc = action.Set<WorldNpc>().Find(fixture.Npc.Id)!;
            npc.AnchorKind = anchor;
            npc.ExpiresAt = deadline;
            action.Commit();
        }

        bool incursion = anchor != NpcAnchorKind.Belt;
        long pilotId = fixture.Pilot.PilotId;
        long shipId = fixture.Pilot.ShipId;
        var before = fixture.Health.Read(pilotId, clock.Now).Remaining;
        Assert.Null(Fire(fixture));
        var first = Assert.Single(live.Economy.Inspect(world => world.Set<WorldMissile>().ToArray()));
        Assert.Equal(MissileFlightStatus.Flying, first.Status);

        using var guard = GameplaySqlGuard.Begin();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        using (var action = live.Economy.Begin())
        {
            action.Set<NpcAggression>().Find(fixture.Npc.Id)!.MissileReadyAt = deadline;
            action.Commit();
        }
        Assert.Null(Fire(fixture));
        Assert.Equal(incursion ? 1 : 2, live.Economy.Inspect(world => world.Set<WorldMissile>().Count()));

        List<NpcShot> impacts = [];
        var missiles = Incoming(fixture);
        for (int tick = 0; tick < 8; tick++)
        {
            impacts.AddRange(missiles.Advance(pilotId, shipId, SpacePosition.Zero, clock.Now));
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        }
        if (incursion)
        {
            Assert.Empty(impacts);
            Assert.Equal(before, fixture.Health.Read(pilotId, clock.Now).Remaining);
            var expired = live.Economy.Inspect(world => world.Set<WorldMissile>().Find(first.Id)!);
            Assert.Equal(MissileFlightStatus.Expired, expired.Status);
            Assert.Equal(deadline, expired.EndedAt);
            Assert.Null(live.Economy.Inspect(world => world.Set<WorldNpc>().Find(fixture.Npc.Id)!.RetiredAt));
        }
        else
        {
            Assert.NotEmpty(impacts);
            Assert.All(impacts, impact => Assert.True(impact.Applied > 0));
        }
    }
}
