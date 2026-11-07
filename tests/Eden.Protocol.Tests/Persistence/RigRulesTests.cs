using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Tests.Persistence;

public sealed class RigRulesTests
{
    [Fact]
    public void CalibrationAndRigSlotsAreCountedWithoutCpuPowerOrHardpoints()
    {
        Dictionary<DogmaAttributeId, double> hull = new()
        {
            [DogmaAttributeId.RigSlotsLeft] = 2,
            [DogmaAttributeId.CpuOutput] = 100,
            [DogmaAttributeId.PowerOutput] = 50,
            [DogmaAttributeId.TurretSlotsLeft] = 3,
            [DogmaAttributeId.LauncherSlotsLeft] = 1,
        };
        FittedResourceUsage[] modules =
        [
            new((int)InventoryFlag.RigSlot0, false, 0, 0, false, false, 150),
            new((int)InventoryFlag.RigSlot1, false, 0, 0, false, false, 200),
        ];
        var fitted = FittingRules.Apply(hull, new Dictionary<int, int>(), modules);
        Assert.Equal(350, fitted[DogmaAttributeId.CalibrationLoad]);
        Assert.Equal(0, fitted[DogmaAttributeId.RigSlotsLeft]);
        Assert.Equal(0, fitted[DogmaAttributeId.CpuLoad]);
        Assert.Equal(0, fitted[DogmaAttributeId.PowerLoad]);
        Assert.Equal(3, fitted[DogmaAttributeId.TurretSlotsLeft]);
        Assert.Equal(1, fitted[DogmaAttributeId.LauncherSlotsLeft]);
    }

    [Theory]
    [InlineData(1, 1, 50, 350, true)]
    [InlineData(2, 2, 150, 250, true)]
    [InlineData(3, 3, 75, 325, true)]
    [InlineData(4, 4, 300, 100, true)]
    [InlineData(1, 2, 50, 0, false)]
    [InlineData(2, 1, 50, 0, false)]
    [InlineData(0, 0, 50, 0, false)]
    [InlineData(1, 1, 150, 300, false)]
    [InlineData(1, 1, 0, 0, false)]
    [InlineData(1, 1, -1, 0, false)]
    [InlineData(1, 1, 50, -1, false)]
    [InlineData(1, 1, double.NaN, 0, false)]
    [InlineData(1, 1, 50, double.PositiveInfinity, false)]
    public void SizeAndCalibrationMustFitExactly(int hullSize, int rigSize, double cost, double used, bool allowed)
    {
        Dictionary<DogmaAttributeId, double> hull = new()
        {
            [DogmaAttributeId.RigSize] = hullSize,
            [DogmaAttributeId.CalibrationCapacity] = 400,
        };
        Dictionary<DogmaAttributeId, double> rig = new()
        {
            [DogmaAttributeId.RigSize] = rigSize,
            [DogmaAttributeId.CalibrationCost] = cost,
        };
        if (allowed)
        {
            RigRules.CheckFit(hull, rig, used);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => RigRules.CheckFit(hull, rig, used));
        }
    }
}
