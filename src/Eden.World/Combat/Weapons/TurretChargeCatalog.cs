using Eden.Data.Catalog;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Combat.Weapons;

internal static class TurretChargeCatalog
{
    private const int AbyssalMetaGroup = 15;

    private static readonly CatalogTypeRule s_support = new(typeof(TurretChargeCatalog).FullName!, ValidateSupport);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool ValidateSupport(CatalogDbContext catalog, int typeId, int group)
    {
        if (!AmmunitionDefinitions.IsTurretChargeGroup(group))
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || !AmmunitionDefinitions.ValidSize(attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize))
        )
        {
            return false;
        }
        if (AmmunitionDefinitions.IsCrystalGroup(group))
        {
            try
            {
                CrystalRules.Use(attributes, 0, 0);
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        HashSet<TurretChargeEffect> expectedEffects = group switch
        {
            AmmunitionDefinitions.FrequencyCrystalGroup =>
            [
                TurretChargeEffect.Range,
                TurretChargeEffect.Capacitor,
                TurretChargeEffect.EntityFlyRange,
            ],
            AmmunitionDefinitions.HybridChargeGroup => [TurretChargeEffect.Range, TurretChargeEffect.Capacitor],
            AmmunitionDefinitions.ProjectileChargeGroup =>
            [
                TurretChargeEffect.Range,
                TurretChargeEffect.Tracking,
                TurretChargeEffect.EntityFlyRange,
            ],
            _ => [TurretChargeEffect.Range, TurretChargeEffect.Tracking, TurretChargeEffect.Falloff, TurretChargeEffect.EntityFlyRange],
        };
        if (group == AmmunitionDefinitions.AdvancedRailgunChargeGroup || attributes.ContainsKey(DogmaAttributeId.CapacitorNeedBonus))
        {
            expectedEffects.Add(TurretChargeEffect.Capacitor);
        }
        return effects.Count == expectedEffects.Count
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.All(e =>
            {
                var expected = AmmunitionDefinitions.TurretModifiers.SingleOrDefault(m => (int)m.Effect == e.EffectID);
                return expected is not null
                    && expectedEffects.Contains(expected.Effect)
                    && e.EffectCategory == (int)PassiveEffectPhase.Fitted
                    && e.ModifierInfo is [{ } modifier]
                    && modifier.Func == "ItemModifier"
                    && modifier.Domain == "otherID"
                    && modifier.ModifiedAttributeID == (int)expected.Attribute
                    && modifier.ModifyingAttributeID == (int)expected.Source
                    && modifier.Operation == (int)expected.Operation;
            });
    }
}
