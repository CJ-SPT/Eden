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
    private const DogmaEffect FieldDurationOverload = (DogmaEffect)3002;
    private const DogmaEffect FieldScriptCapacitor = (DogmaEffect)3615;
    private const DogmaEffect FieldScriptRange = (DogmaEffect)4894;

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

    internal static bool Generator(CatalogDbContext catalog, int type) =>
        catalog.FittingType(type)?.GroupId == InterdictionRules.GeneratorGroup
        && catalog.Read(CatalogData.Client.Types, (long)type)?.Published == 1
        && FittingCatalog.DefaultEffect(catalog, type) == DogmaEffect.WarpDisruptSphere
        && FittingCatalog
            .Effects(catalog, type)
            .SetEquals([
                DogmaEffect.HighPower,
                DogmaEffect.Online,
                FieldDurationOverload,
                DogmaEffect.WarpDisruptSphere,
                FieldScriptCapacitor,
                FieldScriptRange,
            ])
        && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.WarpScrambleRange) > 0
        && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.Duration) > 0;

    internal static bool Script(CatalogDbContext catalog, int type, int group) =>
        group == InterdictionRules.ScriptGroup
        && type is InterdictionRules.FocusedDisruptionScript or InterdictionRules.FocusedScramblingScript
        && catalog.Read(CatalogData.Client.Types, (long)type)?.Published == 1
        && FittingCatalog.DefaultEffect(catalog, type) is { } effect
        && InterdictionRules.Focused(effect)
        && PassiveCatalog
            .Effects(catalog, type)
            .Where(effect => effect.EffectCategory == (int)PassiveEffectPhase.Fitted)
            .All(effect =>
                effect.ModifierInfo is { Length: > 0 }
                && effect.ModifierInfo.Value.All(modifier =>
                    modifier.Domain == "otherID"
                    && modifier.Func == "ItemModifier"
                    && modifier.Operation is (int)DogmaOperation.PostPercent or (int)DogmaOperation.PostAssignment
                )
            );

    internal static bool Probe(CatalogDbContext catalog, int type, int group) =>
        group == InterdictionRules.ProbeGroup
        && catalog.Read(CatalogData.Client.Types, (long)type)?.Published == 1
        && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.WarpScrambleRange) > 0
        && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.MissileFlightTime) > 0;
}
