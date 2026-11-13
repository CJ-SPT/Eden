using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Fleets;

public sealed record CommandBurstModule(
    long Id,
    int TypeId,
    DogmaEffect Effect,
    double Radius,
    double Capacitor,
    long CycleTicks,
    long BoostTicks,
    int MaximumActive
);

public readonly record struct CommandBurstValue(int CollectionId, double Value);

public readonly record struct CommandBurstBoost(int CollectionId, double Value, long ExpiresAt);

public sealed record CommandBurstCollection(int Id, bool Minimum, DogmaOperation Operation, IReadOnlyList<CommandBurstModifier> Modifiers);

public readonly record struct CommandBurstModifier(DogmaAttributeId Attribute, ModifierSelector Selector, int SelectionId = 0);

public sealed record CommandBurstCandidate(long PilotId, long ShipId, SpacePosition Position);

public sealed record CommandBurstPulse(CommandBurstModule Module, long ReadyAt, double Capacitor, Magazine Magazine, long[] Recipients);

public interface IPilotCommandBursts
{
    CommandBurstPulse Pulse(
        long pilot,
        long ship,
        long module,
        DogmaEffect effect,
        IReadOnlyList<CommandBurstCandidate> candidates,
        long now
    );

    IReadOnlyList<CommandBurstBoost> Incoming(long ship, long now);
}

public static class CommandBurstRules
{
    public const int Group = 1770;

    public static bool IsBurst(DogmaEffect effect) =>
        effect
            is DogmaEffect.ArmorCommandBurst
                or DogmaEffect.ShieldCommandBurst
                or DogmaEffect.SkirmishCommandBurst
                or DogmaEffect.InformationCommandBurst
                or DogmaEffect.MiningForemanBurst;

    public static bool IsCharge(int group) =>
        (HoldCargoGroup)group
            is HoldCargoGroup.ArmorCommandBurstCharges
                or HoldCargoGroup.ShieldCommandBurstCharges
                or HoldCargoGroup.SkirmishCommandBurstCharges
                or HoldCargoGroup.InformationCommandBurstCharges
                or HoldCargoGroup.MiningForemanBurstCharges
                or HoldCargoGroup.ExpeditionCommandBurstCharges;

    public static bool Compatible(int moduleGroup, int chargeGroup, IReadOnlyDictionary<DogmaAttributeId, double> module) =>
        moduleGroup == Group && IsCharge(chargeGroup) && module.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == chargeGroup;

    public static CommandBurstModule Define(long id, int type, DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> a)
    {
        double radius = a.GetValueOrDefault(DogmaAttributeId.MaxRange);
        double capacitor = a.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        double limit = a.GetValueOrDefault(DogmaAttributeId.MaxGroupActive);
        if (
            !IsBurst(effect)
            || !double.IsFinite(radius)
            || radius <= 0
            || !double.IsFinite(capacitor)
            || capacitor < 0
            || limit is < 1 or > int.MaxValue
            || limit != Math.Truncate(limit)
        )
        {
            throw new InvalidDataException("Invalid command burst module attributes.");
        }
        return new(
            id,
            type,
            effect,
            radius,
            capacitor,
            Ticks(DogmaAttributeId.Duration),
            Ticks(DogmaAttributeId.BurstDuration),
            (int)limit
        );

        long Ticks(DogmaAttributeId attribute)
        {
            double milliseconds = a.GetValueOrDefault(attribute);
            if (!double.IsFinite(milliseconds) || milliseconds <= 0)
            {
                throw new InvalidDataException("Invalid command burst duration.");
            }
            return checked((long)Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond));
        }
    }

    public static CommandBurstBoost[] Strongest(IEnumerable<CommandBurstBoost> boosts, Func<int, CommandBurstCollection> definition) =>
        boosts
            .GroupBy(boost => boost.CollectionId)
            .OrderBy(group => group.Key)
            .Select(group =>
                definition(group.Key).Minimum
                    ? group.MinBy(boost => (boost.Value, -boost.ExpiresAt))
                    : group.MaxBy(boost => (boost.Value, boost.ExpiresAt))
            )
            .ToArray();

    public static bool InRange(SpacePosition origin, double sourceRadius, SpacePosition target, double targetRadius, double range) =>
        origin.DistanceTo(target) <= range + sourceRadius + targetRadius;
}
