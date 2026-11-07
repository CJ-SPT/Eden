using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Combat.Weapons;

internal static class MissileLauncherCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int OverloadRate = 3001;
    private const int OverloadPhase = 5;
    private const int HullOverride = 5994;
    private const int ShieldArmorOverride = 5995;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (!MissileDefinitions.SupportedLauncher(group, attributes))
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || FittingCatalog.DefaultEffect(catalog, typeId) != DogmaEffect.UseMissiles
            || !double.IsFinite(attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime))
            || attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) <= 0
            || attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed) != 0
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        var ids = effects.Select(e => e.EffectID!.Value).ToHashSet();
        bool civilian = typeId == MissileDefinitions.CivilianLauncher;
        if (
            effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || !new long[] { (int)DogmaEffect.Online, (int)DogmaEffect.HighPower, (int)DogmaEffect.UseMissiles }.All(ids.Contains)
            || (!civilian && (!ids.Contains((int)DogmaEffect.LauncherFitted) || !ids.Contains(OverloadRate)))
            || ids.Contains(HullOverride) != ids.Contains(ShieldArmorOverride)
            || (civilian && effects.Count != 3)
        )
        {
            return false;
        }
        return effects.Any(e =>
                e.EffectID == (int)DogmaEffect.UseMissiles
                && e.EffectCategory == (int)PassiveEffectPhase.Active
                && e.DurationAttributeID == (int)DogmaAttributeId.WeaponCycleTime
                && e.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                && e.IsWarpSafe == 0
            )
            && effects.All(e =>
                e.EffectID switch
                {
                    (int)DogmaEffect.Online
                    or (int)DogmaEffect.HighPower
                    or (int)DogmaEffect.UseMissiles
                    or (int)DogmaEffect.LauncherFitted => (e.ModifierInfo ?? []).Length == 0,
                    OverloadRate => e.EffectCategory == OverloadPhase,
                    HullOverride or ShieldArmorOverride => WeaponCatalog.ValidOverride(e),
                    _ => false,
                }
            );
    }
}
