using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Propulsion;

internal static class InterdictionCatalog
{
    // Build 3662168 launcher overload dogma effect.
    private const DogmaEffect OverloadRate = (DogmaEffect)3001;

    internal static bool Module(CatalogDbContext catalog, int type)
    {
        var item = catalog.FittingType(type);
        if (catalog.Read(CatalogData.Client.Types, (long)type)?.Published != 1 || item?.GroupId != InterdictionRules.LauncherGroup)
        {
            return false;
        }
        var attributes = catalog.FittingAttributes(type);
        return FittingCatalog.DefaultEffect(catalog, type) == DogmaEffect.UseMissiles
            && FittingCatalog
                .Effects(catalog, type)
                .SetEquals([DogmaEffect.HighPower, DogmaEffect.Online, DogmaEffect.UseMissiles, OverloadRate])
            && attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) > 0
            && attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == InterdictionRules.ProbeGroup;
    }

    internal static bool Probe(CatalogDbContext catalog, int type, int group) =>
        group == InterdictionRules.ProbeGroup
        && catalog.Read(CatalogData.Client.Types, (long)type)?.Published == 1
        && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.WarpScrambleRange) > 0
        && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.MissileFlightTime) > 0;
}
