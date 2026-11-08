using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Thermal;

namespace Eden.Protocol.Tests.Thermal;

public sealed class OverloadRulesTests
{
    private static Dictionary<DogmaAttributeId, double> Values() =>
        new()
        {
            [DogmaAttributeId.StructureHitPoints] = 40,
            [DogmaAttributeId.HeatDamage] = 2.3,
            [DogmaAttributeId.HeatAbsorption] = .01,
            [DogmaAttributeId.RequiredThermodynamics] = 1,
            [DogmaAttributeId.Duration] = 10000,
            [DogmaAttributeId.OverloadDurationBonus] = -15,
        };

    [Fact]
    public void EveryAdmittedHeatableProfileUsesItsOwnSourceAttributesAndCannotModifyAnotherItem()
    {
        var matrix = ThermalFixtures.Read<ThermalFixtures.Matrix>("ThermalModuleMatrix.json");
        Assert.Equal(3662168, matrix.Build);
        Assert.Equal(1826, matrix.Profiles.Count);
        Assert.Equal(19, matrix.Programs.Count);
        var definitions = matrix.Definitions.ToDictionary(
            d => d.Id,
            d => new DogmaAttributeDefinition(d.Id, d.DefaultValue, d.Stackable, d.Maximum, d.Minimum)
        );
        foreach (var entry in matrix.Profiles)
        {
            var program = Assert.Single(matrix.Programs, p => p.EffectID == entry.EffectId);
            Assert.Equal(OverloadRules.EffectCategory, program.EffectCategory);
            Assert.All(
                program.ModifierInfo,
                m =>
                {
                    Assert.Equal("itemID", m.Domain);
                    Assert.Equal("ItemModifier", m.Func);
                    Assert.Equal((int)DogmaOperation.PostPercent, m.Operation);
                }
            );
            var bonuses = program.ModifierInfo.Select(m => new OverloadBonus(m.ModifiedAttributeID, m.ModifyingAttributeID)).ToArray();
            var profile = OverloadRules.Define(entry.TypeId, entry.EffectId, entry.Rack, entry.Attributes, bonuses);
            Assert.Equal(1, profile.RequiredThermodynamics);
            var graph = new DogmaGraph(
                [new(100, entry.Attributes), new(101, entry.Attributes)],
                OverloadRules.Modifiers(profile, 100, true),
                definitions
            );
            foreach (var bonus in bonuses)
            {
                double basis = entry.Attributes.GetValueOrDefault(bonus.Attribute, definitions[bonus.Attribute].DefaultValue);
                double multiplier = 1 + entry.Attributes[bonus.SourceAttribute] / 100;
                Assert.Equal(basis * multiplier, graph.Read(100, bonus.Attribute), 7);
                Assert.Equal(basis, graph.Read(101, bonus.Attribute), 7);
            }
            Assert.Empty(OverloadRules.Modifiers(profile, 100, false));
            Assert.Equal(profile.HeatDamage * .75, OverloadRules.ThermodynamicsDamage(profile.HeatDamage, 5, -5), 8);
        }
    }

    [Theory]
    [InlineData(0, 2.3)]
    [InlineData(1, 2.185)]
    [InlineData(3, 1.955)]
    [InlineData(5, 1.725)]
    public void ThermodynamicsReducesDamageWithoutChangingRackHeatGeneration(int level, double damage)
    {
        var profile = OverloadRules.Define(
            399,
            3002,
            ModuleRack.High,
            Values(),
            [new(DogmaAttributeId.Duration, DogmaAttributeId.OverloadDurationBonus)]
        );
        Assert.Equal(damage, OverloadRules.ThermodynamicsDamage(profile.HeatDamage, level, -5), 8);
        Assert.Equal(.01, profile.HeatAbsorption);
    }

    [Theory]
    [InlineData(DogmaAttributeId.StructureHitPoints, 0)]
    [InlineData(DogmaAttributeId.HeatDamage, double.NaN)]
    [InlineData(DogmaAttributeId.HeatAbsorption, -1)]
    [InlineData(DogmaAttributeId.RequiredThermodynamics, 1.5)]
    [InlineData(DogmaAttributeId.RequiredThermodynamics, 6)]
    [InlineData(DogmaAttributeId.OverloadDurationBonus, -101)]
    public void InvalidConditionInputsAndBonusValuesCannotProduceAProfile(DogmaAttributeId attribute, double value)
    {
        var values = Values();
        values[attribute] = value;
        Assert.Throws<InvalidDataException>(() =>
            OverloadRules.Define(
                399,
                3002,
                ModuleRack.High,
                values,
                [new(DogmaAttributeId.Duration, DogmaAttributeId.OverloadDurationBonus)]
            )
        );
    }

    [Fact]
    public void MissingDuplicateAndUnknownModifierInputsAreRejected()
    {
        var values = Values();
        OverloadBonus bonus = new(DogmaAttributeId.Duration, DogmaAttributeId.OverloadDurationBonus);
        Assert.Throws<InvalidDataException>(() => OverloadRules.Define(399, 3002, ModuleRack.High, values, []));
        Assert.Throws<InvalidDataException>(() => OverloadRules.Define(399, 3002, ModuleRack.High, values, [bonus, bonus]));
        Assert.Throws<InvalidDataException>(() => OverloadRules.Define(399, 3002, ModuleRack.Rig, values, [bonus]));
        values.Remove(DogmaAttributeId.OverloadDurationBonus);
        Assert.Throws<InvalidDataException>(() => OverloadRules.Define(399, 3002, ModuleRack.High, values, [bonus]));
        Assert.Throws<InvalidDataException>(() => OverloadRules.ThermodynamicsDamage(1, 6, -5));
        Assert.Throws<InvalidDataException>(() => OverloadRules.ThermodynamicsDamage(1, 1, 5));
        Assert.Throws<InvalidDataException>(() => OverloadRules.ThermodynamicsDamage(1, 5, -100));
        Assert.Throws<InvalidDataException>(() => OverloadRules.ThermodynamicsDamage(0, 5, -100));
    }

    [Fact]
    public void ProfilesRetainImmutableBindingsAndReadEffectiveBonusAttributesFromTheGraph()
    {
        var values = Values();
        List<OverloadBonus> bindings = [new(DogmaAttributeId.Duration, DogmaAttributeId.OverloadDurationBonus)];
        var profile = OverloadRules.Define(399, 3002, ModuleRack.High, values, bindings);
        bindings.Clear();
        Assert.Single(profile.Bonuses);
        List<DogmaModifier> modifiers =
        [
            .. OverloadRules.Modifiers(profile, 100, true),
            new(100, DogmaAttributeId.OverloadDurationBonus, DogmaOperation.PostMultiply, new DogmaValue.Constant(2), true),
        ];
        var graph = new DogmaGraph([new(100, values)], modifiers, new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>());
        Assert.Equal(7000, graph.Read(100, DogmaAttributeId.Duration));
        Assert.Equal(-15, values[DogmaAttributeId.OverloadDurationBonus]);
    }
}
