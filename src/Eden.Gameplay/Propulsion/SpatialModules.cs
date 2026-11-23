using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Propulsion;

public sealed record SpatialModule(
    long ItemId,
    int TypeId,
    DogmaEffect Effect,
    double DurationMilliseconds,
    double CapacitorNeed,
    double ReactivationMilliseconds,
    double TargetingDelayMilliseconds,
    double VelocityMultiplier,
    double SignatureBonus
)
{
    public bool Cloak => SpatialModuleRules.IsCloak(Effect);
    public MicroJumpField? Field { get; init; }
    public bool WarpSafe => Effect == DogmaEffect.CovertCloaking;
}

public sealed record SpatialActivation(
    SpatialModule Module,
    long StartedAt,
    long EndsAt,
    long ReadyAt,
    double Capacitor,
    IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes
);

public sealed record SpatialStatus(
    long CloakModuleId = 0,
    bool WarpSafe = false,
    long TargetingReadyAt = 0,
    long GateCloakUntil = 0,
    long FieldScrambledUntil = 0
)
{
    public bool GateCloaked(long now) => GateCloakUntil > now;

    public bool Cloaked(long now) => CloakModuleId != 0 || GateCloaked(now);
}

public interface IPilotGateCloak
{
    void Cancel(long pilotId, long shipId);
    bool PreservesCloak(long pilotId, long shipId, long moduleId);
}

public sealed record MicroJumpField(double Radius, int PassengerLimit, int CapitalLimit, double ScrambleMilliseconds);

public sealed record MicroJumpPassenger(long PilotId, long ShipId, SpacePosition Position);

public sealed record MicroJumpArrival(long ShipId, SpacePosition Position);

public sealed record MicroJumpResult(SpacePosition Position, SpacePosition Velocity)
{
    public IReadOnlyList<MicroJumpArrival> Passengers { get; init; } = [];
}

public interface IPilotSpatialModules
{
    SpatialActivation Activate(long pilotId, long moduleId, DogmaEffect effect, SpacePosition heading, SpacePosition velocity, long now);
    IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId, long now);
    bool Available(long pilotId, long shipId, long moduleId);
    SpatialStatus Status(long shipId);
    MicroJumpResult? CompleteJump(
        long pilotId,
        long shipId,
        long moduleId,
        long startedAt,
        long now,
        IReadOnlyList<MicroJumpPassenger>? candidates = null
    );
}

public static class SpatialModuleRules
{
    public const int CloakGroup = 330;
    public const int MicroJumpGroup = 1189;

    // Build 3662168 type 37479 is the only published group-1533 field generator.
    public const int MicroJumpFieldGroup = 1533;
    public const int SubcapitalField = 37479;
    public const int CapitalMicroJumpGroup = 4769;
    public const int CapitalDrive = 83465;
    public const int CapitalField = 83485;
    public const int CapitalMicroJumpSkill = 83464;
    public const int CloakingSkill = 11579;
    public const int MicroJumpSkill = 4385;
    public const double DecloakDistance = 2000;

    // Build 3662168 appConst.microJumpDriveDistance. Standard drives omit a range attribute.
    public const double JumpDistance = 100000;
    public const double ActivationGraceMilliseconds = 5000;

    public static bool IsDriveGroup(int group) => group is MicroJumpGroup or MicroJumpFieldGroup or CapitalMicroJumpGroup;

    public static bool IsDrive(int group, int type) =>
        group switch
        {
            MicroJumpGroup => true,
            MicroJumpFieldGroup => type == SubcapitalField,
            CapitalMicroJumpGroup => IsCapital(type),
            _ => false,
        };

    public static bool IsCapital(int type) => type is CapitalDrive or CapitalField;

    public static DogmaEffect JumpEffect(int type) =>
        type switch
        {
            SubcapitalField => DogmaEffect.MicroJumpField,
            CapitalField => DogmaEffect.CapitalMicroJumpField,
            _ => DogmaEffect.MicroJumpDrive,
        };

    public static bool IsField(DogmaEffect effect) => effect is DogmaEffect.MicroJumpField or DogmaEffect.CapitalMicroJumpField;

    public static double Distance(int type, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        type == SubcapitalField || IsCapital(type) ? attributes.GetValueOrDefault(DogmaAttributeId.MicroJumpRange) : JumpDistance;

    public static bool IsCloak(DogmaEffect effect) =>
        effect is DogmaEffect.Cloaking or DogmaEffect.PrototypeCloaking or DogmaEffect.CovertCloaking;

    public static SpatialModule Define(long item, int type, DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        if (!IsCloak(effect) && effect != JumpEffect(type))
        {
            throw new InvalidDataException("Unsupported spatial module effect.");
        }
        bool cloak = IsCloak(effect);
        SpatialModule module = new(
            item,
            type,
            effect,
            cloak ? 0 : attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.ModuleReactivationDelay),
            cloak ? attributes.GetValueOrDefault(DogmaAttributeId.CloakingTargetingDelay) : 0,
            cloak ? attributes.GetValueOrDefault(DogmaAttributeId.CloakVelocityMultiplier) : 1,
            cloak ? 0 : attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadiusBonusPercent)
        );
        double[] values =
        [
            module.DurationMilliseconds,
            module.CapacitorNeed,
            module.ReactivationMilliseconds,
            module.TargetingDelayMilliseconds,
            module.VelocityMultiplier,
            module.SignatureBonus,
        ];
        double distance = Distance(type, attributes);
        if (
            values.Any(v => !double.IsFinite(v) || v < 0)
            || module.VelocityMultiplier <= 0
            || !cloak && (module.DurationMilliseconds <= 0 || !double.IsFinite(distance) || distance <= 0)
        )
        {
            throw new InvalidDataException("Invalid spatial module attributes.");
        }
        if (IsField(effect))
        {
            double radius = attributes.GetValueOrDefault(DogmaAttributeId.MicroJumpFieldRadius);
            int passengers = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MicroJumpPassengerLimit));
            bool capital = effect == DogmaEffect.CapitalMicroJumpField;
            double scramble = capital ? attributes.GetValueOrDefault(DogmaAttributeId.MicroJumpScrambleDuration) : 0;
            int capitals = capital ? checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MicroJumpCapitalLimit)) : 0;
            if (
                !double.IsFinite(radius)
                || radius <= 0
                || passengers <= 0
                || capital && (!double.IsFinite(scramble) || scramble <= 0 || capitals <= 0)
            )
            {
                throw new InvalidDataException("Invalid micro jump field attributes.");
            }
            module = module with { Field = new(radius, passengers, capitals, scramble) };
        }
        return module;
    }

    public static long Deadline(long now, double milliseconds) =>
        checked(now + (long)Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond));

    public static SpacePosition Jump(SpacePosition origin, SpacePosition heading, double distance = JumpDistance)
    {
        if (!Finite(origin) || !Finite(heading) || heading.Length <= 0 || !double.IsFinite(heading.Length))
        {
            throw new InvalidDataException("A micro jump requires finite coordinates and a heading.");
        }
        var destination = origin + heading.Unit * distance;
        if (!Finite(destination))
        {
            throw new InvalidDataException("Invalid micro-jump destination.");
        }
        return destination;
    }

    public static bool Proximity(SpacePosition own, double ownRadius, SpacePosition other, double otherRadius) =>
        own.DistanceTo(other) - ownRadius - otherRadius < DecloakDistance;

    public static bool Finite(SpacePosition value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
