using Eden.Gameplay.Sovereignty;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyResourceRulesTests
{
    [Fact]
    public void PriorityProductionAndSharedFuelExhaustionSettleAtExactBoundaries()
    {
        SovereigntyUpgradeDefinition[] definitions =
        [
            new(1, "Producer", "", 0, 20, 10, 2, 2, 50, 0),
            new(2, "Consumer", "", 70, 0, 10, 0, 2, 0, 0),
        ];
        SovereigntyUpgrade[] upgrades =
        [
            new(1, 0, true, SovereigntyUpgradePower.Pending),
            new(2, 1, true, SovereigntyUpgradePower.Pending),
        ];
        var started = SovereigntyResourceRules.Advance(definitions, upgrades, [new(10, 6)], new(20, 20), 0, 0);
        Assert.All(started.Upgrades, row => Assert.Equal(SovereigntyUpgradePower.Online, row.State));
        Assert.Equal(4, Assert.Single(started.Fuels).Amount);
        long exhaustedAt = TimeSpan.FromMinutes(75).Ticks;
        var end = SovereigntyResourceRules.Advance(definitions, started.Upgrades, started.Fuels, new(20, 20), 0, exhaustedAt);
        Assert.Equal(0, Assert.Single(end.Fuels).Amount);
        Assert.All(end.Upgrades, row => Assert.Equal(SovereigntyUpgradePower.Low, row.State));
        Assert.Equal(new(0, 0), end.Used);
        var repeated = SovereigntyResourceRules.Advance(definitions, end.Upgrades, end.Fuels, new(20, 20), end.UpdatedAt, end.UpdatedAt);
        Assert.Equal(end.Upgrades, repeated.Upgrades);
        Assert.Equal(end.Fuels, repeated.Fuels);
    }

    [Fact]
    public void FractionalSharedFuelBurnMatchesSmallTicksAndExclusivePriorityChanges()
    {
        SovereigntyUpgradeDefinition[] definitions =
        [
            new(1, "A", "same", 10, 0, 10, 3, 7, 0, 0),
            new(2, "B", "same", 10, 0, 10, 3, 11, 0, 0),
            new(3, "C", "", 10, 0, 10, 0, 13, 0, 0),
        ];
        SovereigntyUpgrade[] upgrades =
        [
            new(1, 0, true, SovereigntyUpgradePower.Pending),
            new(2, 1, true, SovereigntyUpgradePower.Pending),
            new(3, 2, true, SovereigntyUpgradePower.Pending),
        ];
        var initial = SovereigntyResourceRules.Advance(definitions, upgrades, [new(10, 27)], new(20, 0), 0, 0);
        Assert.Equal(SovereigntyUpgradePower.Low, initial.Upgrades[1].State);
        long finish = TimeSpan.FromHours(8).Ticks + 7;
        var single = SovereigntyResourceRules.Advance(definitions, initial.Upgrades, initial.Fuels, new(20, 0), 0, finish);
        var small = initial;
        for (long now = TimeSpan.FromSeconds(37).Ticks; now < finish; now += TimeSpan.FromSeconds(37).Ticks)
        {
            small = SovereigntyResourceRules.Advance(definitions, small.Upgrades, small.Fuels, new(20, 0), small.UpdatedAt, now);
        }
        small = SovereigntyResourceRules.Advance(definitions, small.Upgrades, small.Fuels, new(20, 0), small.UpdatedAt, finish);
        Assert.Equal(single.Fuels, small.Fuels);
        Assert.Equal(single.Upgrades, small.Upgrades);
        var reordered = SovereigntyResourceRules.Advance(
            definitions,
            initial.Upgrades.Select(row => row with { Priority = row.TypeId == 2 ? -1 : row.Priority }).ToArray(),
            initial.Fuels,
            new(20, 0),
            0,
            0
        );
        Assert.Equal(2, reordered.Upgrades[0].TypeId);
        Assert.Equal(SovereigntyUpgradePower.Online, reordered.Upgrades[0].State);
        Assert.Equal(SovereigntyUpgradePower.Low, reordered.Upgrades[1].State);
        Assert.Throws<InvalidDataException>(() => SovereigntyResourceRules.Advance(definitions, upgrades, [new(10, -1)], new(20, 0), 0, 0));
    }

    [Fact]
    public void WorkforceRoutingRequiresAllianceTransitAndExplicitDestinationImports()
    {
        SovereigntyNetworkHub[] hubs =
        [
            new(1, 1, 10, new(100, 500), new(SovereigntyWorkforceMode.Export, 3, 900)),
            new(2, 2, 10, new(100, 800), new(SovereigntyWorkforceMode.Transit)),
            new(3, 3, 10, new(100, 200), new(SovereigntyWorkforceMode.Import, Sources: [1])),
        ];
        Dictionary<int, IReadOnlyList<int>> gates = new()
        {
            [1] = [2],
            [2] = [1, 3],
            [3] = [2],
        };
        var flow = Assert.Single(SovereigntyResourceRules.Route(hubs, gates));
        Assert.True(flow.Connected);
        Assert.Equal(500, flow.Amount);
        foreach (
            var broken in new[] { hubs[1] with { AllianceId = 11 }, hubs[1] with { Configuration = new(SovereigntyWorkforceMode.Idle) } }
        )
        {
            flow = Assert.Single(SovereigntyResourceRules.Route([hubs[0], broken, hubs[2]], gates));
            Assert.False(flow.Connected);
            Assert.Equal(0, flow.Amount);
        }
        flow = Assert.Single(
            SovereigntyResourceRules.Route(
                [hubs[0], hubs[1], hubs[2] with { Configuration = new(SovereigntyWorkforceMode.Import, Sources: []) }],
                gates
            )
        );
        Assert.False(flow.Connected);
        Assert.Equal(0, flow.Amount);
    }
}
