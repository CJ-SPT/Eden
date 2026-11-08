using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Thermal;

internal static class OverloadCatalog
{
    private const int AbyssalMetaGroup = 15;

    // Five levels must not produce a negative damage multiplier.
    private const double MinimumThermodynamicsBonusPerLevel = -20;
    private static readonly CatalogDefinition<OverloadProfile?> s_profiles = new("overload-profile", Read);
    private static readonly CatalogDefinition<double> s_thermodynamics = new("thermodynamics-bonus", ReadThermodynamics);

    internal static OverloadProfile? Definition(CatalogDbContext catalog, int type) => catalog.Definition(s_profiles, type);

    internal static double ThermodynamicsBonus(CatalogDbContext catalog) =>
        catalog.Definition(s_thermodynamics, OverloadRules.ThermodynamicsSkill);

    private static double ReadThermodynamics(CatalogDbContext catalog, int skill)
    {
        const int skillLevelEffect = 3195;
        const int damageEffect = 3196;
        var effects = PassiveCatalog.Effects(catalog, skill);
        var level = effects.SingleOrDefault(e => e.EffectID == skillLevelEffect)?.ModifierInfo;
        var damage = effects.SingleOrDefault(e => e.EffectID == damageEffect)?.ModifierInfo;
        double bonus = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.ThermodynamicsHeatDamage);
        if (
            level is not { Length: 1 }
            || damage is not { Length: 1 }
            || !double.IsFinite(bonus)
            || bonus < MinimumThermodynamicsBonusPerLevel
            || bonus > 0
        )
        {
            throw new InvalidDataException("Unverified Thermodynamics skill program.");
        }
        var scale = level.Value[0];
        var apply = damage.Value[0];
        if (
            scale.Domain != "itemID"
            || scale.Func != "ItemModifier"
            || scale.Operation != (int)DogmaOperation.PreMultiply
            || scale.ModifiedAttributeID != (int)DogmaAttributeId.ThermodynamicsHeatDamage
            || scale.ModifyingAttributeID != (int)DogmaAttributeId.SkillLevel
            || apply.Domain != "shipID"
            || apply.Func != "LocationModifier"
            || apply.Operation != (int)DogmaOperation.PostPercent
            || apply.ModifiedAttributeID != (int)DogmaAttributeId.HeatDamage
            || apply.ModifyingAttributeID != (int)DogmaAttributeId.ThermodynamicsHeatDamage
        )
        {
            throw new InvalidDataException("Unverified Thermodynamics skill selectors.");
        }
        return bonus;
    }

    private static OverloadProfile? Read(CatalogDbContext catalog, int type)
    {
        var source = catalog.Read(CatalogData.Client.Types, (long)type);
        if (
            source?.Published != 1
            || source.MetaGroupID == AbyssalMetaGroup
            || catalog.FittingType(type)?.CategoryId != (int)InventoryCategory.Module
            || FittingCatalog.Definition(catalog, type) is not { Rack: not ModuleRack.Rig } fitting
        )
        {
            return null;
        }
        var effects = PassiveCatalog.Effects(catalog, type).Where(e => e.EffectCategory == OverloadRules.EffectCategory).ToArray();
        if (
            effects is not [var effect]
            || effect.EffectID is not long effectId
            || effectId is <= 0 or > int.MaxValue
            || (effect.ModifierInfo ?? []).Length == 0
        )
        {
            return null;
        }
        List<OverloadBonus> bonuses = [];
        foreach (var modifier in effect.ModifierInfo!.Value)
        {
            if (
                modifier.Domain != "itemID"
                || modifier.Func != "ItemModifier"
                || modifier.Operation != (int)DogmaOperation.PostPercent
                || modifier.ModifiedAttributeID is not long target
                || modifier.ModifyingAttributeID is not long from
                || target is < 0 or > int.MaxValue
                || from is < 0 or > int.MaxValue
            )
            {
                return null;
            }
            bonuses.Add(new((DogmaAttributeId)checked((int)target), (DogmaAttributeId)checked((int)from)));
        }
        try
        {
            return OverloadRules.Define(type, checked((int)effectId), fitting.Rack, catalog.FittingAttributes(type), bonuses);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
