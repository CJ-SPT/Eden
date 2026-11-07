using System.Collections.Frozen;
using System.Text.Json;
using Eden.Data.Catalog;

namespace Eden.World.Catalog;

public static class CatalogSchemas
{
    private static readonly Lazy<CatalogSchemaReport> s_report = new(Load);
    private static readonly Lazy<FrozenDictionary<string, CatalogDatasetDescriptor>> s_datasets = new(() =>
        Report.Datasets.ToFrozenDictionary(dataset => dataset.Name, StringComparer.Ordinal)
    );

    public static CatalogSchemaReport Report => s_report.Value;

    public static CatalogDatasetDescriptor Get(string name) => s_datasets.Value[name];

    private static CatalogSchemaReport Load()
    {
        using var stream =
            typeof(CatalogSchemaReport).Assembly.GetManifestResourceStream("Eden.Data.Catalog.Schema.json")
            ?? throw new InvalidDataException("Missing generated catalog schema report.");
        var report =
            JsonSerializer.Deserialize<CatalogSchemaReport>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing catalog schema report.");
        if (report.DatasetCount != report.Datasets.Count || report.UnresolvedFields != 0)
        {
            throw new InvalidDataException("Incomplete generated catalog schemas.");
        }
        return report;
    }
}
