using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Targeting;

namespace Eden.Gameplay.Structures;

public sealed record StructureTargetLock(long TargetId, long StartedAt, long ReadyAt, bool Locked);

public sealed record StructureTargetingState(long StructureId, IReadOnlyList<StructureTargetLock> Targets, IReadOnlyList<long> Targeters);

public sealed record StructureTargetingLimits(double Range, double ScanResolution, int MaximumTargets);

public interface IPilotStructureTargeting
{
    StructureTargetingState Add(StructureControlToken token, long targetId);
    StructureTargetingState Read(StructureControlToken token);
    StructureTargetingState Remove(StructureControlToken token, long targetId);
    StructureTargetingState Clear(StructureControlToken token);
    void RequireLocked(StructureControlToken token, long targetId);
}

public static class StructureTargetingRules
{
    // The pinned structure targeting service caps acquisition at 490 km, independently of ship targeting caps.
    public const double MaximumRange = 490_000;

    public static bool Active(bool hasCore, StructureState state) =>
        hasCore
        && StructureCombatRules.Vulnerable(state)
        && state is not (StructureState.Onlining or StructureState.AnchorVulnerable or StructureState.DeployVulnerable);

    public static StructureTargetingLimits Limits(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double range = attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange);
        double scan = attributes.GetValueOrDefault(DogmaAttributeId.ScanResolution);
        double count = attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets);
        if (
            !double.IsFinite(range)
            || range <= 0
            || !double.IsFinite(scan)
            || scan <= 0
            || !double.IsFinite(count)
            || count <= 0
            || count > int.MaxValue
            || count != Math.Truncate(count)
        )
        {
            throw new InvalidDataException("The structure has no functioning targeting systems.");
        }
        return new(Math.Min(MaximumRange, range), scan, checked((int)count));
    }

    public static bool InRange(
        StructureTargetingLimits limits,
        SpacePosition origin,
        double sourceRadius,
        SpacePosition target,
        double targetRadius
    ) => Math.Max(0, origin.DistanceTo(target) - sourceRadius - targetRadius) <= limits.Range;

    public static long ReadyAt(StructureTargetingLimits limits, double signatureRadius, long now)
    {
        if (!double.IsFinite(signatureRadius) || signatureRadius <= 0)
        {
            throw new InvalidDataException("The target has no valid signature for acquisition.");
        }
        double seconds = TargetingDefinitions.LockSeconds(limits.ScanResolution, signatureRadius);
        double ticks = Math.Ceiling(seconds * TimeSpan.TicksPerSecond);
        if (!double.IsFinite(ticks) || ticks <= 0 || ticks >= long.MaxValue - (double)now)
        {
            throw new InvalidDataException("The target has no valid signature for acquisition.");
        }
        return checked(now + (long)ticks);
    }
}
