using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;

namespace Eden.Protocol.Tests.Industry;

public sealed class ScienceRulesTests
{
    private static IndustryRequest Request(IndustryActivity activity, int runs, double seconds, int licensed = 0) =>
        new(1, 1130, 60003760, runs, null, seconds, new Dictionary<int, int>(), Activity: activity, LicensedRuns: licensed);

    private static ScienceInputs Inputs => new(new(), 105, 600, 0, [], []);

    [Theory]
    [InlineData(IndustryActivity.ResearchMaterial, 1, 105, 1, 0)]
    [InlineData(IndustryActivity.ResearchTime, 1, 105, 0, 2)]
    [InlineData(IndustryActivity.ResearchMaterial, 2, 250, 2, 0)]
    [InlineData(IndustryActivity.ResearchTime, 2, 250, 0, 4)]
    public void ResearchUsesIncrementalPinnedLevelTimes(IndustryActivity activity, int runs, long seconds, int me, int te)
    {
        var plan = ScienceRules.Plan(Request(activity, runs, seconds), Inputs);
        Assert.Equal(seconds, plan.DurationSeconds);
        Assert.Equal(me, plan.MaterialEfficiency);
        Assert.Equal(te, plan.TimeEfficiency);
        Assert.Equal(seconds / 105d * IndustryDefinitions.ScienceCostPercentage, plan.CostMultiplier, 10);
    }

    [Fact]
    public void ExistingResearchStartsAtTheCurrentLevelAndUsesOnlyMatchingTimeBonuses()
    {
        var inputs = Inputs with
        {
            Blueprint = new(1, 2),
            Bonuses =
            [
                new(3409, DogmaAttributeId.MineralNeedResearchSpeed, -5, false),
                new(3380, DogmaAttributeId.ManufactureTimeMultiplier, -20, false),
            ],
        };
        var plan = ScienceRules.Plan(Request(IndustryActivity.ResearchMaterial, 1, 138), inputs);
        Assert.Equal(2, plan.MaterialEfficiency);
        Assert.Equal(2, plan.TimeEfficiency);
        Assert.Equal(29 / 21d * IndustryDefinitions.ScienceCostPercentage, plan.CostMultiplier, 10);
    }

    [Fact]
    public void CopyingMultipliesCopiesLicensedRunsTimeAndMaterialsAndPreservesEfficiency()
    {
        var request = Request(IndustryActivity.Copying, 2, 1440, 3) with { QuotedMaterials = new Dictionary<int, int> { [34] = 12 } };
        var plan = ScienceRules.Plan(request, Inputs with { BaseSeconds = 240, Blueprint = new(8, 16), Materials = [new(34, 2)] });
        Assert.Equal(3, plan.OutputRuns);
        Assert.Equal(8, plan.MaterialEfficiency);
        Assert.Equal(16, plan.TimeEfficiency);
        Assert.Equal(12, plan.Materials[34]);
        Assert.Equal(0.12, plan.CostMultiplier, 10);
    }

    [Fact]
    public void InventionUsesSkillsAndDecryptorsWithoutInheritingSourceResearch()
    {
        var plan = ScienceRules.Plan(
            Request(IndustryActivity.Invention, 2, 210),
            Inputs with
            {
                Blueprint = new(10, 20, 2),
                BaseProbability = 0.34,
                SkillProbabilityBonus = 0.2,
                InventionOutputRuns = 10,
                Decryptor = new(1.2, 2, 10, 1),
            }
        );
        Assert.Equal(0.4896, plan.Probability, 10);
        Assert.Equal(4, plan.MaterialEfficiency);
        Assert.Equal(14, plan.TimeEfficiency);
        Assert.Equal(11, plan.OutputRuns);
        Assert.Equal(0.04, plan.CostMultiplier, 10);
        Assert.Equal(2, ScienceRules.SuccessfulRuns(plan.Probability, [0, 0.4, 0.5, 0.99]));
        Assert.Equal(0, ScienceRules.SuccessfulRuns(0, [0, 0.1]));
        Assert.Equal(2, ScienceRules.SuccessfulRuns(1, [0, 0.99]));
    }

