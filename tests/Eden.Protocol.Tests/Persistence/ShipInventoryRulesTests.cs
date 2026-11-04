using System.Reflection;
using System.Text.Json;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ShipInventoryRulesTests
{
    [Fact]
    public void WreckShipContentsRequireOwnershipAnOpenFlightAndTransferRange()
    {
        ShipInventoryAccess access = new(1, 2, 3, false);
        WreckContact contact = new(4, 5, 1, 6, 7, "Owned wreck", 8, SpacePosition.Zero, 1, 10, false);
        ShipWreckAccess wreck = new(contact, new(2560, 0, 0), 50);
        InventoryItem child = new(9, 587, 1, 4, (int)InventoryFlag.Cargo, 1, 25, 6, 27289, true);
        Assert.True(ShipStorageRules.CanRead(access, child, null, wreck));
        Assert.False(ShipStorageRules.CanRead(access, child, null, wreck with { ShipPosition = new(2561, 0, 0) }));
        Assert.False(ShipStorageRules.CanRead(access, child, null, wreck with { Wreck = contact with { OwnerId = 10 } }));
        Assert.False(ShipStorageRules.CanRead(access, child with { OwnerId = 10 }, null, wreck));
        Assert.False(ShipStorageRules.CanRead(access, child with { LocationId = 11 }, null, wreck));
        Assert.False(ShipStorageRules.CanRead(access with { Docked = true }, child, null, wreck));
        Assert.False(ShipStorageRules.CanRead(access, child, null));
    }

    private sealed record CargoProbe(int TypeId, int GroupId, int CategoryId);

    private sealed record NativeHold(int Flag, int Attribute, IReadOnlyList<int> AllowedTypes);

    private sealed record NativeCapacity(int Flag, int Attribute, double Capacity);

    private sealed record NativeVolume(
        int TypeId,
        int GroupId,
        double AssembledVolume,
        double PackagedVolume,
        IReadOnlyList<NativeCapacity> Holds
    );

    private sealed record NativeFixture(
        int Build,
        IReadOnlyList<CargoProbe> Items,
        IReadOnlyList<NativeHold> Holds,
        IReadOnlyList<NativeVolume> Volumes,
        IReadOnlyList<NativeVolume> OtherShipVolumes
    );

    private static NativeFixture Read()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.NativeShipInventory.json")!;
        return JsonSerializer.Deserialize<NativeFixture>(stream, JsonSerializerOptions.Web)!;
    }

    public static IEnumerable<object[]> Holds() => Read().Holds.Select(hold => new object[] { hold.Flag });

    public static IEnumerable<object[]> Hulls() => Read().Volumes.Select(hull => new object[] { hull.TypeId });

    [Theory]
    [MemberData(nameof(Holds))]
    public void CargoFiltersAgreeWithThePinnedClientForEveryProbe(int flag)
    {
        var native = Read();
        Assert.Equal(3662168, native.Build);
        var hold = Assert.IsType<ShipHoldDefinition>(ShipHoldRules.Definition(flag));
        var expected = native.Holds.Single(h => h.Flag == flag);
        Assert.Equal(expected.Attribute, (int)hold.CapacityAttribute);
        foreach (var cargo in native.Items)
        {
            var item = new InventoryItem(
                1,
                cargo.TypeId,
                2,
                3,
                4,
                1,
                cargo.GroupId,
                cargo.CategoryId,
                1,
                Singleton: hold.RequiresAssembledShip
            );
            Assert.True(
                expected.AllowedTypes.Contains(cargo.TypeId) == hold.Allows(item),
                $"Native flag {flag}, type {cargo.TypeId}: cargo filter mismatch."
            );
        }
    }

    [Theory]
    [MemberData(nameof(Hulls))]
    public void EveryHullHasItsNativePackagedVolume(int type)
    {
        var expected = Read().Volumes.Single(h => h.TypeId == type);
        Assert.Equal(expected.PackagedVolume, ShipVolumeRules.Packaged(type, (HullClass)expected.GroupId, expected.AssembledVolume));
        var attributes = expected.Holds.ToDictionary(h => (DogmaAttributeId)h.Attribute, h => h.Capacity);
        foreach (var native in expected.Holds)
        {
            var definition = Assert.IsType<ShipHoldDefinition>(ShipHoldRules.Definition(native.Flag));
            Assert.Equal(native.Attribute, (int)definition.CapacityAttribute);
            Assert.Equal(native.Capacity, ShipHoldRules.Capacity(native.Flag, attributes));
        }
    }

    [Fact]
    public void PackagedVolumesAlsoRespectTheNativeGroupsUsedByOtherMarketShips()
    {
        foreach (var ship in Read().OtherShipVolumes)
        {
            Assert.Equal(ship.PackagedVolume, ShipVolumeRules.Packaged(ship.TypeId, (HullClass)ship.GroupId, ship.AssembledVolume));
        }
    }

    [Fact]
    public void UnknownOrAbsentHoldsNeverFallBackToOrdinaryCargo()
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.CargoCapacity] = 10000,
            [DogmaAttributeId.SpecialGasHoldCapacity] = 500,
        };
        Assert.Equal(10000, ShipHoldRules.Capacity((int)InventoryFlag.Cargo, attributes));
        Assert.Equal(500, ShipHoldRules.Capacity((int)InventoryFlag.GasHold, attributes));
        Assert.Equal(0, ShipHoldRules.Capacity((int)InventoryFlag.IceHold, attributes));
        Assert.Equal(0, ShipHoldRules.Capacity(int.MaxValue, attributes));
        Assert.Null(ShipHoldRules.Definition(int.MaxValue));
    }

    [Fact]
    public void StoredShipCargoRetainsPermittedConsumablesButRejectsOtherCommoditiesAndImplants()
    {
        var probes = Read().Items;
        InventoryItem Item(int type)
        {
            var probe = probes.Single(p => p.TypeId == type);
            return new(1, type, 2, 3, (int)InventoryFlag.Cargo, 1, probe.GroupId, probe.CategoryId, 1);
        }
        foreach (int type in new[] { 215, 3898, 16273, 16275, 28668, 45586 })
        {
            ShipStorageRules.CheckContents([Item(type)], false);
        }
        foreach (
            int type in new[]
            {
                34,
                16272,
                3699,
                probes.First(p => p.CategoryId == (int)InventoryCategory.Implant && p.GroupId != (int)HoldCargoGroup.Booster).TypeId,
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => ShipStorageRules.CheckContents([Item(type)], false));
        }
        ShipStorageRules.CheckContents([Item(215) with { FlagId = (int)InventoryFlag.AmmoHold }], false);
    }

    [Fact]
    public void AssembledShipsRequirePersonalShipStorageAndCannotUseOrdinaryCargo()
    {
        var frigate = new InventoryItem(1, 587, 2, 3, 4, 1, (int)HullClass.Frigate, (int)InventoryCategory.Ship, 27289, Singleton: true);
        Assert.True(ShipHoldRules.Definition((int)InventoryFlag.ShipMaintenanceBay)!.Allows(frigate));
        var escape = ShipHoldRules.Definition((int)InventoryFlag.FrigateEscapeBay)!;
        Assert.True(escape.Allows(frigate));
        Assert.Equal(ShipHoldCapacityUnit.Ships, escape.CapacityUnit);
        Assert.Equal(1, escape.RequiredCapacity(frigate, 1));
        var maintenance = ShipHoldRules.Definition((int)InventoryFlag.ShipMaintenanceBay)!;
        Assert.Equal(ShipHoldCapacityUnit.CubicMeters, maintenance.CapacityUnit);
        Assert.Equal(27289, maintenance.RequiredCapacity(frigate, 1));
        Assert.False(ShipHoldRules.Definition((int)InventoryFlag.Cargo)!.Allows(frigate));
        Assert.True(ShipHoldRules.Definition((int)InventoryFlag.FleetHangar)!.Allows(frigate));
        Assert.False(ShipHoldRules.Definition((int)InventoryFlag.ShipMaintenanceBay)!.Allows(frigate with { Singleton = false }));
    }
}
