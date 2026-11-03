using Eden.Gameplay.Dogma;

namespace Eden.Protocol.Tests.Persistence;

public sealed class DogmaGraphTests
{
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
