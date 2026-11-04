using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Characters;

public sealed record CharacterBoosterProfile(
    int TypeId,
    int Slot,
    int Bonus,
    long DurationTicks,
    long? MaximumAgeTicks,
    long? LastInjection
)
{
    public int NonDiminishingUses { get; init; }
}

public sealed record ActiveBooster(long ItemId, int TypeId, int Slot, int Bonus, long StartedAt, long ExpiresAt);

public sealed record BoosterSnapshot(IReadOnlyList<ActiveBooster> Boosters, long Revision, CharacterAttributes Attributes);

public sealed record BoosterChange(BoosterSnapshot Snapshot, TrainingState Training, InventoryChange Change);

public interface IPilotBoosters
{
    BoosterSnapshot Read(long pilotId);
    BoosterChange Consume(long pilotId, long itemId, long expectedLocation);
}

public static class BoosterRules
{
    public const int Group = 303;
    public const int BiologySkill = 3405;

    // Pinned Biology effect 2866: durationBonus 66 is +20% per effective level.
    public const decimal BiologyDurationBonusPerLevel = .2m;

    public static long Expiry(CharacterBoosterProfile booster, long born, int biology, long now, IReadOnlyList<ActiveBooster> active)
    {
        if (
            booster.Slot <= 0
            || (booster.Bonus < 0 || (booster.Bonus == 0 && booster.NonDiminishingUses <= 0))
            || biology is < 0 or > 5
            || born > now
        )
        {
            throw new InvalidDataException("Invalid booster profile or character age.");
        }
        if (booster.LastInjection is long last && now >= last)
        {
            throw new InvalidDataException("This booster has passed its last injection date.");
        }
        if (active.Any(b => b.Slot == booster.Slot && b.ExpiresAt > now))
        {
            throw new InvalidDataException("An active booster already occupies that slot.");
        }
        long? ageLimit = booster.MaximumAgeTicks is long age ? checked(born + age) : null;
        if (ageLimit <= now)
        {
            throw new InvalidDataException("This character is too old to use that accelerator.");
        }
        long? end =
            booster.DurationTicks > 0
                ? checked(now + (long)decimal.Ceiling(booster.DurationTicks * (1 + BiologyDurationBonusPerLevel * biology)))
                : ageLimit;
        if (ageLimit is long limit && end > limit)
        {
            end = limit;
        }
        if (end is not long expiry || expiry <= now)
        {
            throw new InvalidDataException("The booster has no remaining duration.");
        }
        return expiry;
    }

    public static CharacterAttributes Effective(CharacterAttributes basis, IEnumerable<ActiveBooster> boosters)
    {
        int bonus = boosters.Sum(b => b.Bonus);
        return new(
            checked(basis.Intelligence + bonus),
            checked(basis.Memory + bonus),
            checked(basis.Charisma + bonus),
            checked(basis.Perception + bonus),
            checked(basis.Willpower + bonus)
        );
    }
}
