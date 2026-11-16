using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Propulsion;

public enum InterdictionKind
{
    Probe,
    HeavyField,
    Projector,
}

public sealed record InterdictionSphere(long Id, SpacePosition Center, double Radius);

public sealed record InterdictionWarp(bool Blocked, SpacePosition Destination, long? FieldId = null);

public sealed record InterdictionActivation(
    long ModuleId,
    int TypeId,
    DogmaEffect Effect,
    double Duration,
    long ExpiresAt,
    long ReadyAt,
    double Capacitor,
    MagazineChange? Magazine = null,
    long? TargetId = null,
    double Range = 0
);

public sealed record InterdictionGenerator(double Duration, double Capacitor, double Range, double Strength)
{
    public static InterdictionGenerator Define(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        InterdictionGenerator result = new(
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleRange),
            attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStrength)
        );
        if (
            !double.IsFinite(result.Duration)
            || result.Duration <= 0
            || !double.IsFinite(result.Capacitor)
            || result.Capacitor < 0
            || !double.IsFinite(result.Range)
            || result.Range <= 0
            || !double.IsFinite(result.Strength)
            || result.Strength <= 0
        )
        {
            throw new InvalidDataException("Invalid heavy interdiction generator profile.");
        }
        return result;
    }
}

public sealed record InterdictionNullifier(double Duration, double Capacitor, double Reactivation)
{
    public static InterdictionNullifier Define(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        // Build 3662168 shares durationHighisGood (3115) with signature suppressors.
        InterdictionNullifier result = new(
            attributes.GetValueOrDefault(DogmaAttributeId.SignatureSuppressionDuration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.ModuleReactivationDelay)
        );
        if (
            !double.IsFinite(result.Duration)
            || result.Duration <= 0
            || !double.IsFinite(result.Capacitor)
            || result.Capacitor < 0
            || !double.IsFinite(result.Reactivation)
            || result.Reactivation < 0
        )
        {
            throw new InvalidDataException("Invalid interdiction nullifier profile.");
        }
        return result;
    }
}

public interface IPilotInterdiction
{
    bool IsLauncher(long pilotId, long moduleId);
    InterdictionActivation ActivateGenerator(
        long pilotId,
        long shipId,
        long moduleId,
        DogmaEffect effect,
        TackleTarget? target,
        IReadOnlySet<long> locked,
        long now
    );
    InterdictionActivation ActivateNullifier(long pilotId, long shipId, long moduleId, long now);
    void Stop(long pilotId, long shipId, long moduleId, long now);
    bool Active(long shipId, long moduleId, long now);
    InterdictionActivation Launch(long pilotId, long shipId, long moduleId, long now);
    InterdictionWarp Resolve(long shipId, int systemId, SpacePosition origin, SpacePosition destination, long now);
    IReadOnlyList<StaticSpaceObject> Contacts(int systemId, long now);
}

public static class InterdictionRules
{
    public const int LauncherGroup = 589;
    public const int ProbeGroup = 548;
    public const int GeneratorGroup = 899;
    public const int ScriptGroup = 908;
    public const int NullifierGroup = 4117;
    public const int FocusedDisruptionScript = 29003;
    public const int FocusedScramblingScript = 45010;

    public static bool Focused(DogmaEffect effect) => effect is DogmaEffect.FocusedWarpDisruption or DogmaEffect.FocusedWarpScrambling;

    public const int HazardSystem = 30100000;
    public const long FirstProbeId = 8_900_000_000_000;

    // The warp interception window follows the reference engine; it is a server policy, not a catalog attribute.
    public const double CaptureDistance = 500_000;

    public static void CheckSystem(int system, double security)
    {
        if (!double.IsFinite(security) || security > 0 || system == HazardSystem)
        {
            throw new InvalidDataException("Warp disruption bubbles require null security or wormhole space outside Zarzakh.");
        }
    }

    public static InterdictionWarp Resolve(
        SpacePosition origin,
        SpacePosition destination,
        IReadOnlyList<InterdictionSphere> fields,
        bool immune = false
    )
    {
        if (!Finite(origin) || !Finite(destination))
        {
            throw new InvalidDataException("Invalid interdiction warp coordinates.");
        }
        if (immune)
        {
            return new(false, destination);
        }
        var direction = destination - origin;
        double distance = direction.Length;
        double nearest = double.PositiveInfinity;
        long? selected = null;
        foreach (var field in fields.OrderBy(field => field.Id))
        {
            if (!Finite(field.Center) || !double.IsFinite(field.Radius) || field.Radius <= 0)
            {
                throw new InvalidDataException("Invalid interdiction field.");
            }
            if (origin.DistanceTo(field.Center) <= field.Radius)
            {
                return new(true, destination, field.Id);
            }
            if (distance <= 1 || field.Center.DistanceTo(destination) > CaptureDistance)
            {
                continue;
            }
            var unit = direction * (1 / distance);
            var relative = field.Center - origin;
            double along = relative.Dot(unit);
            double perpendicular = Math.Max(0, relative.Dot(relative) - along * along);
            double chord = field.Radius * field.Radius - perpendicular;
            if (chord <= 0)
            {
                continue;
            }
            double entry = along - Math.Sqrt(chord);
            if (entry <= 0 || entry > distance + CaptureDistance || entry >= nearest)
            {
                continue;
            }
            nearest = entry;
            selected = field.Id;
        }
        // Land a metre inside the surface so the same boundary rule blocks the next warp.
        return selected is null ? new(false, destination) : new(false, origin + direction.Unit * (nearest + 1), selected);
    }

    public static long Deadline(long now, double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds <= 0)
        {
            throw new InvalidDataException("Invalid interdiction duration.");
        }
        return checked(now + (long)Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond));
    }

    private static bool Finite(SpacePosition position) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z);
}
