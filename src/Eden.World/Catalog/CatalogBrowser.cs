using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;

namespace Eden.World.Catalog;

public sealed class CatalogBrowser(CatalogProvider catalog)
{
    private static readonly FrozenDictionary<string, ICatalogSource> s_sources = CatalogSources.All.ToFrozenDictionary(
        source => source.Name,
        StringComparer.Ordinal
    );

    public IReadOnlyList<CatalogDatasetDescriptor> Datasets => CatalogSchemas.Report.Datasets;

    public CatalogDatasetDescriptor Describe(string dataset) => Source(dataset).Descriptor;

    public CatalogBrowsePage Page(string dataset, string? after = null, int limit = 50, string? search = null, string? key = null) =>
        Source(dataset).Browse(catalog, after, limit, search, key);

    public ImmutableArray<CatalogRelatedRecord> Relationships(string dataset, CatalogStoredRecord record)
    {
        var links = ImmutableArray.CreateBuilder<CatalogRelatedRecord>();
        foreach (var relationship in Describe(dataset).Relationships)
        {
            foreach (var value in Values(record.Payload, relationship.Path.Split('/').Skip(1).ToArray()))
            {
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }
                string key =
                    CatalogSchemas.Get(relationship.Target).KeySchema.Kind == "string"
                        ? JsonSerializer.Serialize(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
                        : value.GetRawText();
                links.Add(new(relationship.Path, relationship.Target, key));
            }
        }
        return links.Distinct().ToImmutableArray();
    }

    private static ICatalogSource Source(string dataset) =>
        s_sources.GetValueOrDefault(dataset) ?? throw new InvalidDataException("Unknown catalog dataset.");

    private static IEnumerable<JsonElement> Values(JsonElement value, string[] path)
    {
        if (path.Length == 0)
        {
            yield return value;
            yield break;
        }
        IEnumerable<JsonElement> children =
            path[0] == "*"
                ? value.ValueKind switch
                {
                    JsonValueKind.Array => value.EnumerateArray(),
                    JsonValueKind.Object => value.EnumerateObject().Select(property => property.Value),
                    _ => [],
                }
            : value.ValueKind == JsonValueKind.Object && value.TryGetProperty(path[0], out var child) ? [child]
            : [];
        foreach (var descendant in children)
        {
            foreach (var found in Values(descendant, path[1..]))
            {
                yield return found;
            }
        }
    }
}