    [Theory]
    [InlineData(1.2, 2, 10, 1)]
    [InlineData(1.8, -1, 4, 4)]
    [InlineData(0.6, -2, 2, 9)]
    [InlineData(1.5, 1, -2, 3)]
    [InlineData(1.1, 3, 6, 0)]
    [InlineData(1.0, 1, 8, 2)]
    [InlineData(1.9, 1, -2, 2)]
    [InlineData(0.9, 2, 0, 7)]
    public void AllEightPinnedGenericDecryptorProfilesAffectOnlyChanceAndOutput(double probability, int me, int te, int runs)
    {
        var plan = ScienceRules.Plan(
            Request(IndustryActivity.Invention, 1, 105),
            Inputs with
            {
                Blueprint = new(0, 0, 1),
                BaseProbability = 0.34,
                InventionOutputRuns = 10,
                Decryptor = new(probability, me, te, runs),
            }
        );
        Assert.Equal(0.34 * probability, plan.Probability, 10);
        Assert.Equal(2 + me, plan.MaterialEfficiency);
        Assert.Equal(4 + te, plan.TimeEfficiency);
        Assert.Equal(10 + runs, plan.OutputRuns);
        Assert.Equal(105, plan.DurationSeconds);
    }

    [Theory]
    [InlineData(IndustryActivity.ResearchMaterial)]
    [InlineData(IndustryActivity.ResearchTime)]
    [InlineData(IndustryActivity.Copying)]
    [InlineData(IndustryActivity.Invention)]
    public void AllScienceActivitiesUseOneSharedSlotPool(IndustryActivity activity)
    {
        var request = Request(activity, 1, 105, activity == IndustryActivity.Copying ? 1 : 0);
        var inputs = Inputs with { Blueprint = activity == IndustryActivity.Invention ? new(0, 0, 1) : new(), InstalledJobs = 1 };
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(request, inputs));
        var expanded = inputs with { Bonuses = [new(3406, DogmaAttributeId.MaximumLaboratorySlots, 1, true)] };
        Assert.Equal(105, ScienceRules.Plan(request, expanded).DurationSeconds);
    }

    [Fact]
    public void RejectsCopyOriginalConfusionOverResearchOverrunsAndStaleQuotes()
    {
        var request = Request(IndustryActivity.ResearchMaterial, 1, 105);
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(request, Inputs with { Blueprint = new(0, 0, 1) }));
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(request, Inputs with { Blueprint = new(10, 0) }));
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(request with { Runs = 11 }, Inputs));
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(request with { QuotedSeconds = double.NaN }, Inputs));
        Assert.Throws<InvalidDataException>(() =>
            ScienceRules.Plan(request with { QuotedMaterials = new Dictionary<int, int> { [34] = 1 } }, Inputs)
        );
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(Request(IndustryActivity.Copying, 1, 105, 601), Inputs));
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(Request(IndustryActivity.Invention, 1, 105), Inputs));
        Assert.Throws<InvalidDataException>(() =>
            ScienceRules.Plan(Request(IndustryActivity.Invention, 2, 210), Inputs with { Blueprint = new(0, 0, 1) })
        );
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(Request(IndustryActivity.Copying, 501, 52605, 1), Inputs));
    }

    [Fact]
    public void ResearchedManufacturingRoundsTheWholeJobAndRespectsCopyRuns()
    {
        var request = Request(IndustryActivity.Manufacturing, 3, 720) with
        {
            QuotedMaterials = new Dictionary<int, int> { [34] = 73, [35] = 3 },
        };
        var inputs = new ManufacturingInputs(300, 0, [], [], [new(34, 27), new(35, 1)], new(10, 20, 3));
        var plan = ManufacturingRules.Plan(request, inputs);
        Assert.Equal(73, plan.Materials[34]);
        Assert.Equal(3, plan.Materials[35]);
        Assert.Equal(720, plan.DurationSeconds);
        Assert.Throws<InvalidDataException>(() => ManufacturingRules.Plan(request with { Runs = 4 }, inputs));
    }
}
