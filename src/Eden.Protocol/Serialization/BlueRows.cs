namespace Eden.Protocol.Serialization;

internal static class BlueRows
{
    internal static BlueConstructor Descriptor(IReadOnlyList<(string Name, BlueColumnType Type)> columns, bool inventoryVirtuals = false) =>
        new(
            "blue.DBRowDescriptor",
            [columns.Select(column => (object?)new object?[] { column.Name, (long)column.Type }).ToArray()],
            inventoryVirtuals ? BlueRowDescriptor.InventoryVirtualColumns() : null
        );
}
