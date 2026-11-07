using System.Collections.Immutable;

namespace Eden.Data.Catalog;

public sealed record CatalogRow<TKey, TValue>(TKey Key, TValue Value);

public sealed record CatalogPage<TKey, TValue>(ImmutableArray<CatalogRow<TKey, TValue>> Rows, string? NextCursor);

public sealed record CatalogItemDefinition(
    int Id,
    int GroupId,
    int CategoryId,
    string Name,
    double? Mass,
    double? Radius,
    double? Capacity
);
