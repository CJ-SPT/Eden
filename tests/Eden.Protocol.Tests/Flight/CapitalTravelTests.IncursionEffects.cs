using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncursionJamsStandardCynoUntilExactDeadlineAndPermitsCovert(bool covert)
    {
        using Fixture fixture = covert ? new(emitterHull: Rapier, generator: CapitalTravelRules.CovertGenerator) : new();
        using Client fleetmate = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        fixture.Join(emitter);
        long now = fixture.Clock.Now;
        long endsAt = now + TimeSpan.TicksPerSecond;
        int lane = IncursionRules.LowSecurityLane;
        long tale = IncursionRules.TaleId(now, lane);
        fixture.Live.SetIncursionFocus(lane, new(tale, Jan, 20000162, now, endsAt, 0, [new(Jan, IncursionScene.Staging)]), now);
        var navigation = fixture.Db.Services.GetRequiredService<ICapitalNavigation>();
        Assert.Equal(IncursionRules.OpensAt(tale), navigation.JammerOnlineAt(Jan));

        var result = emitter.Call("dogmaIM", "Activate", fixture.Module, "cynosuralGeneration", null, 0);
        if (covert)
        {
            Assert.IsType<PilotCallResult.Success>(result);
            Assert.Equal(CapitalTravelRules.CovertField, fixture.Travel.Field(emitter.Pilot.PilotId)!.Beacon.TypeId);
        }
        else
        {
            Assert.IsType<PilotCallResult.Failure>(result);
            Assert.Null(fixture.Travel.Field(emitter.Pilot.PilotId));
        }

        fixture.Clock.Seconds = 1;
        Assert.Equal(endsAt, fixture.Clock.Now);
        Assert.Null(navigation.JammerOnlineAt(Jan));
        if (!covert)
        {
            Assert.Equal(CapitalTravelRules.IndustrialField, fixture.Light(emitter).Beacon.TypeId);
        }
    }
}
