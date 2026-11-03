using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Ships;

public sealed record CapacitorBooster(long ItemId, int TypeId, double DurationMilliseconds, double Amount, int ChargesPerCycle);

public sealed record CapacitorInjection(CapacitorBooster Module, double Capacitor, Magazine Magazine);

public interface IPilotCapacitorBoosting
{
    CapacitorInjection Inject(long pilotId, long moduleId, long now);
}

public static class CapacitorBoosterDefinitions
{
    public const int Group = 76;
    public const int ChargeGroup = 87;
    public const int SmallTechOne = 3566;
    public const int ChargeOneHundred = 3554;
    private const int TechOne = 1;
    private const int TechTwo = 2;

    public static bool IsSupported(int group, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        group == Group
        && attributes.GetValueOrDefault(DogmaAttributeId.TechLevel) is TechOne or TechTwo
        && !InventoryDefinitions.RequiresSpecificShip(attributes);

    public static CapacitorBooster Define(
        long itemId,
        int typeId,
        IReadOnlyDictionary<DogmaAttributeId, double> module,
        IReadOnlyDictionary<DogmaAttributeId, double> charge
    )
    {
        double duration = module.GetValueOrDefault(DogmaAttributeId.Duration);
        double amount = charge.GetValueOrDefault(DogmaAttributeId.CapacitorBonus);
        double rate = module.GetValueOrDefault(DogmaAttributeId.ChargeRate);
        if (
            !double.IsFinite(duration)
            || duration <= 0
            || !double.IsFinite(amount)
            || amount <= 0
            || !double.IsFinite(rate)
            || rate < 1
            || rate != Math.Truncate(rate)
            || rate > int.MaxValue
        )
        {
            throw new InvalidDataException("Invalid capacitor booster or charge attributes.");
        }
        return new(itemId, typeId, duration, amount * rate, checked((int)rate));
    }

    public static double Inject(double current, double capacity, double amount)
    {
        if (
            !double.IsFinite(current)
            || current < 0
            || !double.IsFinite(capacity)
            || capacity <= 0
            || !double.IsFinite(amount)
            || amount <= 0
        )
        {
            throw new InvalidDataException("Invalid capacitor injection.");
        }
        return Math.Min(capacity, current + amount);
    }
}
