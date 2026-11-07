using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcCapacitorRuleTests
{
    [Fact]
    public void NominalPoolStartsFullAndRegeneratesFromTheSavedDeadlineWithoutMutatingSnapshots()
    {
        NpcCapacitorProfile profile = new(700, 1000000);
        Assert.Equal(new(700, 700), NpcCapacitorRules.Read(profile, null, 100));
        NpcCapacitorState state = new(100, 100);
        Assert.Equal(new(100, 700), NpcCapacitorRules.Read(profile, state, 100));
        Assert.Equal(new(100, 700), NpcCapacitorRules.Read(profile, state, 99));
        long later = 100 + 10 * TimeSpan.TicksPerSecond;
        double expected = ChargedResource.Recharged(100, 700, 1000000, 10000);
        Assert.Equal(new(expected, 700), NpcCapacitorRules.Read(profile, state, later));
        Assert.Equal(new(100, 100), state);
        Assert.Equal(new(700, 700), NpcCapacitorRules.Read(profile, new(1000, 100), 100));
    }

    [Theory]
    [InlineData(double.NaN, 1000)]
    [InlineData(-1, 1000)]
    [InlineData(700, 0)]
    [InlineData(700, -1)]
    [InlineData(700, double.PositiveInfinity)]
    public void MissingOrInvalidRechargeProfilesDoNotCreateAnInfiniteNpcEnergySource(double capacity, double recharge)
    {
        Dictionary<DogmaAttributeId, double> values = new()
        {
            [DogmaAttributeId.CapacitorCapacity] = capacity,
            [DogmaAttributeId.CapacitorRechargeTime] = recharge,
        };
        Assert.Throws<InvalidDataException>(() => NpcCapacitorRules.Profile(values));
        Assert.Throws<InvalidDataException>(() => NpcCapacitorRules.Read(new(capacity, recharge), null, 100));
    }

    [Fact]
    public void AShipWithoutCapacitorCannotSupplyEnergyAndInvalidSavedChargesReject()
    {
        var profile = NpcCapacitorRules.Profile(new Dictionary<DogmaAttributeId, double>());
        Assert.Equal(new(0, 0), NpcCapacitorRules.Read(profile, null, 100));
        Assert.Equal(new(0, 0), NpcCapacitorRules.Read(profile, new(100, 100), 100));
        Assert.Throws<InvalidDataException>(() => NpcCapacitorRules.Read(profile, new(double.NaN, 100), 100));
        Assert.Throws<InvalidDataException>(() => NpcCapacitorRules.Read(profile, new(-1, 100), 100));
    }
}
