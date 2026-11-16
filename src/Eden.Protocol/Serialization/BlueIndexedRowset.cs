namespace Eden.Protocol.Serialization;

// Build 3662168 CIndexedRowset(header, columnName) subclasses dict. Carbon REDUCE's
// second iterator populates its mapping; __setstate__ is not a dictionary loader.
internal sealed record BlueIndexedRowset(BlueConstructor Header, string ColumnName, IReadOnlyDictionary<long, object?> Rows);
