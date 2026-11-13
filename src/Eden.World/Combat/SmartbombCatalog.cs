using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Combat;

internal static class SmartbombCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadDurationEffect = 3002;
    private static readonly CatalogTypeRule s_support = new(typeof(SmartbombCatalog).FullName!, Validate);

    internal static bool Supported(CatalogDbContext catalog, int type, int group) => s_support.Supports(catalog, type, group);

    private static bool Validate(CatalogDbContext catalog, int type, int group)
    {
        var source = catalog.Read(CatalogData.Client.Types, (long)type);
        var attributes = catalog.FittingAttributes(type);
        if (
            group != SmartbombRules.Group
            || catalog.FittingType(type)?.CategoryId != (int)InventoryCategory.Module
            || source?.Published != 1
            || source.MetaGroupID == AbyssalMetaGroup
            || FittingCatalog.DefaultEffect(catalog, type) != DogmaEffect.Smartbomb
            || !FittingCatalog
                .Effects(catalog, type)
                .SetEquals([DogmaEffect.HighPower, DogmaEffect.Online, DogmaEffect.Smartbomb, (DogmaEffect)OverloadDurationEffect])
            || InventoryDefinitions.RequiresSpecificShip(attributes)
        )
        {
            return false;
        }
        var effect = catalog.Read(CatalogData.Client.Dogmaeffects, (long)DogmaEffect.Smartbomb);
        if (
            effect
            is not {
                EffectName: "empWave",
                Guid: "effects.EMPWave",
                EffectCategory: (int)PassiveEffectPhase.Active,
                IsOffensive: 1,
                IsWarpSafe: 0,
                DisallowAutoRepeat: 0,
                RangeAttributeID: (int)DogmaAttributeId.EmpFieldRange,
                DurationAttributeID: (int)DogmaAttributeId.Duration,
                DischargeAttributeID: (int)DogmaAttributeId.CapacitorNeed,
            }
        )
        {
            return false;
        }
        try
        {
            _ = SmartbombRules.Define(0, type, attributes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
