using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.World.Catalog;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CatalogSchemaTests
{
    [Fact]
    public void EveryPinnedDatasetHasProvenanceSchemaAndTypedKeys()
    {
        var report = CatalogSchemas.Report;
        Assert.Equal(3662168, report.Build);
        Assert.Equal(442, report.DatasetCount);
        Assert.Equal(4_913_627, report.RecordCount);
        Assert.Equal(0, report.UnresolvedFields);
        var sources = typeof(CatalogData)
            .GetNestedTypes()
            .SelectMany(group => group.GetFields())
            .Select(field => Assert.IsAssignableFrom<ICatalogSource>(field.GetValue(null)))
            .ToArray();
        Assert.Equal(report.DatasetCount, sources.Length);
        Assert.Equal(report.DatasetCount, sources.Select(source => source.Name).Distinct().Count());
        Assert.All(
            sources,
            source =>
            {
                Assert.Equal(source.Name, source.Descriptor.Name);
                Assert.NotEmpty(source.Descriptor.Source);
                Assert.Equal(64, source.Descriptor.Sha256.Length);
                Assert.True(source.KeyType == typeof(long) || source.KeyType == typeof(string) || source.KeyType == typeof(CatalogAbsent));
            }
        );
        Assert.Equal(typeof(string), CatalogData.Client.Blueprints.KeyType);
        Assert.Equal(typeof(long), CatalogData.Sde.Blueprints.KeyType);
        Assert.Equal("absent", CatalogSchemas.Get("client/skilltags").ValueSchema.Kind);
        Assert.Contains(
            CatalogData.Client.Blueprints.Descriptor.Relationships,
            relationship => relationship.Path == "/activities/manufacturing/products/*/typeID" && relationship.Target == "client/types"
        );
    }

    [Theory]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    [InlineData("0.125")]
    [InlineData("\"named value\"")]
    public void MixedCatalogValuesRoundTripWithoutLosingIntegerPrecision(string json)
    {
        var value = JsonSerializer.Deserialize<ClientAtomsParametersInputsItemDefaultValueChoice>(json);
        Assert.NotNull(value);
        Assert.Equal(json, JsonSerializer.Serialize(value));
    }

    [Fact]
    public void PinnedEmptyFieldsAndUnknownMembersRejectInventedData()
    {
        Assert.Equal(default, JsonSerializer.Deserialize<CatalogAbsent>("null"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogAbsent>("1"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ClientTypes>("{\"_unexpected\":1}"));
    }

    [Fact]
    public void GeneratedCatalogModelsHaveNoUntypedJsonProperties()
    {
        var properties = typeof(ClientTypes)
            .Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(ClientTypes).Namespace)
            // The handwritten catalog browser returns a raw validated payload so
            // arbitrary datasets can be displayed. It is not a generated source model.
            .Where(type => type != typeof(CatalogStoredRecord))
            .SelectMany(type => type.GetProperties());
        foreach (var property in properties)
        {
            AssertTyped(property.PropertyType);
        }
    }

    [Fact]
    public void ParsedCatalogCollectionsCannotBeMutatedThroughAnotherReference()
    {
        var dogma = JsonSerializer.Deserialize<ClientTypedogma>("{\"dogmaEffects\":[{\"effectID\":12}]}")!;
        var effects = dogma.DogmaEffects!.Value;
        var modified = effects.SetItem(0, new ClientTypedogmaDogmaEffectsItem { EffectID = 16 });
        Assert.Equal(12, dogma.DogmaEffects.Value[0].EffectID);
        Assert.Equal(16, modified[0].EffectID);
        Assert.Equal(typeof(ImmutableArray<ClientTypedogmaDogmaEffectsItem>), effects.GetType());
    }

    private static void AssertTyped(Type type)
    {
        Assert.NotEqual(typeof(JsonElement), type);
        Assert.NotEqual(typeof(object), type);
        if (type.HasElementType)
        {
            AssertTyped(type.GetElementType()!);
        }
        foreach (var argument in type.GetGenericArguments())
        {
            AssertTyped(argument);
        }
    }
}
