using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public sealed record DoomsdayCharge(DamageVector Damage, double MovementLockMilliseconds, double DriveLockMilliseconds);

public static class DoomsdayRules
{
    // Build 3662168 group, skills and targeted racial effects.
    public const int Group = 588;
    public const int OperationSkill = 24563;
    public const int RapidFiringSkill = 41537;

    public static DogmaEffect? Effect(string name) =>
        name switch
        {
            "superWeaponAmarr" => DogmaEffect.SuperWeaponAmarr,
            "superWeaponCaldari" => DogmaEffect.SuperWeaponCaldari,
            "superWeaponGallente" => DogmaEffect.SuperWeaponGallente,
            "superWeaponMinmatar" => DogmaEffect.SuperWeaponMinmatar,
            _ => null,
        };

    public static bool IsTargeted(DogmaEffect effect) =>
        effect
            is DogmaEffect.SuperWeaponAmarr
                or DogmaEffect.SuperWeaponCaldari
                or DogmaEffect.SuperWeaponGallente
                or DogmaEffect.SuperWeaponMinmatar;

    public static DogmaEffect? EffectForType(int typeId) =>
        typeId switch
        {
            24550 => DogmaEffect.SuperWeaponAmarr,
            24552 => DogmaEffect.SuperWeaponCaldari,
            24554 => DogmaEffect.SuperWeaponGallente,
            23674 => DogmaEffect.SuperWeaponMinmatar,
            TitanLanceRules.HolyDestiny or TitanLanceRules.IronPike or TitanLanceRules.Phalarica or TitanLanceRules.Geiravor =>
                DogmaEffect.DoomsdayBeam,
            _ => null,
        };

    public static string Graphic(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.SuperWeaponAmarr => "effects.SuperWeaponAmarr",
            DogmaEffect.SuperWeaponCaldari => "effects.SuperWeaponCaldari",
            DogmaEffect.SuperWeaponGallente => "effects.SuperWeaponGallente",
            DogmaEffect.SuperWeaponMinmatar => "effects.SuperWeaponMinmatar",
            _ => throw new InvalidDataException("Unknown targeted doomsday effect."),
        };

    public static DoomsdayCharge Charge(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double Read(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
        var result = new DoomsdayCharge(
            new(
                Read(DogmaAttributeId.EmDamage),
                Read(DogmaAttributeId.ExplosiveDamage),
                Read(DogmaAttributeId.KineticDamage),
                Read(DogmaAttributeId.ThermalDamage)
            ),
            Read(DogmaAttributeId.DoomsdayImmobility),
            Read(DogmaAttributeId.DoomsdayDriveLock)
        );
        if (
            new[] { result.Damage.Total, result.MovementLockMilliseconds, result.DriveLockMilliseconds }.Any(value =>
                !double.IsFinite(value) || value <= 0
            )
        )
        {
            throw new InvalidDataException("Invalid targeted doomsday catalog data.");
        }
        return result;
    }
}
