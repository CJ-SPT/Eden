using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Repair;

public enum AncillaryRepairKind
{
    Shield,
    Armor,
}

public sealed record AncillaryRepairer(
    AncillaryRepairKind Kind,
    double DurationMilliseconds,
    double Amount,
    double CapacitorNeed,
    int ChargesPerCycle,
    double ChargedArmorMultiplier,
    double ReloadMilliseconds
);

public sealed record AncillaryRepairPayment(double Amount, double CapacitorNeed, int ChargesUsed);

public static class AncillaryRepairRules
{
    public const int ShieldGroup = 1156;
    public const int ArmorGroup = 1199;
    public const int PasteGroup = 916;

    public static AncillaryRepairKind? Kind(int group) =>
        group switch
        {
            ShieldGroup => AncillaryRepairKind.Shield,
            ArmorGroup => AncillaryRepairKind.Armor,
            _ => null,
        };

    public static DogmaEffect Effect(AncillaryRepairKind kind) =>
        kind switch
        {
            AncillaryRepairKind.Shield => DogmaEffect.AncillaryShieldBoosting,
            AncillaryRepairKind.Armor => DogmaEffect.AncillaryArmorRepair,
            _ => throw new InvalidDataException("Unknown ancillary repair kind."),
        };

    public static bool Compatible(
        AncillaryRepairKind kind,
        int chargeGroup,
        IReadOnlyDictionary<DogmaAttributeId, double> module,
        IReadOnlyDictionary<DogmaAttributeId, double> charge,
        DogmaAttributeId chargeGroupAttribute = DogmaAttributeId.ChargeGroup2
    ) =>
        module.GetValueOrDefault(chargeGroupAttribute) == chargeGroup
        && kind switch
        {
            // ASB charge size zero is meaningful: small boosters use size-zero
            // Cap Booster 25s. Smaller charges cannot extend a larger ASB's burst.
            AncillaryRepairKind.Shield => chargeGroup == CapacitorBoosterDefinitions.ChargeGroup
                && ValidShieldSize(module.GetValueOrDefault(DogmaAttributeId.ChargeSize))
                && ValidShieldSize(charge.GetValueOrDefault(DogmaAttributeId.ChargeSize))
                && module.GetValueOrDefault(DogmaAttributeId.ChargeSize) == charge.GetValueOrDefault(DogmaAttributeId.ChargeSize),
            AncillaryRepairKind.Armor => chargeGroup == PasteGroup,
            _ => false,
        };

    public static AncillaryRepairer Define(AncillaryRepairKind kind, IReadOnlyDictionary<DogmaAttributeId, double> values)
    {
        _ = Effect(kind);
        double rate = values.GetValueOrDefault(DogmaAttributeId.ChargeRate);
        double duration = values.GetValueOrDefault(DogmaAttributeId.Duration);
        double amount = values.GetValueOrDefault(
            kind == AncillaryRepairKind.Shield ? DogmaAttributeId.ShieldBonus : DogmaAttributeId.ArmorRepairAmount
        );
        double cost = values.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        double reload = values.GetValueOrDefault(DogmaAttributeId.ReloadTime);
        double multiplier = kind == AncillaryRepairKind.Armor ? values.GetValueOrDefault(DogmaAttributeId.ChargedArmorMultiplier) : 1;
        if (
            new[] { duration, amount, reload }.Any(v => !double.IsFinite(v) || v <= 0)
            || !double.IsFinite(cost)
            || cost < 0
            || !double.IsFinite(rate)
            || rate < 1
            || rate != Math.Truncate(rate)
            || rate > int.MaxValue
            || kind == AncillaryRepairKind.Shield && rate != 1
            || kind == AncillaryRepairKind.Shield && !ValidShieldSize(values.GetValueOrDefault(DogmaAttributeId.ChargeSize))
            || !double.IsFinite(multiplier)
            || multiplier < 1
        )
        {
            throw new InvalidDataException("Invalid ancillary repair profile.");
        }
        return new(kind, duration, amount, cost, checked((int)rate), multiplier, reload);
    }

    private static bool ValidShieldSize(double value) => value >= 0 && value <= (int)ChargeSize.ExtraLarge && value == Math.Truncate(value);

    public static AncillaryRepairPayment Payment(AncillaryRepairer module, int loadedQuantity)
    {
        _ = Effect(module.Kind);
        if (loadedQuantity < 0)
        {
            throw new InvalidDataException("Invalid loaded ancillary fuel quantity.");
        }
        if (
            new[] { module.DurationMilliseconds, module.Amount, module.ReloadMilliseconds }.Any(v => !double.IsFinite(v) || v <= 0)
            || !double.IsFinite(module.CapacitorNeed)
            || module.CapacitorNeed < 0
            || module.ChargesPerCycle < 1
            || module.Kind == AncillaryRepairKind.Shield && module.ChargesPerCycle != 1
            || !double.IsFinite(module.ChargedArmorMultiplier)
            || module.ChargedArmorMultiplier < 1
        )
        {
            throw new InvalidDataException("Invalid ancillary repair profile.");
        }
        int used = Math.Min(module.ChargesPerCycle, loadedQuantity);
        AncillaryRepairPayment payment = module.Kind switch
        {
            AncillaryRepairKind.Shield => new(module.Amount, used > 0 ? 0 : module.CapacitorNeed, used),
            // CCP's partial-paste rule scales only the additional charged bonus.
            AncillaryRepairKind.Armor => new(
                module.Amount * (1 + (module.ChargedArmorMultiplier - 1) * used / module.ChargesPerCycle),
                module.CapacitorNeed,
                used
            ),
            _ => throw new InvalidDataException("Unknown ancillary repair kind."),
        };
        if (!double.IsFinite(payment.Amount))
        {
            throw new InvalidDataException("The charged ancillary repair amount exceeds the supported range.");
        }
        return payment;
    }
}
