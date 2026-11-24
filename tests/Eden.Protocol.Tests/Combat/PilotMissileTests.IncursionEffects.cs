using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.World.Runtime;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class PilotMissileTests
{
    [Fact]
    public void PaidMissileKeepsIncursionLaunchDamageAfterExpiry()
    {
        var normal = Impact(penalized: false);
        var penalized = Impact(penalized: true);
        Assert.True(normal.Damage.Total > 0);
        Assert.Equal(normal.Damage.Total * 0.5, penalized.Damage.Total, 10);
        Assert.Equal(normal.Health.Defense, penalized.Health.Defense);

        static MissileImpact Impact(bool penalized)
        {
            using var fixture = Prepare();
            using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
            long launcher = MissileCombatTests.Load(fixture, Launcher, Charge);
            Space(fixture);
            if (penalized)
            {
                long now = fixture.Clock.Now;
                int lane = IncursionRules.HighSecurityLane;
                long tale = IncursionRules.TaleId(now, lane);
                IncursionFocus focus = new(tale, SystemId, 20000020, now, now + 1, 1, [new(SystemId, IncursionScene.Headquarters)]);
                live.SetIncursionFocus(lane, focus, now);
            }
            Assert.Equal(2, Launch(fixture, launcher).Magazine.Quantity);
            Advance(fixture);
            return Assert.Single(Store(fixture).TakeImpacts(fixture.PilotId));
        }
    }
}
