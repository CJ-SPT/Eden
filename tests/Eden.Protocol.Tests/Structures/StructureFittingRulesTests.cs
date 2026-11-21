using System.Collections.Immutable;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureFittingRulesTests
{
    [Theory]
    [InlineData(0.45, 1)]
    [InlineData(0.449, 1.2)]
    [InlineData(0.01, 1.2)]
    [InlineData(0, 1.3)]
    [InlineData(-1, 1.3)]
    public void FittedRigEffectsUseTheSecurityBoundaryWithoutRequiringAnOnlineEffect(double security, double coefficient)
    {
        Dictionary<DogmaAttributeId, double> hull = new() { [DogmaAttributeId.MaxTargetRange] = 100 };
        StructureEquipmentProfile rig = new(
            1,
            1,
            new(ModuleRack.Rig, false, false, 0, 0),
            new([], []),
            new(null, null, null),
            new Dictionary<DogmaAttributeId, double>
            {
                [StructureFittingRules.RigHighSecurity] = 1,
                [StructureFittingRules.RigLowSecurity] = 1.2,
                [StructureFittingRules.RigNullSecurity] = 1.3,
            },
            [
                new(
                    ModifierDomain.Ship,
                    ModifierSelector.Item,
                    null,
                    DogmaAttributeId.MaxTargetRange,
                    StructureFittingRules.RigSecurityMultiplier,
                    DogmaOperation.PostMultiply,
                    PassiveEffectPhase.Fitted
                ),
            ],
            ImmutableHashSet<int>.Empty,
            1
        );
        var state = StructureFittingRules.Calculate(
            100,
            hull,
            [new(101, (int)InventoryFlag.RigSlot0, false, rig)],
            [],
            new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>(),
            security
        );
        Assert.Equal(100 * coefficient, state.Attributes[DogmaAttributeId.MaxTargetRange], 6);
        Assert.Equal(coefficient, state.Equipment[101][StructureFittingRules.RigSecurityMultiplier]);
    }
}
