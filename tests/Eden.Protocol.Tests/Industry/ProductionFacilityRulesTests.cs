using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Tests.Industry;

public sealed class ProductionFacilityRulesTests
{
    [Theory]
    [InlineData(HullClass.Carrier)]
    [InlineData(HullClass.Dreadnought)]
    [InlineData(HullClass.LancerDreadnought)]
    [InlineData(HullClass.ForceAuxiliary)]
    [InlineData(HullClass.CapitalIndustrial)]
    [InlineData(HullClass.CommandCarrier)]
    public void CapitalsNeedLowOrNullSecurityAndRejectMissingSecurity(HullClass group)
    {
        Assert.True(ProductionFacilityRules.CanManufacture(InventoryCategory.Ship, (int)group, 0.449999));
        Assert.True(ProductionFacilityRules.CanManufacture(InventoryCategory.Ship, (int)group, -1));
        Assert.False(ProductionFacilityRules.CanManufacture(InventoryCategory.Ship, (int)group, 0.45));
        Assert.False(ProductionFacilityRules.CanManufacture(InventoryCategory.Ship, (int)group, double.NaN));
    }

    [Theory]
    [InlineData(HullClass.Titan)]
    [InlineData(HullClass.Supercarrier)]
    public void SupercapitalsCannotBeBuiltAtNpcStations(HullClass group)
    {
        Assert.False(ProductionFacilityRules.CanManufacture(InventoryCategory.Ship, (int)group, -1));
    }

    [Theory]
    [InlineData(HullClass.Freighter)]
    [InlineData(HullClass.JumpFreighter)]
    [InlineData(HullClass.IndustrialCommand)]
    [InlineData(HullClass.StrategicCruiser)]
    public void OrdinaryHullProductionRetainsHighSecurityAccess(HullClass group)
    {
        Assert.True(ProductionFacilityRules.CanManufacture(InventoryCategory.Ship, (int)group, 1));
    }
}
