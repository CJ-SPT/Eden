using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Ammunition;

public sealed record Magazine(long ModuleId, long ShipId, int FlagId, int TypeId, int Quantity, long ReadyAt, double Damage = 0);

public sealed record MagazineChange(Magazine? Previous, Magazine? Current);

public sealed record AmmunitionTransfer(
    IReadOnlyList<InventoryChange> Items,
    IReadOnlyList<MagazineChange> Magazines,
    double ReloadMilliseconds = 0
);

public interface IPilotAmmunition
{
    IReadOnlyList<Magazine> Read(long pilotId, long shipId);
    IReadOnlyDictionary<DogmaAttributeId, double> ChargeAttributes(long pilotId, long shipId, long moduleId);
    AmmunitionTransfer Load(
        long pilotId,
        long shipId,
        IReadOnlyList<long> moduleIds,
        IReadOnlyList<long> chargeIds,
        long sourceId,
        long now
    );
    AmmunitionTransfer Unload(
        long pilotId,
        long shipId,
        IReadOnlyList<long> moduleIds,
        long destinationId,
        int flagId,
        int? quantity,
        long now
    );
}

public enum HybridChargeType
{
    IronSmall = 215,
    AntimatterSmall = 222,
}

public enum ChargeSize
{
    Small = 1,
    Medium = 2,
    Large = 3,
    ExtraLarge = 4,
}

public enum TurretChargeEffect
{
    Range = 596,
    Falloff = 599,
    Tracking = 600,
    Capacitor = 804,
    EntityFlyRange = 1173,
}

public sealed record TurretChargeModifier(
    TurretChargeEffect Effect,
    DogmaAttributeId Attribute,
    DogmaAttributeId Source,
    DogmaOperation Operation,
    double DefaultValue = 0
);

public static class AmmunitionDefinitions
{
    public const int FrequencyCrystalGroup = 86;
    public const int AdvancedBeamCrystalGroup = 374;
    public const int AdvancedPulseCrystalGroup = 375;
    public const int HybridChargeGroup = 85;
    public const int AdvancedRailgunChargeGroup = 373;
    public const int AdvancedBlasterChargeGroup = 377;
    public const int ProjectileChargeGroup = 83;
    public const int AdvancedAutocannonChargeGroup = 372;
    public const int AdvancedArtilleryChargeGroup = 376;
    public const int SmallChargeSize = (int)ChargeSize.Small;
    public const int TechOne = 1;
    public const int TechTwo = 2;

    public static bool ValidSize(double size) =>
        size is (int)ChargeSize.Small or (int)ChargeSize.Medium or (int)ChargeSize.Large or (int)ChargeSize.ExtraLarge;

    public static bool IsTurretChargeGroup(int group) =>
        group
            is FrequencyCrystalGroup
                or AdvancedBeamCrystalGroup
                or AdvancedPulseCrystalGroup
                or HybridChargeGroup
                or AdvancedRailgunChargeGroup
                or AdvancedBlasterChargeGroup
                or ProjectileChargeGroup
                or AdvancedAutocannonChargeGroup
                or AdvancedArtilleryChargeGroup;

    public static bool IsCrystalGroup(int group) => group is FrequencyCrystalGroup or AdvancedBeamCrystalGroup or AdvancedPulseCrystalGroup;

    public static bool CompatibleTurretCharge(
        IReadOnlyDictionary<DogmaAttributeId, double> weapon,
        int group,
        IReadOnlyDictionary<DogmaAttributeId, double> charge
    ) =>
        IsTurretChargeGroup(group)
        && ValidSize(charge.GetValueOrDefault(DogmaAttributeId.ChargeSize))
        && weapon.GetValueOrDefault(DogmaAttributeId.ChargeSize) == charge.GetValueOrDefault(DogmaAttributeId.ChargeSize)
        && (
            weapon.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == group
            || weapon.GetValueOrDefault(DogmaAttributeId.ChargeGroup2) == group
        );

    // Build 3662168 fitted charge effects. Effect 1173 affects sentry/NPC orbit
    // range, not player turret optimal; its native default is 500 metres.
    public static IReadOnlyList<TurretChargeModifier> TurretModifiers { get; } =
    [
        new(TurretChargeEffect.Range, DogmaAttributeId.MaxRange, DogmaAttributeId.WeaponRangeMultiplier, DogmaOperation.PreMultiply),
        new(
            TurretChargeEffect.Falloff,
            DogmaAttributeId.AccuracyFalloff,
            DogmaAttributeId.ChargeFalloffMultiplier,
            DogmaOperation.PostMultiply
        ),
        new(
            TurretChargeEffect.Tracking,
            DogmaAttributeId.TrackingSpeed,
            DogmaAttributeId.ChargeTrackingMultiplier,
            DogmaOperation.PostMultiply
        ),
        new(TurretChargeEffect.Capacitor, DogmaAttributeId.CapacitorNeed, DogmaAttributeId.CapacitorNeedBonus, DogmaOperation.PostPercent),
        new(
            TurretChargeEffect.EntityFlyRange,
            DogmaAttributeId.EntityFlyRange,
            DogmaAttributeId.EntityFlyRangeMultiplier,
            DogmaOperation.PreMultiply,
            500
        ),
    ];
}
