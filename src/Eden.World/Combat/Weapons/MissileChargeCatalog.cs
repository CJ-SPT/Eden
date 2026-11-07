using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Combat.Weapons;

internal static class MissileChargeCatalog
{
    private const int AbyssalMetaGroup = 15;

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        if (!MissileDefinitions.ChargeGroups.Contains(group))
        {
            return false;
        }
        var type = catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        bool automatic =
            group
            is (int)MissileChargeGroup.AutoTargetingLight
                or (int)MissileChargeGroup.AutoTargetingHeavy
                or (int)MissileChargeGroup.AutoTargetingCruise;
        var effectId = automatic ? DogmaEffect.AutoTargetingMissile : DogmaEffect.MissileLaunching;
        if (
            type?.Published != 1
            || type.MetaGroupID == AbyssalMetaGroup
            || InventoryDefinitions.RequiresSpecificShip(attributes)
            || FittingCatalog.DefaultEffect(catalog, typeId) != effectId
        )
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, typeId);
        if (
            effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || effects is not [{ } effect]
            || effect.EffectID != (int)effectId
            || effect.IsWarpSafe != 0
            || effect.EffectCategory != (int)(automatic ? PassiveEffectPhase.Active : PassiveEffectPhase.Targeted)
            || (effect.ModifierInfo ?? []).Length != 0
        )
        {
            return false;
        }
        try
        {
            _ = MissileRules.Warhead(attributes);
            MissileFlightRules.Validate(Drive(catalog, typeId, attributes));
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static MissileDrive Drive(CatalogDbContext catalog, int typeId, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var type = catalog.Set<ItemType>().Find(typeId) ?? throw new InvalidDataException("Unknown missile type.");
        return new(
            type.Mass ?? 0,
            (float)(type.Radius ?? 0),
            (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            (float)attributes.GetValueOrDefault(DogmaAttributeId.Agility),
            attributes.GetValueOrDefault(DogmaAttributeId.MissileFlightTime)
        );
    }
}
