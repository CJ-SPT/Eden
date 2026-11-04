using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Skills;

public enum SkillInjectorType
{
    Large = 40520,
    Mini = 43630,
    Small = 45635,
    DailyAlpha = 46375,
}

public enum SkillInjectorKind
{
    Diminishing,
    Fixed,
    DailyAlpha,
}

public sealed record SkillInjectorProfile(int TypeId, SkillInjectorKind Kind, long Points, long? MaximumTotal);

public sealed record SkillInjectionInputs(
    long Allocated,
    long Unallocated,
    AccountCloneGrade Grade,
    int AvailableUses,
    long NextAlphaInjection
);

public sealed record SkillInjectionPlan(long Points, long TotalAfter, int UsesSpent, long NextAlphaInjection);

public sealed record SkillInjectorStatus(int AvailableUses, long NextAlphaInjection, long Revision);

public sealed record SkillInjectorChange(FreePointChange Points, SkillInjectorStatus Status, IReadOnlyList<InventoryChange> Changes);

public interface IPilotSkillInjectors
{
    SkillInjectorStatus Read(long pilotId);
    long AllocatedPoints(long pilotId);
    long Preview(long pilotId, int typeId, int quantity);
    void Check(long pilotId, long itemId, int quantity);
    SkillInjectorChange Inject(long pilotId, long itemId, int quantity);
    IReadOnlyList<InventoryChange> Convert(long pilotId, long itemId, int quantity, bool split);
}

public sealed class InvalidSkillInjection(string name, int typeId = 0, long? limit = null) : Exception(name)
{
    public int TypeId { get; } = typeId;
    public long? Limit { get; } = limit;
}

public static class SkillInjectorRules
{
    // Build 3662168 appConst, inventorycommon.const, and localized item descriptions.
    public const int Group = 1739;
    public const int MaximumBatch = 10_000;
    public const int SmallPerLarge = 5;
    public const long LargePoints = 500_000;
    public const long SmallPoints = LargePoints / SmallPerLarge;
    private const long FreeZone = 5_000_000;
    private const long LowDiminishLimit = 50_000_000;
    private const long MediumDiminishLimit = 80_000_000;
    private const int Percentage = 100;

    private enum Efficiency
    {
        Full = 100,
        LowDiminish = 80,
        MediumDiminish = 60,
        FullDiminish = 30,
    }

    public static SkillInjectionPlan Plan(SkillInjectorProfile profile, int quantity, SkillInjectionInputs inputs, long now)
    {
        if (quantity <= 0)
        {
            throw new InvalidSkillInjection("InconsistentInjectorQuantity");
        }
        if (quantity > MaximumBatch)
        {
            throw new InvalidSkillInjection("TooManyInjectorsInjected");
        }
        if (
            inputs.Allocated < 0
            || inputs.Unallocated < 0
            || inputs.AvailableUses < 0
            || profile.Points <= 0
            || !Enum.IsDefined(inputs.Grade)
            || !Enum.IsDefined(profile.Kind)
        )
        {
            throw new InvalidDataException("Invalid skill injection state.");
        }
        long total = checked(inputs.Allocated + inputs.Unallocated);
        long before = total;
        long next = inputs.NextAlphaInjection;
        int uses = 0;
        if (profile.Kind == SkillInjectorKind.DailyAlpha)
        {
            if (inputs.Grade != AccountCloneGrade.Alpha)
            {
                throw new InvalidSkillInjection("RestrictedToAlphaClones");
            }
            if (quantity != 1)
            {
                throw new InvalidSkillInjection("InconsistentInjectorQuantity");
            }
            if (now < next)
            {
                throw new InvalidSkillInjection("AlreadyInjectedToday");
            }
            next = checked(now + TimeSpan.TicksPerDay);
        }
        for (int i = 0; i < quantity; i++)
        {
            long amount = profile.Points;
            if (profile.Kind == SkillInjectorKind.Diminishing)
            {
                if (uses < inputs.AvailableUses)
                {
                    uses++;
                }
                else
                {
                    var efficiency =
                        total < FreeZone ? Efficiency.Full
                        : total < LowDiminishLimit ? Efficiency.LowDiminish
                        : total < MediumDiminishLimit ? Efficiency.MediumDiminish
                        : Efficiency.FullDiminish;
                    amount = checked(amount * (int)efficiency / Percentage);
                }
            }
            if (profile.MaximumTotal is long maximum)
            {
                if (total >= maximum)
                {
                    throw new InvalidSkillInjection("InjectorSkillPointLimitReached", profile.TypeId, maximum);
                }
                amount = Math.Min(amount, maximum - total);
            }
            total = checked(total + amount);
        }
        return new(total - before, total, uses, next);
    }

    public static (int Consumed, int OutputType, int OutputQuantity) Conversion(int typeId, int quantity, bool split)
    {
        int expected = split ? (int)SkillInjectorType.Large : (int)SkillInjectorType.Small;
        if (typeId != expected)
        {
            throw new InvalidSkillInjection("IncorrectServiceItemType");
        }
        if (quantity <= 0 || (!split && quantity < SmallPerLarge))
        {
            throw new InvalidSkillInjection("InconsistentInjectorQuantity");
        }
        return split
            ? (quantity, (int)SkillInjectorType.Small, checked(quantity * SmallPerLarge))
            : (quantity / SmallPerLarge * SmallPerLarge, (int)SkillInjectorType.Large, quantity / SmallPerLarge);
    }
}
