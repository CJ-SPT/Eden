using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaHubTests
{
    [Fact]
    public void EligibilityCaptureFloorAndDowntimeUseExactBoundaries()
    {
        var zone = new WarzoneDefinition(WarzoneId.CaldariGallente, MilitiaFaction.Caldari, MilitiaFaction.Gallente, 500010);
        var starting = new WarzoneSystem(30002806, zone, MilitiaFaction.Caldari, WarzoneAdjacency.Frontline, 0, null);
        Assert.Equal(MilitiaHubPhase.Stable, starting.HubPhase);
        Assert.False(MilitiaHubRules.CanAttack(starting.HubPhase, starting.Occupier, MilitiaFaction.Gallente));
        var threshold = FactionWarzoneRules.VictoryPointThreshold;
        var contested = starting with { VictoryPoints = threshold - 1 };
        Assert.Equal(MilitiaHubPhase.Contested, contested.HubPhase);
        Assert.False(MilitiaHubRules.CanAttack(contested.HubPhase, contested.Occupier, MilitiaFaction.Gallente));
        var vulnerable = starting with { VictoryPoints = threshold };
        Assert.Equal(MilitiaHubPhase.Vulnerable, vulnerable.HubPhase);
        Assert.True(MilitiaHubRules.CanAttack(vulnerable.HubPhase, vulnerable.Occupier, MilitiaFaction.Gallente));
        Assert.False(MilitiaHubRules.CanAttack(vulnerable.HubPhase, vulnerable.Occupier, MilitiaFaction.Caldari));
        Assert.False(MilitiaHubRules.CanAttack(vulnerable.HubPhase, vulnerable.Occupier, MilitiaFaction.Amarr));
        Assert.False(MilitiaHubRules.CanAttack(vulnerable.HubPhase, vulnerable.Occupier, null));

        var downtime = new DateTime(2026, 11, 20, 11, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        Assert.Equal(downtime, MilitiaHubRules.NextTransfer(downtime - 1));
        Assert.Equal(downtime + TimeSpan.TicksPerDay, MilitiaHubRules.NextTransfer(downtime));
        var captured = vulnerable with { PendingTransferAt = downtime };
        Assert.Equal(MilitiaHubPhase.Captured, captured.HubPhase);
        Assert.False(MilitiaHubRules.CanAttack(captured.HubPhase, captured.Occupier, MilitiaFaction.Gallente));

        var defense = new HullDefense(
            7_500_000,
            7_500_000,
            10_000_000,
            10_800_000,
            DamageVector.None,
            DamageVector.None,
            DamageVector.None
        );
        Assert.False(MilitiaHubRules.Captures(defense, new(0, 0, 100_001)));
        Assert.True(MilitiaHubRules.Captures(defense, new(0, 0, 100_000)));
        Assert.Equal(40_000, Enumerable.Range(0, 3).Sum(index => MilitiaHubRules.Share(index, 3)));
    }
}
