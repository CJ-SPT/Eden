using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Docking;

public sealed class TypedInventoryTests
{
    [Fact]
    public void InventoryRowsKeepTheNinePhysicalFieldsAndVirtualHeader()
    {
        InventoryItem item = new(1000000000001, 601, 90000001, 60003760, 4, 1, 237, 6, 100, true);
        var row = InventoryRow.From(item);
        Assert.Equal(-1, row.Quantity);
        var descriptor = new BlueConstructor(
            "blue.DBRowDescriptor",
            [
                new object?[]
                {
                    new object?[] { "itemID", 20 },
                    new object?[] { "typeID", 3 },
                    new object?[] { "ownerID", 3 },
                    new object?[] { "locationID", 20 },
                    new object?[] { "flagID", 2 },
                    new object?[] { "quantity", 3 },
                    new object?[] { "groupID", 3 },
                    new object?[] { "categoryID", 3 },
                    new object?[] { "customInfo", 129 },
                },
            ],
            BlueRowDescriptor.InventoryVirtualColumns()
        );
        var original = new BlueConstructor(
            "blue.DBRow",
            [descriptor, new object?[] { 1000000000001L, 601, 90000001L, 60003760L, 4, -1, 237, 6, "" }]
        );
        Assert.Equal(BlueMarshal.Encode(original), BlueMarshal.Encode(row));
    }

    [Fact]
    public void DogmaInstancesKeepIntegerPlaceholdersAndFloatingResources()
    {
        Assert.Equal(BlueMarshal.Encode(new object?[] { 42L, 1, 0, 0, 0, 0, 0, 0 }), BlueMarshal.Encode(new DogmaInstanceState(42, true)));
        Assert.Equal(
            BlueMarshal.Encode(new object?[] { 42L, 0, 0d, 15d, 0, 2d, 50d, 0 }),
            BlueMarshal.Encode(new DogmaInstanceState(42, HullDamage: 0, Capacitor: 15, ArmorDamage: 2, Shield: 50))
        );
    }

    [Fact]
    public void ActiveEffectsKeepIndefiniteDurationAndChargeKeys()
    {
        var online = new DogmaActiveEffect(10, 20, 30, null, DogmaEffect.Online, 40, null, 0);
        Assert.Equal(
            BlueMarshal.Encode(new object?[] { 10L, 20L, 30L, null, null, Array.Empty<object?>(), 16, 40L, -1, 0 }),
            BlueMarshal.Encode(online)
        );
        var timed = online with { Effect = DogmaEffect.ProjectileFired, TargetId = 50, DurationMilliseconds = 5000, Repeats = 2 };
        Assert.Equal(
            BlueMarshal.Encode(new object?[] { 10L, 20L, 30L, 50L, null, Array.Empty<object?>(), 34, 40L, 5000d, 2L }),
            BlueMarshal.Encode(timed)
        );
        Assert.Equal(BlueMarshal.Encode(new object?[] { 30L, 27, 222 }), BlueMarshal.Encode(new DogmaItemKey.Charge(30, 27, 222)));
    }

    [Fact]
    public void ItemNotificationsPreserveColumnKeysAndPreviousNumericValues()
    {
        InventoryRow row = new(42, 34, 20, 30, 5, 10, 18, 4);
        ItemChanged notification = new(
            row,
            new Dictionary<InventoryColumn, long>
            {
                [InventoryColumn.Location] = 60,
                [InventoryColumn.Flag] = 4,
                [InventoryColumn.Quantity] = 30,
            }
        );
        Assert.Equal(
            BlueMarshal.Encode(
                new object?[]
                {
                    row,
                    new Dictionary<long, object?>
                    {
                        [3] = 60L,
                        [4] = 4,
                        [5] = 30,
                    },
                    null,
                }
            ),
            BlueMarshal.Encode(notification.Arguments)
        );
        Assert.Equal(30, notification.Previous[InventoryColumn.Quantity]);
    }
}
