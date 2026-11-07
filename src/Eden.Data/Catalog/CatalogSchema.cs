using System.Collections.Immutable;

namespace Eden.Data.Catalog;

public sealed record CatalogFieldCompatibility(bool Editable, string Reason)
{
    public static CatalogFieldCompatibility InstalledSource { get; } =
        new(false, "Installed source fields and identities require a compatible client synchronization contract.");
}

public sealed record CatalogShapeDescriptor(
    string Kind,
    bool AllowsNull,
    bool WideInteger,
    ImmutableDictionary<string, CatalogShapeDescriptor>? Fields = null,
    CatalogShapeDescriptor? Element = null,
    ImmutableDictionary<string, CatalogShapeDescriptor>? Alternatives = null
)
{
    public CatalogFieldCompatibility Compatibility => CatalogFieldCompatibility.InstalledSource;
}

public sealed record CatalogRelationshipDescriptor(string Path, string Target);

public sealed record CatalogDatasetDescriptor(
    string Name,
    string Source,
    string Sha256,
    long Records,
    string KeyType,
    string ValueType,
    CatalogShapeDescriptor KeySchema,
    CatalogShapeDescriptor ValueSchema,
    ImmutableArray<CatalogRelationshipDescriptor> Relationships
);

public sealed record CatalogSchemaReport(
    int Build,
    int DatasetCount,
    long RecordCount,
    int UnresolvedFields,
    ImmutableArray<CatalogDatasetDescriptor> Datasets
);
