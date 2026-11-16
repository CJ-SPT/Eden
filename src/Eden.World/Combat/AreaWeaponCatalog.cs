using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Combat;

internal static class AreaWeaponCatalog
{
    private static readonly CatalogTypeRule s_modules = new(typeof(AreaWeaponCatalog).FullName!, ValidateModule);

    internal static bool Module(CatalogDbContext catalog, int type, int group) => s_modules.Supports(catalog, type, group);

    private static bool ValidateModule(CatalogDbContext catalog, int type, int group)
    {
        if (!AreaWeaponRules.IsModule(group) || catalog.Read(CatalogData.Client.Types, (long)type) is not { Published: 1 })
        {
            return false;
        }
        var effect = FittingCatalog.DefaultEffect(catalog, type);
        if (effect is null)
        {
            return false;
        }
        try
        {
            _ = AreaWeaponRules.Define(0, type, group, effect.Value, catalog.FittingAttributes(type));
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return false;
        }
        var effects = PassiveCatalog.Effects(catalog, type);
        var rack = group == AreaWeaponRules.BurstJammerGroup ? DogmaEffect.MediumPower : DogmaEffect.HighPower;
        return effects.Count == FittingCatalog.Effects(catalog, type).Count
            && effects.Any(e => e.EffectID == (int)rack)
            && effects.Any(e => e.EffectID == (int)DogmaEffect.Online)
            && effects.All(e =>
                e.EffectID == (int)effect
                || e.EffectID == (int)rack
                || e.EffectID == (int)DogmaEffect.Online
                || e.EffectCategory == 5
                || (e.ModifierInfo ?? []).Length > 0 && e.ModifierInfo!.Value.All(m => Parse(m, e.EffectCategory!.Value) is not null)
            );
    }

    internal static bool Charge(CatalogDbContext catalog, int type, int group)
    {
        if (
            !(AreaWeaponRules.IsBomb(group) || group == AreaWeaponRules.DefenderChargeGroup)
            || catalog.Read(CatalogData.Client.Types, (long)type) is not { Published: 1 }
        )
        {
            return false;
        }
        var expected = group == AreaWeaponRules.DefenderChargeGroup ? DogmaEffect.DefenderLaunching : DogmaEffect.BombLaunching;
        var attributes = catalog.FittingAttributes(type);
        return FittingCatalog.DefaultEffect(catalog, type) == expected
            && attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity) > 0
            && attributes.GetValueOrDefault(DogmaAttributeId.MissileFlightTime) > 0
            && (group == AreaWeaponRules.DefenderChargeGroup || Bomb(catalog, type).Radius > 0);
    }

    internal static BombProfile Bomb(CatalogDbContext catalog, int type)
    {
        var attributes = catalog.FittingAttributes(type);
        double Value(DogmaAttributeId id, double fallback = 0) => attributes.GetValueOrDefault(id, fallback);
        DamageVector armor = new(
            Value(DogmaAttributeId.ArmorEmResonance, 1),
            Value(DogmaAttributeId.ArmorExplosiveResonance, 1),
            Value(DogmaAttributeId.ArmorKineticResonance, 1),
            Value(DogmaAttributeId.ArmorThermalResonance, 1)
        );
        DamageVector full = new(1, 1, 1, 1);
        HullDefense defense = new(
            0,
            Value(DogmaAttributeId.ArmorHitPoints),
            Value(DogmaAttributeId.StructureHitPoints),
            0,
            full,
            armor,
            full
        );
        var power = AreaWeaponRules
            .Define(
                0,
                0,
                AreaWeaponRules.BurstJammerGroup,
                DogmaEffect.BurstJammer,
                new Dictionary<DogmaAttributeId, double>(attributes)
                {
                    [DogmaAttributeId.Duration] = 1,
                    [DogmaAttributeId.EcmBurstRange] = 1,
                }
            )
            .Power;
        return new(
            type,
            catalog.FittingType(type)!.GroupId,
            Value(DogmaAttributeId.MaxVelocity),
            Value(DogmaAttributeId.MissileFlightTime),
            Value(DogmaAttributeId.BombExplosionRange),
            Value(DogmaAttributeId.ExplosionRadius),
            Value(DogmaAttributeId.SignatureRadius),
            defense,
            new(
                Value(DogmaAttributeId.EmDamage),
                Value(DogmaAttributeId.ExplosiveDamage),
                Value(DogmaAttributeId.KineticDamage),
                Value(DogmaAttributeId.ThermalDamage)
            ),
            power
        );
    }

    private static PassiveModifier? Parse(ClientDogmaeffectsModifierInfoItem modifier, long phase)
    {
        bool immunity =
            modifier is
            {
                Domain: "shipID",
                Func: "ItemModifier",
                Operation: (int)DogmaOperation.PostAssignment,
                ModifiedAttributeID: (int)DogmaAttributeId.WarpBubbleImmune,
                ModifyingAttributeID: (int)DogmaAttributeId.WarpBubbleImmuneModifier,
            };
        return PassiveCatalog.Parse(modifier, phase, rig: immunity);
    }

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int type) =>
        PassiveCatalog
            .Effects(catalog, type)
            .Where(effect => effect.EffectCategory is 0 or 4)
            .SelectMany(effect =>
                (effect.ModifierInfo ?? []).Select(modifier =>
                    Parse(modifier, effect.EffectCategory!.Value)
                    ?? throw new InvalidDataException("Unsupported area weapon passive modifier.")
                )
            )
            .ToArray();
}
