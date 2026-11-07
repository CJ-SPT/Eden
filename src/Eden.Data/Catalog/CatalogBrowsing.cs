using System.Collections.Immutable;
using System.Text.Json;

namespace Eden.Data.Catalog;

// Administration is a dynamic display boundary. Preserve source presence/null and exact numeric spelling.
// The corresponding generated key and payload are validated before any stored record is exposed.
public sealed record CatalogStoredRecord(string Key, JsonElement Payload);

public sealed record CatalogBrowsePage(ImmutableArray<CatalogStoredRecord> Rows, string? NextCursor);

public sealed record CatalogRelatedRecord(string Path, string Dataset, string Key);
