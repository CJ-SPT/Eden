namespace Eden.Data.Catalog;

public sealed record CatalogShapeDescriptor(
    string Kind,
    bool AllowsNull,
    bool WideInteger,
    IReadOnlyDictionary<string, CatalogShapeDescriptor>? Fields = null,
    CatalogShapeDescriptor? Element = null,
    IReadOnlyDictionary<string, CatalogShapeDescriptor>? Alternatives = null
);

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
    IReadOnlyList<CatalogRelationshipDescriptor> Relationships
);

public sealed record CatalogSchemaReport(
    int Build,
    int DatasetCount,
    long RecordCount,
    int UnresolvedFields,
    IReadOnlyList<CatalogDatasetDescriptor> Datasets
);
