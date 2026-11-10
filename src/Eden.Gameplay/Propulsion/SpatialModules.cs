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
    public bool Cloak => Effect != DogmaEffect.MicroJumpDrive;
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

public sealed record SpatialStatus(long CloakModuleId = 0, bool WarpSafe = false, long TargetingReadyAt = 0);

public sealed record MicroJumpResult(SpacePosition Position, SpacePosition Velocity);

public interface IPilotSpatialModules
{
    SpatialActivation Activate(long pilotId, long moduleId, DogmaEffect effect, SpacePosition heading, SpacePosition velocity, long now);
    IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId, long now);
    bool Available(long pilotId, long shipId, long moduleId);
    SpatialStatus Status(long shipId);
    MicroJumpResult? CompleteJump(long pilotId, long shipId, long moduleId, long startedAt, long now);
}

public static class SpatialModuleRules
{
    public const int CloakGroup = 330;
    public const int MicroJumpGroup = 1189;
    public const int CloakingSkill = 11579;
    public const int MicroJumpSkill = 4385;
    public const double DecloakDistance = 2000;

    // Build 3662168 appConst.microJumpDriveDistance. Standard drives omit a range attribute.
    public const double JumpDistance = 100000;
    public const double ActivationGraceMilliseconds = 5000;

    public static bool IsCloak(DogmaEffect effect) =>
        effect is DogmaEffect.Cloaking or DogmaEffect.PrototypeCloaking or DogmaEffect.CovertCloaking;

    public static SpatialModule Define(long item, int type, DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        if (!IsCloak(effect) && effect != DogmaEffect.MicroJumpDrive)
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
        if (values.Any(v => !double.IsFinite(v) || v < 0) || module.VelocityMultiplier <= 0 || !cloak && module.DurationMilliseconds <= 0)
        {
            throw new InvalidDataException("Invalid spatial module attributes.");
        }
        return module;
    }

    public static long Deadline(long now, double milliseconds) =>
        checked(now + (long)Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond));

    public static SpacePosition Jump(SpacePosition origin, SpacePosition heading)
    {
        if (!Finite(origin) || !Finite(heading) || heading.Length <= 0 || !double.IsFinite(heading.Length))
        {
            throw new InvalidDataException("A micro jump requires finite coordinates and a heading.");
        }
        var destination = origin + heading.Unit * JumpDistance;
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
