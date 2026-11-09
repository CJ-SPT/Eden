using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Flight;

public sealed class GateTravelRulesTests
{
    [Fact]
    public void ArrivalsSpreadConsecutiveSameHullShipsAcrossTheGateShell()
    {
        var gate = new SpacePosition(1000, 2000, 3000);
        const double gateRadius = 3500;
        const double shipRadius = 100;
        var positions = Enumerable
            .Range(0, 256)
            .Select(index => GateTravelRules.Arrival(2000000000000L + index * 2, gate, gateRadius, shipRadius))
            .ToArray();
        Assert.Equal(positions.Length, positions.Distinct().Count());
        Assert.All(positions, point => Assert.InRange(point.DistanceTo(gate) - gateRadius - shipRadius, 11999.999, 12000.001));
        Assert.Contains(positions, point => point.X > gate.X && point.Y > gate.Y && point.Z > gate.Z);
        Assert.Contains(positions, point => point.X < gate.X && point.Y < gate.Y && point.Z < gate.Z);
        Assert.Equal(positions[0], GateTravelRules.Arrival(2000000000000L, gate, gateRadius, shipRadius));
    }
}
