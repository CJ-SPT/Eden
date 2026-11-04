using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

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
) : BlueModel
{
    public static InventoryRow From(InventoryItem item) =>
        new(
            item.Id,
            item.TypeId,
            item.OwnerId,
            item.LocationId,
            item.FlagId,
            item.Singleton
                ? item.TypeId == CourierRules.PackageType
                    ? -CourierRules.VolumeUnits(item.Volume)
                    : InventoryWireDefinitions.AssembledQuantity
                : item.Quantity,
            item.GroupId,
            item.CategoryId
        );

    public override object ToBlue() =>
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

    internal static BlueConstructor Descriptor() => BlueRows.Descriptor(s_columns, inventoryVirtuals: true);
}
