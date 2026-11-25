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

    private static readonly CatalogTypeRule s_support = new(typeof(MissileChargeCatalog).FullName!, ValidateSupport);

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) => s_support.Supports(catalog, typeId, group);

    private static bool ValidateSupport(CatalogDbContext catalog, int typeId, int group)
    {
        if (!MissileDefinitions.ChargeGroups.Contains(group))
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        bool automatic =
            group
            is (int)MissileChargeGroup.AutoTargetingLight
                or (int)MissileChargeGroup.AutoTargetingHeavy
                or (int)MissileChargeGroup.AutoTargetingCruise;
        bool breacher = group == (int)MissileChargeGroup.BreacherPod;
        bool festival = group == (int)MissileChargeGroup.Festival;
        var effectId =
            breacher ? DogmaEffect.BreacherPodLaunch
            : festival ? DogmaEffect.SnowballLaunching
            : automatic ? DogmaEffect.AutoTargetingMissile
            : DogmaEffect.MissileLaunching;
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
        var launch = effects.FirstOrDefault(e => e.EffectID == (int)effectId);
        if (
            effects.Count != FittingCatalog.Effects(catalog, typeId).Count
            || launch is null
            || launch.IsWarpSafe != 0
            || launch.EffectCategory != (int)(automatic ? PassiveEffectPhase.Active : PassiveEffectPhase.Targeted)
            || (launch.ModifierInfo ?? []).Length != 0
            || (festival ? !FestivalEffects(effects, launch) : effects.Count != 1)
        )
        {
            return false;
        }
        try
        {
            if (breacher)
            {
                _ = Breacher(catalog, typeId, attributes);
            }
            else if (!festival)
            {
                _ = MissileRules.Warhead(attributes);
            }
            MissileFlightRules.Validate(Drive(catalog, typeId, attributes));
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool FestivalEffects(IReadOnlyList<ClientDogmaeffects> effects, ClientDogmaeffects launch) =>
        effects.Count == 2
        && launch.IsOffensive == 0
        && launch.Guid == "effects.MissileDeployment"
        && effects.FirstOrDefault(e => e.EffectID == (int)DogmaEffect.AmmoSpeedMultiplier)
            is { EffectCategory: (int)PassiveEffectPhase.Fitted, ModifierInfo: [{ } modifier] }
        && modifier.Func == "ItemModifier"
        && modifier.Domain == "otherID"
        && modifier.Operation == (int)DogmaOperation.PostMultiply
        && modifier.ModifiedAttributeID == (int)DogmaAttributeId.WeaponCycleTime
        && modifier.ModifyingAttributeID == (int)DogmaAttributeId.AmmoSpeedMultiplier;

    internal static BreacherPayload Breacher(CatalogDbContext catalog, int typeId, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var result = new BreacherPayload(
            attributes.GetValueOrDefault(DogmaAttributeId.BreacherDuration),
            attributes.GetValueOrDefault(DogmaAttributeId.BreacherMaxDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.BreacherMaxPercent)
        );
        if (
            new[] { result.DurationMilliseconds, result.MaximumDamage, result.MaximumPercent }.Any(value =>
                !double.IsFinite(value) || value <= 0
            )
            || catalog.FittingType(typeId)?.GroupId != (int)MissileChargeGroup.BreacherPod
        )
        {
            throw new InvalidDataException("Invalid Breacher Pod attributes.");
        }
        return result;
    }

    internal static MissileDrive Drive(CatalogDbContext catalog, int typeId, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var type = catalog.FittingType(typeId) ?? throw new InvalidDataException("Unknown missile type.");
        return new(
            type.Mass ?? 0,
            (float)(type.Radius ?? 0),
            (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
            (float)attributes.GetValueOrDefault(DogmaAttributeId.Agility),
            attributes.GetValueOrDefault(DogmaAttributeId.MissileFlightTime)
        );
    }
}
