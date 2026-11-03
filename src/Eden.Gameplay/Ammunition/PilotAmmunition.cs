using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Ammunition;

public sealed record Magazine(long ModuleId, long ShipId, int FlagId, int TypeId, int Quantity, long ReadyAt);

public sealed record MagazineChange(Magazine? Previous, Magazine? Current);

public sealed record AmmunitionTransfer(
    IReadOnlyList<InventoryChange> Items,
    IReadOnlyList<MagazineChange> Magazines,
    double ReloadMilliseconds = 0
);

public interface IPilotAmmunition
{
    IReadOnlyList<Magazine> Read(long pilotId, long shipId);
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

public enum HybridChargeEffect
{
    Range = 596,
    Falloff = 599,
    Tracking = 600,
    Capacitor = 804,
    EntityFlyRange = 1173,
}

public sealed record HybridChargeModifier(
    HybridChargeEffect Effect,
    DogmaAttributeId Attribute,
    DogmaAttributeId Source,
    DogmaOperation Operation,
    double DefaultValue = 0
);

public static class AmmunitionDefinitions
{
    public const int HybridChargeGroup = 85;
    public const int AdvancedRailgunChargeGroup = 373;
    public const int AdvancedBlasterChargeGroup = 377;
    public const int SmallChargeSize = (int)ChargeSize.Small;
    public const int TechOne = 1;
    public const int TechTwo = 2;

    public static bool ValidSize(double size) =>
        size is (int)ChargeSize.Small or (int)ChargeSize.Medium or (int)ChargeSize.Large or (int)ChargeSize.ExtraLarge;

    public static bool HybridGroup(int group) => group is HybridChargeGroup or AdvancedRailgunChargeGroup or AdvancedBlasterChargeGroup;

    public static bool CompatibleHybridCharge(
        IReadOnlyDictionary<DogmaAttributeId, double> weapon,
        int group,
        IReadOnlyDictionary<DogmaAttributeId, double> charge
    ) =>
        HybridGroup(group)
        && ValidSize(charge.GetValueOrDefault(DogmaAttributeId.ChargeSize))
        && weapon.GetValueOrDefault(DogmaAttributeId.ChargeSize) == charge.GetValueOrDefault(DogmaAttributeId.ChargeSize)
        && (
            weapon.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == group
            || weapon.GetValueOrDefault(DogmaAttributeId.ChargeGroup2) == group
        );

    // Build 3662168 fitted charge effects. Effect 1173 affects sentry/NPC orbit
    // range, not player turret optimal; its native default is 500 metres.
    public static IReadOnlyList<HybridChargeModifier> HybridModifiers { get; } =
    [
        new(HybridChargeEffect.Range, DogmaAttributeId.MaxRange, DogmaAttributeId.WeaponRangeMultiplier, DogmaOperation.PreMultiply),
        new(
            HybridChargeEffect.Falloff,
            DogmaAttributeId.AccuracyFalloff,
            DogmaAttributeId.ChargeFalloffMultiplier,
            DogmaOperation.PostMultiply
        ),
        new(
            HybridChargeEffect.Tracking,
            DogmaAttributeId.TrackingSpeed,
            DogmaAttributeId.ChargeTrackingMultiplier,
            DogmaOperation.PostMultiply
        ),
        new(HybridChargeEffect.Capacitor, DogmaAttributeId.CapacitorNeed, DogmaAttributeId.CapacitorNeedBonus, DogmaOperation.PostPercent),
        new(
            HybridChargeEffect.EntityFlyRange,
            DogmaAttributeId.EntityFlyRange,
            DogmaAttributeId.EntityFlyRangeMultiplier,
            DogmaOperation.PreMultiply,
            500
        ),
    ];
}
