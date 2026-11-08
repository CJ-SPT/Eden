using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;

namespace Eden.Protocol.Tests.Industry;

public sealed class ManufacturingRulesTests
{
    private static ManufacturingInputs Inputs =>
        new(
            300,
            0,
            [new((int)IndustrySkill.Industry, DogmaAttributeId.ManufactureTimeMultiplier, -4, false)],
            [],
            [new(34, 27), new(35, 21)]
        );

    private static IndustryRequest Request => new(1, 1130, 60003760, 1, 215, 288, new Dictionary<int, int> { [34] = 27, [35] = 21 });

    [Fact]
    public void CalculatesCatalogRecipeAndTrainedDurationWithoutStorage()
    {
        var plan = ManufacturingRules.Plan(Request, Inputs);
        Assert.Equal(288, plan.DurationSeconds);
        Assert.Equal(27, plan.Materials[34]);
        Assert.Equal(21, plan.Materials[35]);
    }

    [Theory]
    [InlineData(288.5, 289)]
    [InlineData(289.5, 290)]
    public void RoundsManufacturingDurationLikePinnedPython(double seconds, long expected)
    {
        var plan = ManufacturingRules.Plan(Request with { QuotedSeconds = expected }, Inputs with { BaseSeconds = seconds, Bonuses = [] });
        Assert.Equal(expected, plan.DurationSeconds);
    }

    [Fact]
    public void RejectsStaleQuotesAndOccupiedSlotsWithoutChangingInputs()
    {
        var request = Request;
        var inputs = Inputs;
        Assert.Throws<InvalidDataException>(() => ManufacturingRules.Plan(request with { QuotedSeconds = 300 }, inputs));
        Assert.Throws<InvalidDataException>(() =>
            ManufacturingRules.Plan(request with { QuotedMaterials = new Dictionary<int, int> { [34] = 1 } }, inputs)
        );
        Assert.Throws<InvalidDataException>(() => ManufacturingRules.Plan(request, inputs with { InstalledJobs = 1 }));
        Assert.Equal(27, request.QuotedMaterials[34]);
        Assert.Equal(21, request.QuotedMaterials[35]);
        Assert.Equal(0, inputs.InstalledJobs);
    }
}
