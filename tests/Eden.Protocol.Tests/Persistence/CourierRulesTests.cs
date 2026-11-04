using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CourierRulesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.01, 1)]
    [InlineData(0.07, 7)]
    [InlineData(0.071, 8)]
    [InlineData(35, 3500)]
    [InlineData(1200000, 120000000)]
    public void PackageVolumeAndNativeNegativeQuantityAgreeAtTheCentiCubicMetreBoundary(double volume, int units)
    {
        Assert.Equal(units, CourierRules.VolumeUnits(volume));
        InventoryItem item = new(
            1,
            CourierRules.PackageType,
            90000001,
            60003760,
            (int)InventoryFlag.Hangar,
            1,
            CourierRules.PackageGroup,
            CourierRules.PackageCategory,
            CourierRules.PackageVolume(volume),
            true
        );
        Assert.Equal(-units, InventoryRow.From(item).Quantity);
        Assert.Equal(item.Volume, -InventoryRow.From(item).Quantity / 100d);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1200000.01)]
    public void InvalidPackageVolumesRejectBeforeEncodingOrCapacityChecks(double volume) =>
        Assert.Throws<InvalidDataException>(() => CourierRules.VolumeUnits(volume));

    [Theory]
    [InlineData(1000, 0, 1, 10000, 10000)]
    [InlineData(100000000, 0, 1, 40000, 100000)]
    [InlineData(100000000, 5, 1, 30000, 100000)]
    [InlineData(100000000, 0, 28, 94000, 100000)]
    public void CourierFeesUseNativeCostMultiplierMinimumDepositAndNoSalesTax(
        long rewardIsk,
        int level,
        int days,
        long brokerIsk,
        long depositIsk
    )
    {
        var draft = CourierTestData.Draft(1) with { AssigneeId = null, RewardCents = rewardIsk * 100, ExpiryMinutes = days * 1440 };
        Assert.Equal(new ContractFees(brokerIsk * 100, depositIsk * 100, 0), ContractRules.Fees(draft, new(level, level, level, level)));
        Assert.Equal(
            new ContractFees(1000000, 0, 0),
            ContractRules.Fees(draft with { AssigneeId = CourierTestData.Hauler }, new(level, level, level, level))
        );
    }

    [Fact]
    public void CourierTermsRequireRealGoodsAValidDurationAndNoItemExchangePaymentFields()
    {
        var draft = CourierTestData.Draft(1);
        foreach (
            var invalid in new[]
            {
                draft with
                {
                    Courier = null,
                },
                draft with
                {
                    Items = [],
                },
                draft with
                {
                    RequestedItems = [new(3651, 1)],
                },
                draft with
                {
                    PriceCents = 1,
                },
                draft with
                {
                    Courier = draft.Courier! with { DestinationStationId = 0 },
                },
                draft with
                {
                    Courier = draft.Courier! with { DurationDays = 0 },
                },
                draft with
                {
                    Courier = draft.Courier! with { DurationDays = 29 },
                },
                draft with
                {
                    Courier = draft.Courier! with { CollateralCents = -1 },
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => ContractRules.Check(invalid));
        }
        ContractRules.Check(draft);
        Assert.Equal(TimeSpan.FromDays(28).Ticks + 1, CourierRules.Deadline(1, 28));
    }
}
