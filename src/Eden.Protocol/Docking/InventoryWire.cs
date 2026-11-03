using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

internal sealed record InventoryMoveRequest(IReadOnlyList<long> ItemIds, long SourceId, int FlagId, int? Quantity);

internal sealed record InventoryRow(
    long Id,
    int TypeId,
    long OwnerId,
    long LocationId,
    int FlagId,
    int Quantity,
    int GroupId,
    int CategoryId,
    string CustomInfo = ""
) : IBlueWritable
{
    public static InventoryRow From(InventoryItem item) =>
        new(
            item.Id,
            item.TypeId,
            item.OwnerId,
            item.LocationId,
            item.FlagId,
            item.Singleton ? InventoryDefinitions.AssembledQuantity : item.Quantity,
            item.GroupId,
            item.CategoryId
        );

    public object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                InventoryWire.Descriptor(),
                new object?[] { Id, TypeId, OwnerId, LocationId, FlagId, Quantity, GroupId, CategoryId, CustomInfo },
            ]
        );
}

internal static class InventoryWire
{
    // Build 3662168 inventory header: nine physical columns plus two virtual getters.
    private static readonly (string Name, BlueColumnType Type)[] s_columns =
    [
        ("itemID", BlueColumnType.Int64),
        ("typeID", BlueColumnType.Int32),
        ("ownerID", BlueColumnType.Int32),
        ("locationID", BlueColumnType.Int64),
        ("flagID", BlueColumnType.Int16),
        ("quantity", BlueColumnType.Int32),
        ("groupID", BlueColumnType.Int32),
        ("categoryID", BlueColumnType.Int32),
        ("customInfo", BlueColumnType.String),
    ];

    internal static BlueConstructor Descriptor() =>
        new(
            "blue.DBRowDescriptor",
            [s_columns.Select(column => (object?)new object?[] { column.Name, (long)column.Type }).ToArray()],
            BlueRowDescriptor.InventoryVirtualColumns()
        );
}
