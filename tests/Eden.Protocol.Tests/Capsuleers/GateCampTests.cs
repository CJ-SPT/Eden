using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Server.Administration;
using Eden.World.Capsuleers;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class GateCampTests
{
    [Fact]
    public void ThreePilotsCoordinateOnOneGateAndDisperseAtTheShiftBoundary()
    {
        CapsuleerGateCamps camps = new();
        long end = GateCampRules.End(0, 180);
        var watch = camps.At(300, 500, end, 0)!;
        Assert.Equal(CampRole.Tackle, watch.Join(10, true));
        Assert.Equal(CampRole.Scout, watch.Join(20, false));
        Assert.Equal(CampRole.Damage, watch.Join(30, false));
        Assert.Equal(CampRole.Scout, watch.Join(20, false));
        Assert.Null(watch.Join(40, true));
        Assert.Null(camps.At(300, 501, end, 0));
        watch.Report(100, TimeSpan.TicksPerSecond);
        Assert.Equal(100, camps.At(300, 500, end, 0)!.Reported(TimeSpan.TicksPerSecond));
        Assert.Equal(0, watch.Reported(TimeSpan.FromSeconds(14).Ticks));
        Assert.False(camps.At(301, 500, end, 0)!.Member(10));
        watch.Dispersing = true;
        watch.Leave(30);
        Assert.Null(watch.Join(40, false));
        Assert.Null(camps.At(300, 500, end, end));
        Assert.Equal(0, GateCampRules.End(end, 180));
        var next = camps.At(300, 501, GateCampRules.End(end * 2, 180), end * 2)!;
        Assert.False(next.Member(20));
        Assert.False(next.Dispersing);
        Assert.Equal(CampRole.Tackle, next.Join(40, true));
        Assert.True(GateCampRules.StandOff(CampRole.Scout) > GateCampRules.StandOff(CampRole.Tackle));
    }

    [Fact]
    public void CampSettingsRoundTripWithLowsecAndBubbleEligibility()
    {
        SettingsForm form = SettingsForm.From(new());
        form.LowsecGateCamps = true;
        form.CampBubbles = false;
        form.CampSeconds = 120;
        form.CampPercent = 70;
        form.InterdictorPercent = 3;
        var policy = form.Build().Capsuleers;
        var repeat = SettingsForm.From(new() { Capsuleers = policy });
        Assert.True(repeat.LowsecGateCamps);
        Assert.False(repeat.CampBubbles);
        Assert.Equal(120, repeat.CampSeconds);
        Assert.Equal(70, repeat.CampPercent);
        Assert.Equal(3, repeat.InterdictorPercent);
        var camp = new PatrolProgress(PatrolKind.GateCamp, 300);
        Assert.True(PatrolRules.Allowed(camp, -.2, policy));
        Assert.True(PatrolRules.Allowed(camp, .2, policy));
        Assert.False(PatrolRules.Allowed(camp, .8, policy));
        Assert.False(PatrolRules.Allowed(camp, .2, policy with { LowsecGateCamps = false }));
        Assert.False(PatrolRules.Allowed(camp, 0, policy with { GateCamps = false }));
        Assert.Throws<InvalidDataException>(() => CapsuleerPopulationRules.Validate(policy with { CampSeconds = 0 }));
    }
}
