using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Industry;

public static class ProductionFacilityRules
{
    public static bool CanManufacture(InventoryCategory category, int groupId, double security, bool supercapitalShipyard = false)
    {
        if (category != InventoryCategory.Ship)
        {
            return true;
        }
        return (HullClass)groupId switch
        {
            HullClass.Titan or HullClass.Supercarrier => supercapitalShipyard && double.IsFinite(security) && security <= 0,
            HullClass.Carrier
            or HullClass.Dreadnought
            or HullClass.LancerDreadnought
            or HullClass.ForceAuxiliary
            or HullClass.CapitalIndustrial
            or HullClass.CommandCarrier => double.IsFinite(security) && security < CrimewatchRules.HighSecurity,
            _ => true,
        };
    }
}
