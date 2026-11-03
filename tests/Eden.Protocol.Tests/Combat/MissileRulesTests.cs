using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

namespace Eden.Protocol.Tests.Combat;

public sealed class MissileRulesTests
{
    [Theory]
    [InlineData(40, 0, 1)]
    [InlineData(400, 170, 1)]
    [InlineData(20, 0, .5)]
    [InlineData(20, 85, .5)]
    [InlineData(40, 340, .6579272625145041)]
    [InlineData(400, 3400, .6579272625145041)]
    public void ApplicationCapsDamageBySignatureAndVelocityUsingTheStoredExponent(double signature, double speed, double expected)
    {
        MissileWarhead missile = new(new(83, 0, 0, 0), 40, 170, .604);
        Assert.Equal(expected, MissileRules.Application(missile, signature, speed), 10);
    }

    [Theory]
    [InlineData(DogmaAttributeId.EmDamage, -1)]
    [InlineData(DogmaAttributeId.EmDamage, double.PositiveInfinity)]
    [InlineData(DogmaAttributeId.ExplosionRadius, 0)]
    [InlineData(DogmaAttributeId.ExplosionVelocity, double.NaN)]
    [InlineData(DogmaAttributeId.MissileReductionExponent, -1)]
    public void InvalidWarheadsCannotProduceAnApplicationProfile(DogmaAttributeId attribute, double value)
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.EmDamage] = 83,
            [DogmaAttributeId.ExplosionRadius] = 40,
            [DogmaAttributeId.ExplosionVelocity] = 170,
            [DogmaAttributeId.MissileReductionExponent] = .604,
        };
        attributes[attribute] = value;
        Assert.Throws<InvalidDataException>(() => MissileRules.Warhead(attributes));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, -1)]
    [InlineData(double.NaN, 0)]
    [InlineData(40, double.PositiveInfinity)]
    public void InvalidTargetsCannotProduceNonfiniteDamage(double signature, double speed)
    {
        MissileWarhead missile = new(new(83, 0, 0, 0), 40, 170, .604);
        Assert.Throws<InvalidDataException>(() => MissileRules.Application(missile, signature, speed));
    }
}
