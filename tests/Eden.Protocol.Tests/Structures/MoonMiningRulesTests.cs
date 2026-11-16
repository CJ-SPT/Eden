using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Reprocessing;
using Eden.Gameplay.Structures;

namespace Eden.Protocol.Tests.Structures;

public sealed class MoonMiningRulesTests
{
    private const int BaseOreTypeAttribute = 2711;
    private const int AsteroidCategory = 25;
    private const int UbiquitousGroup = 1884;
    private const int FirstMoonGroup = 1920;
    private const int LastMoonGroup = 1923;
    private const int FirstRawMoonMaterial = 16633;
    private const int LastRawMoonMaterial = 16653;
    private const int UnusedRawMoonMaterial = 16645;
    private const int FirstMoonId = 40_000_000;
    private const int SampleMoonCount = 1_000;

    private sealed record AttributeValue(int Id, double Value);

    private sealed record OreDefinition(int Id, int GroupId, int CategoryId, IReadOnlyList<AttributeValue> Attributes);

    private sealed record CatalogRecord(string Dataset, int Key, JsonElement Data);

    private sealed record CatalogFixture(int Build, IReadOnlyList<OreDefinition> Types, IReadOnlyList<CatalogRecord> Records);

    [Theory]
    [InlineData(40000002, 45510, 45492)]
    [InlineData(40000021, 45506, 45493)]
    [InlineData(40000023, 45513, 45492)]
    [InlineData(40000039, 45512, 45490)]
    [InlineData(40000051, 45511, 45493)]
    public void CorrectedRareAndExceptionalSlotsPreserveSeedAndShares(int moonId, int primary, int secondary)
    {
        MoonOreShare[] expected = [new(primary, 70), new(secondary, 30)];
        Assert.Equal(expected, MoonMiningRules.Composition(moonId, 0.3));
        Assert.Equal(expected, MoonMiningRules.Composition(moonId, -0.8));
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0)]
    [InlineData(-0.8)]
    public void CompositionSuppliesEveryPinnedMoonOreAndRawReactionMaterial(double security)
    {
        using var stream = typeof(MoonMiningRulesTests).Assembly.GetManifestResourceStream(
            "Eden.Protocol.Tests.Fixtures.CompressionCatalog.json"
        )!;
        var fixture = JsonSerializer.Deserialize<CatalogFixture>(stream, JsonSerializerOptions.Web)!;
        Assert.Equal(3662168, fixture.Build);
        var ores = fixture
            .Types.Where(type =>
                type.CategoryId == AsteroidCategory
                && (type.GroupId == UbiquitousGroup || type.GroupId is >= FirstMoonGroup and <= LastMoonGroup)
                && type.Attributes.Any(attribute => attribute.Id == BaseOreTypeAttribute && attribute.Value == type.Id)
            )
            .ToDictionary(type => type.Id);
        Assert.Equal(20, ores.Count);
        var compositions = Enumerable
            .Range(FirstMoonId, SampleMoonCount)
            .Select(moonId => MoonMiningRules.Composition(moonId, security))
            .ToArray();
        var generated = compositions.SelectMany(shares => shares).Select(share => share.TypeId).Distinct().Order().ToArray();
        Assert.Equal(ores.Keys.Order(), generated);
        Assert.Equal(ores.Keys.Order(), MoonMiningRules.OreTypes.Order());
        Assert.All(compositions, shares => Assert.Equal(100, shares.Sum(share => share.Percentage)));

        var recovered = new HashSet<int>();
        foreach (int oreId in generated)
        {
            var definition = fixture
                .Records.Single(record => record.Dataset == "client/types" && record.Key == oreId)
                .Data.Deserialize<ClientTypes>()!;
            Assert.Equal(1L, definition.Published);
            Assert.Equal(10d, definition.Volume);
            var recipe = fixture
                .Records.Single(record => record.Dataset == "client/typematerials" && record.Key == oreId)
                .Data.Deserialize<ClientTypematerials>()!;
            var materials = recipe
                .Materials!.Value.Select(material => new ReprocessingMaterial(
                    (int)material.MaterialTypeID!.Value,
                    material.Quantity!.Value
                ))
                .ToArray();
            int portion = (int)definition.PortionSize!.Value;
            var quote = ReprocessingRules.Quote(new(1, oreId, portion, portion, 0.5, [], materials));
            Assert.Equal(portion, quote.QuantityToProcess);
            Assert.All(quote.Recoverables, material => Assert.True(material.Quantity > 0));
            recovered.UnionWith(quote.Recoverables.Select(material => material.TypeId));
        }
        var expectedMaterials = Enumerable
            .Range(FirstRawMoonMaterial, LastRawMoonMaterial - FirstRawMoonMaterial + 1)
            .Where(typeId => typeId != UnusedRawMoonMaterial);
        Assert.Equal(expectedMaterials, recovered.Where(typeId => typeId >= FirstRawMoonMaterial).Order());
    }

    [Fact]
    public void HighSecurityMoonsRemainUbiquitousAndIneligibleInputsHaveNoComposition()
    {
        int[] ubiquitousOres = [45490, 45491, 45492, 45493];
        foreach (double security in new[] { StructureRules.HighSecurityBoundary, 0.5, Math.BitDecrement(0.55) })
        {
            var compositions = Enumerable
                .Range(FirstMoonId, SampleMoonCount)
                .Select(moonId => MoonMiningRules.Composition(moonId, security))
                .ToArray();
            Assert.Equal(ubiquitousOres, compositions.SelectMany(shares => shares).Select(share => share.TypeId).Distinct().Order());
            Assert.All(compositions, shares => Assert.Equal(100, shares.Sum(share => share.Percentage)));
        }
        Assert.Empty(MoonMiningRules.Composition(0, 0.3));
        Assert.Empty(MoonMiningRules.Composition(-1, 0.3));
        foreach (double security in new[] { 0.55, 1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Empty(MoonMiningRules.Composition(FirstMoonId, security));
        }
    }
}
