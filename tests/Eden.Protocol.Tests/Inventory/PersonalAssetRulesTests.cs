using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Tests.Inventory;

public sealed class PersonalAssetRulesTests
{
    [Theory]
    [InlineData(PersonalAssetContainerGroup.Cargo)]
    [InlineData(PersonalAssetContainerGroup.SecureCargo)]
    [InlineData(PersonalAssetContainerGroup.AuditLogSecure)]
    [InlineData(PersonalAssetContainerGroup.Freight)]
    public void SearchIncludesOnlyReachableContainerContents(PersonalAssetContainerGroup group)
    {
        var container = Item(1, Station, InventoryFlag.Hangar) with { GroupId = (int)group, Singleton = true };
        var loose = Item(2, Station, InventoryFlag.Hangar);
        var locked = Item(3, container.Id, InventoryFlag.Locked);
        var unlocked = Item(4, container.Id, InventoryFlag.Unlocked);
        var ordinary = Item(5, container.Id, InventoryFlag.AutoFit);
        var ship = Item(6, Station, InventoryFlag.Hangar) with { CategoryId = (int)InventoryCategory.Ship, Singleton = true };
        var cargo = Item(7, ship.Id, InventoryFlag.Cargo);
        var fitted = Item(8, ship.Id, InventoryFlag.HighSlot0);
        var orphan = Item(9, 999, InventoryFlag.Unlocked);
        var invalidFlag = Item(10, container.Id, InventoryFlag.Cargo);
        var remote = Item(11, Station + 1, InventoryFlag.Hangar);
        var packaged = container with { Id = 12, Singleton = false };
        var hidden = Item(13, packaged.Id, InventoryFlag.Unlocked);
        InventoryItem[] all =
        [
            container,
            loose,
            locked,
            unlocked,
            ordinary,
            ship,
            cargo,
            fitted,
            orphan,
            invalidFlag,
            remote,
            packaged,
            hidden,
        ];
        HashSet<long> stations = [Station];
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 12 }, PersonalAssetRules.Select(all, stations, true).Select(i => i.Id));
        Assert.Equal(new long[] { 1, 2, 6, 12 }, PersonalAssetRules.Select(all, stations, false).Select(i => i.Id));
        Assert.Empty(PersonalAssetRules.Select(all, new HashSet<long>(), true));
    }

    private const long Station = 60003760;

    private static InventoryItem Item(long id, long location, InventoryFlag flag) =>
        new(id, 1, 90000001, location, (int)flag, 20, 1, (int)InventoryCategory.Module, 1);
}
