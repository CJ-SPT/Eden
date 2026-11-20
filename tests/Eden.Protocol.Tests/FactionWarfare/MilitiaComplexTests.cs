using Eden.Gameplay.FactionWarfare;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaComplexTests
{
    [Fact]
    public void VictoryPointsAndCapacityRespectStableAndVulnerableSystems()
    {
        var system = new WarzoneSystem(
            30002087,
            new(WarzoneId.AmarrMinmatar, MilitiaFaction.Amarr, MilitiaFaction.Minmatar, 500010),
            MilitiaFaction.Minmatar,
            WarzoneAdjacency.Frontline,
            0,
            null
        );
        var open = MilitiaComplexRules.Policy(MilitiaComplexFamily.Open, 5);
        Assert.Equal(45_000L, MilitiaComplexRules.PointsPerPilot(system, true, open, 1));
        Assert.Equal(22_500L, MilitiaComplexRules.PointsPerPilot(system, true, open, 10));
        Assert.Equal(0, MilitiaComplexRules.PointsPerPilot(system, false, open, 1));
        Assert.Equal(350, MilitiaComplexRules.VictoryPoints(0, true, open));
        Assert.Equal(0, MilitiaComplexRules.VictoryPoints(100, false, open));

        var vulnerable = system with { VictoryPoints = FactionWarzoneRules.VictoryPointThreshold };
        Assert.Equal(0, MilitiaComplexRules.PointsPerPilot(vulnerable, true, open, 1));
        Assert.Equal(45_000L, MilitiaComplexRules.PointsPerPilot(vulnerable, false, open, 1));
        Assert.Equal(75_500, MilitiaComplexRules.VictoryPoints(75_400, true, open));
        Assert.Equal(74_650, MilitiaComplexRules.VictoryPoints(75_000, false, open));
        Assert.Equal(123L, MilitiaComplexRules.CorporationTax(1_239, .1));
    }
}
