using Eden.Gameplay.Mining;

namespace Eden.Protocol.Tests.Mining;

public sealed class AutoMiningRouteRulesTests
{
    [Fact]
    public void ShortestPathIsStableAndRejectsDisconnectedSystems()
    {
        AutoMiningGate[] gates = [new(1, 20, 2, 21), new(1, 10, 3, 11), new(2, 30, 4, 31), new(3, 40, 4, 41), new(3, 11, 1, 10)];
        Assert.Equal([10L, 40L], AutoMiningRouteRules.Path(1, 4, gates).Select(gate => gate.Id));
        Assert.Empty(AutoMiningRouteRules.Path(1, 1, gates));
        Assert.Throws<InvalidDataException>(() => AutoMiningRouteRules.Path(4, 1, gates));
        Assert.Throws<InvalidDataException>(() => AutoMiningRouteRules.Path(1, 5, gates));
    }
}
