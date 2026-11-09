using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Information;

public enum InformationGroup
{
    CargoScanner = 47,
    ShipScanner = 48,
    MiningSurveyChipset = 49,
    PassiveTargeter = 82,
    AutoTargeter = 96,
    TractorBeam = 650,
    Salvager = 1122,
}

public enum InformationSkill
{
    Survey = 3551,
    Salvaging = 25863,
}

public sealed record InformationModule(
    long ItemId,
    int TypeId,
    DogmaEffect Effect,
    double DurationMilliseconds,
    double CapacitorNeed,
    double Range,
    double Falloff,
    double TractorVelocity,
    double AccessBonus
);

public static class InformationRules
{
    // Build 3662168 directionalScanSvc.pyj THROTTLE_TIME is 1500 milliseconds.
    public static readonly TimeSpan DirectionalScanCooldown = TimeSpan.FromMilliseconds(1500);

    public const double MaximumDirectionalRange = 2_147_483_648_000;
    public const double TractorStopDistance = 1500;
    public const double MiningScanRange = 250_000;

    public static DogmaEffect? Effect(int group) =>
        (InformationGroup)group switch
        {
            InformationGroup.CargoScanner => DogmaEffect.CargoScan,
            InformationGroup.ShipScanner => DogmaEffect.ShipScan,
            InformationGroup.PassiveTargeter => DogmaEffect.PassiveTargeting,
            InformationGroup.AutoTargeter => DogmaEffect.AutomaticTargeting,
            InformationGroup.TractorBeam => DogmaEffect.TractorBeam,
            InformationGroup.Salvager => DogmaEffect.Salvaging,
            _ => null,
        };

    public static InformationModule Define(
        long itemId,
        int typeId,
        DogmaEffect effect,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes
    )
    {
        if (!Enum.GetValues<InformationGroup>().Any(g => Effect((int)g) == effect))
        {
            throw new InvalidDataException("Unsupported information module effect.");
        }
        double Value(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
        var range = effect switch
        {
            DogmaEffect.CargoScan => DogmaAttributeId.CargoScanRange,
            DogmaEffect.ShipScan => DogmaAttributeId.ShipScanRange,
            DogmaEffect.AutomaticTargeting => DogmaAttributeId.TargetHostileRange,
            _ => DogmaAttributeId.MaxRange,
        };
        InformationModule module = new(
            itemId,
            typeId,
            effect,
            Value(DogmaAttributeId.Duration),
            Value(DogmaAttributeId.CapacitorNeed),
            Value(range),
            Value(DogmaAttributeId.ShipScanFalloff),
            Value(DogmaAttributeId.MaxTractorVelocity),
            Value(DogmaAttributeId.AccessDifficultyBonus)
        );
        double[] values =
        [
            module.DurationMilliseconds,
            module.CapacitorNeed,
            module.Range,
            module.Falloff,
            module.TractorVelocity,
            module.AccessBonus,
        ];
        if (
            values.Any(v => !double.IsFinite(v) || v < 0)
            || module.DurationMilliseconds <= 0
            || effect != DogmaEffect.PassiveTargeting && module.Range <= 0
            || effect == DogmaEffect.TractorBeam && module.TractorVelocity <= 0
        )
        {
            throw new InvalidDataException("Invalid information module attributes.");
        }
        return module;
    }

    public static double DirectionalRange(double requested, double shipMaximum)
    {
        if (!double.IsFinite(requested) || requested < 0 || !double.IsFinite(shipMaximum) || shipMaximum < 0)
        {
            throw new InvalidDataException("Invalid directional scan range.");
        }
        // Build 3662168's unset dir_scanrange default is already in metres, but
        // GetScanRangeInMeters multiplies it by 1000 again. Cap oversized requests
        // without allowing that client default to expand authoritative visibility.
        return Math.Min(requested, Math.Min(shipMaximum, MaximumDirectionalRange));
    }

    public static bool InCone(SpacePosition origin, SpacePosition position, SpacePosition direction, double angle, double range)
    {
        double length = direction.DistanceTo(SpacePosition.Zero);
        if (
            !double.IsFinite(angle)
            || angle <= 0
            || angle > Math.Tau
            || !double.IsFinite(range)
            || range < 0
            || range > MaximumDirectionalRange
            || !double.IsFinite(direction.X)
            || !double.IsFinite(direction.Y)
            || !double.IsFinite(direction.Z)
            || !double.IsFinite(length)
            || length <= 0
        )
        {
            throw new InvalidDataException("Invalid directional scan cone.");
        }
        var offset = position - origin;
        double distance = offset.DistanceTo(SpacePosition.Zero);
        return distance <= range
            && (
                distance == 0
                || angle == Math.Tau
                || (offset.X * direction.X + offset.Y * direction.Y + offset.Z * direction.Z) / distance / length
                    >= Math.Cos(angle / 2) - 1e-12
            );
    }

    public static SpacePosition Pull(SpacePosition wreck, SpacePosition ship, double stopDistance, double velocity, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(velocity) || velocity < 0 || stopDistance < 0)
        {
            throw new InvalidDataException("Invalid tractor movement.");
        }
        double distance = wreck.DistanceTo(ship);
        double fraction = distance == 0 ? 0 : Math.Min(Math.Max(0, distance - stopDistance), velocity * seconds) / distance;
        return wreck + (ship - wreck) * fraction;
    }

    public static double SalvageChance(double accessBonus, double accessDifficulty) =>
        Math.Clamp((accessBonus + accessDifficulty) / 100, 0, 1);
}
