using Eden.Gameplay.Dogma;

namespace Eden.Protocol.Tests.Persistence;

public sealed class DogmaGraphTests
{
    [Theory]
    [InlineData(200, 100)]
    [InlineData(-200, 10)]
    public void FinalModifiedValuesRespectTheNativeCeilingAndFloor(double addition, double expected)
    {
        var attribute = DogmaAttributeId.SignatureRadius;
        DogmaNode[] nodes =
        [
            new(
                1,
                new Dictionary<DogmaAttributeId, double>
                {
                    [attribute] = 20,
                    [DogmaAttributeId.CpuOutput] = 100,
                    [DogmaAttributeId.PowerOutput] = 10,
                }
            ),
        ];
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions = new()
        {
            [attribute] = new(attribute, 0, Maximum: DogmaAttributeId.CpuOutput, Minimum: DogmaAttributeId.PowerOutput),
        };
        DogmaModifier[] modifiers = [new(1, attribute, DogmaOperation.Add, new DogmaValue.Constant(addition))];
        Assert.Equal(expected, new DogmaGraph(nodes, modifiers, definitions).Read(1, attribute));
    }

    [Fact]
    public void PostAssignmentOverridesAllEarlierBonusesAndIsClampedAfterward()
    {
        var attribute = DogmaAttributeId.ShieldEmResonance;
        DogmaNode[] nodes = [new(1, new Dictionary<DogmaAttributeId, double> { [attribute] = .8 })];
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions = new()
        {
            [attribute] = new(attribute, 1, false, Maximum: DogmaAttributeId.MiningRoleYieldMultiplier),
            [DogmaAttributeId.MiningRoleYieldMultiplier] = new(DogmaAttributeId.MiningRoleYieldMultiplier, 1),
        };
        DogmaModifier[] modifiers =
        [
            new(1, attribute, DogmaOperation.PostAssignment, new DogmaValue.Constant(100)),
            new(1, attribute, DogmaOperation.PostPercent, new DogmaValue.Constant(-60)),
            new(1, attribute, DogmaOperation.PostMultiply, new DogmaValue.Constant(.5)),
        ];
        Assert.Equal(1, new DogmaGraph(nodes, modifiers, definitions).Read(1, attribute));
        Assert.Equal(1, new DogmaGraph(nodes, modifiers.Reverse().ToArray(), definitions).Read(1, attribute));
    }

    [Fact]
    public void StrongestBonusesStackSeparatelyFromPenaltiesAndExemptModifiers()
    {
        const long Ship = 1;
        var attribute = DogmaAttributeId.ArmorEmResonance;
        DogmaNode[] nodes = [new(Ship, new Dictionary<DogmaAttributeId, double> { [attribute] = 1 })];
        var modifiers = new[] { -20d, -40d, -30d }
            .Select(value => new DogmaModifier(Ship, attribute, DogmaOperation.PostPercent, new DogmaValue.Constant(value)))
            .Append(new(Ship, attribute, DogmaOperation.PreMultiply, new DogmaValue.Constant(0.9)))
            .ToArray();
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions = new() { [attribute] = new(attribute, 1, false) };
        double expected = 0.9 * 0.6 * (1 - 0.3 * 0.8691199806) * (1 - 0.2 * 0.5705831435);
        Assert.Equal(expected, new DogmaGraph(nodes, modifiers, definitions).Read(Ship, attribute), 9);
        Assert.Equal(expected, new DogmaGraph(nodes, modifiers.Reverse().ToArray(), definitions).Read(Ship, attribute), 9);
    }

    [Fact]
    public void DependenciesApplyAdditionsBeforeMultipliersAndRejectCycles()
    {
        DogmaNode[] nodes = [new(1, new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.ShieldCapacity] = 200 })];
        DogmaModifier[] modifiers =
        [
            new(1, DogmaAttributeId.ShieldCapacity, DogmaOperation.PostPercent, new DogmaValue.Constant(15), true),
            new(1, DogmaAttributeId.ShieldCapacity, DogmaOperation.Add, new DogmaValue.Constant(400)),
        ];
        Assert.Equal(
            690,
            new DogmaGraph(nodes, modifiers, new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>()).Read(
                1,
                DogmaAttributeId.ShieldCapacity
            ),
            8
        );
        DogmaModifier[] cycle =
        [
            new(1, DogmaAttributeId.ShieldCapacity, DogmaOperation.Add, new DogmaValue.Attribute(1, DogmaAttributeId.ShieldCapacity)),
        ];
        Assert.Throws<InvalidDataException>(() =>
            new DogmaGraph(nodes, cycle, new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>()).Read(
                1,
                DogmaAttributeId.ShieldCapacity
            )
        );
    }
}
