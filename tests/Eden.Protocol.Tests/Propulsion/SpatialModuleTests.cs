using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;

namespace Eden.Protocol.Tests.Propulsion;

public sealed class SpatialModuleTests
{
    [Fact]
    public void JumpUsesNormalizedHeadingAndRejectsInvalidCoordinates()
    {
        Assert.Equal(new SpacePosition(100010, 20, 30), SpatialModuleRules.Jump(new(10, 20, 30), new(5, 0, 0)));
        Assert.Throws<InvalidDataException>(() => SpatialModuleRules.Jump(SpacePosition.Zero, SpacePosition.Zero));
        Assert.Throws<InvalidDataException>(() => SpatialModuleRules.Jump(new(double.NaN, 0, 0), new(1, 0, 0)));
        Assert.Throws<InvalidDataException>(() => SpatialModuleRules.Jump(SpacePosition.Zero, new(double.PositiveInfinity, 0, 0)));
    }

    [Theory]
    [InlineData(2199.999, true)]
    [InlineData(2200, false)]
    [InlineData(2200.001, false)]
    public void DecloakProximityMeasuresSurfaceDistance(double distance, bool expected) =>
        Assert.Equal(expected, SpatialModuleRules.Proximity(SpacePosition.Zero, 100, new(distance, 0, 0), 100));

    [Theory]
    [InlineData(DogmaEffect.Cloaking, true, false)]
    [InlineData(DogmaEffect.PrototypeCloaking, true, false)]
    [InlineData(DogmaEffect.CovertCloaking, true, true)]
    [InlineData(DogmaEffect.MicroJumpDrive, false, false)]
    public void ProfilesPreserveWarpSafetyAndRejectInvalidDurations(DogmaEffect effect, bool cloak, bool warpSafe)
    {
        Dictionary<DogmaAttributeId, double> values = new()
        {
            [DogmaAttributeId.Duration] = 12000,
            [DogmaAttributeId.CloakVelocityMultiplier] = .25,
            [DogmaAttributeId.ModuleReactivationDelay] = 180000,
        };
        var module = SpatialModuleRules.Define(1, 2, effect, values);
        Assert.Equal(cloak, module.Cloak);
        Assert.Equal(warpSafe, module.WarpSafe);
        values[DogmaAttributeId.ModuleReactivationDelay] = double.NaN;
        Assert.Throws<InvalidDataException>(() => SpatialModuleRules.Define(1, 2, effect, values));
    }
}
