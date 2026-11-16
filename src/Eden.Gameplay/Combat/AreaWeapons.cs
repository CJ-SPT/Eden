using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public enum AreaWeaponKind
{
    BurstJammer,
    Bomb,
    Defender,
    Projector,
}

public sealed record AreaWeaponProfile(
    long ItemId,
    int TypeId,
    AreaWeaponKind Kind,
    DogmaEffect Effect,
    double Duration,
    double CapacitorNeed,
    double Reactivation,
    double Range,
    double Radius,
    double Delay,
    double EffectDuration,
    int FuelType,
    int FuelQuantity,
    NpcEwarPower Power
);

public sealed record BombProfile(
    int TypeId,
    int GroupId,
    double Speed,
    double Lifetime,
    double Radius,
    double SignatureResolution,
    double SignatureRadius,
    HullDefense Defense,
    DamageVector Damage,
    NpcEwarPower Power
);

public sealed record AreaWeaponFlight(
    AreaWeaponProfile Module,
    BombProfile? Bomb,
    SpacePosition Origin,
    SpacePosition Velocity,
    SpacePosition Point,
    long StartedAt,
    long ResolvesAt,
    long TargetId = 0,
    HullDamage? Health = null,
    bool Active = false
)
{
    public SpacePosition Position(long now) =>
        Origin + Velocity * (Math.Clamp(now, StartedAt, ResolvesAt) - StartedAt) * (1.0 / TimeSpan.TicksPerSecond);
}

public sealed record AreaWeaponActivation(
    AreaWeaponProfile Module,
    long ReadyAt,
    double Capacitor,
    MagazineChange? Charge,
    long? FlightId,
    IReadOnlyList<InventoryChange> InventoryChanges
);

public interface IPilotAreaWeapons
{
    bool IsLauncher(long pilotId, long moduleId);
    AreaWeaponActivation Activate(
        long pilotId,
        long shipId,
        long moduleId,
        DogmaEffect effect,
        SpacePosition direction,
        SpacePosition velocity,
        SpacePosition? point,
        long now
    );
    void Advance(int systemId, long now);
    IReadOnlyList<MissileContact> Projectiles(int systemId, long now);
    void Cancel(long pilotId, long shipId, long now);
    void CancelModule(long pilotId, long shipId, long moduleId);
    bool Impacted(long projectileId);
}

public static class AreaWeaponRules
{
    public const int BombDeploymentSkill = 28073;
    public const int DefenderSkill = 3323;
    public const int ProjectorSkill = 27911;
    public const int BurstJammerGroup = 80;
    public const int ProjectorGroup = 842;
    public const int BombLauncherGroup = 862;
    public const int DefenderLauncherGroup = 512;
    public const int DefenderChargeGroup = 88;
    public const int DamageBombGroup = 90;
    public const int LockbreakerBombGroup = 863;
    public const int EnergyBombGroup = 864;
    public const long FirstFlightId = 9_700_000_000;

    public static bool IsModule(int group) => group is BurstJammerGroup or ProjectorGroup or BombLauncherGroup or DefenderLauncherGroup;

    public static bool Compatible(int moduleGroup, int chargeGroup, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        (
            moduleGroup == BombLauncherGroup && IsBomb(chargeGroup)
            || moduleGroup == DefenderLauncherGroup && chargeGroup == DefenderChargeGroup
        )
        && new[] { DogmaAttributeId.ChargeGroup1, DogmaAttributeId.ChargeGroup2, DogmaAttributeId.ChargeGroup3 }.Any(attribute =>
            attributes.GetValueOrDefault(attribute) == chargeGroup
        );

    public static bool IsBomb(int group) => group is DamageBombGroup or LockbreakerBombGroup or EnergyBombGroup;

    public static bool IsProjector(DogmaEffect effect) =>
        effect
            is DogmaEffect.ProjectorEcm
                or DogmaEffect.ProjectorWeb
                or DogmaEffect.ProjectorNeutralizer
                or DogmaEffect.ProjectorBubble
                or DogmaEffect.ProjectorDampener
                or DogmaEffect.ProjectorPainter
                or DogmaEffect.ProjectorDisruptor;

    public static AreaWeaponProfile Define(
        long item,
        int type,
        int group,
        DogmaEffect effect,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes
    )
    {
        double Value(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
        var kind = group switch
        {
            BurstJammerGroup when effect == DogmaEffect.BurstJammer => AreaWeaponKind.BurstJammer,
            ProjectorGroup when IsProjector(effect) => AreaWeaponKind.Projector,
            BombLauncherGroup when effect == DogmaEffect.UseMissiles => AreaWeaponKind.Bomb,
            DefenderLauncherGroup when effect == DogmaEffect.UseMissiles => AreaWeaponKind.Defender,
            _ => throw new InvalidDataException("Unsupported area weapon program."),
        };
        var durationAttribute = effect switch
        {
            DogmaEffect.ProjectorEcm => DogmaAttributeId.EcmProjectorDuration,
            DogmaEffect.ProjectorDampener => DogmaAttributeId.DampenerProjectorDuration,
            DogmaEffect.ProjectorPainter => DogmaAttributeId.PainterProjectorDuration,
            DogmaEffect.ProjectorDisruptor => DogmaAttributeId.DisruptorProjectorDuration,
            DogmaEffect.UseMissiles => DogmaAttributeId.WeaponCycleTime,
            _ => DogmaAttributeId.Duration,
        };
        NpcEwarPower power = new(
            TargetRangeMultiplier: 1 + Value(DogmaAttributeId.TargetRangeBonus) / 100,
            ScanResolutionMultiplier: 1 + Value(DogmaAttributeId.ScanResolutionBonus) / 100,
            OptimalPercent: Value(DogmaAttributeId.OptimalRangeBonus),
            FalloffPercent: Value(DogmaAttributeId.FalloffBonus),
            TrackingPercent: Value(DogmaAttributeId.TrackingSpeedBonus),
            SignaturePercent: Value(DogmaAttributeId.SignatureRadiusBonus),
            CapacitorDrain: Value(DogmaAttributeId.EnergyNeutralizerAmount),
            WebPercent: Value(DogmaAttributeId.SpeedFactor),
            Jamming: new(
                Value(DogmaAttributeId.RadarJammingStrength),
                Value(DogmaAttributeId.LadarJammingStrength),
                Value(DogmaAttributeId.MagnetometricJammingStrength),
                Value(DogmaAttributeId.GravimetricJammingStrength)
            ),
            MissileSpeedPercent: Value(DogmaAttributeId.MissileVelocityBonus),
            MissileLifetimePercent: Value(DogmaAttributeId.MissileLifetimeBonus),
            ExplosionRadiusPercent: Value(DogmaAttributeId.ExplosionRadiusBonus),
            ExplosionVelocityPercent: Value(DogmaAttributeId.ExplosionVelocityBonus)
        );
        AreaWeaponProfile result = new(
            item,
            type,
            kind,
            effect,
            Value(durationAttribute),
            Value(DogmaAttributeId.CapacitorNeed),
            Value(DogmaAttributeId.ModuleReactivationDelay),
            Value(kind == AreaWeaponKind.BurstJammer ? DogmaAttributeId.EcmBurstRange : DogmaAttributeId.MaxRange),
            Value(DogmaAttributeId.ProjectorRadius),
            Value(DogmaAttributeId.ProjectorWarningDuration),
            Value(DogmaAttributeId.ProjectorEffectDuration),
            checked((int)Value(DogmaAttributeId.ConsumptionType)),
            checked((int)Value(DogmaAttributeId.ConsumptionQuantity)),
            power
        );
        if (
            new[]
            {
                result.Duration,
                result.CapacitorNeed,
                result.Reactivation,
                result.Range,
                result.Radius,
                result.Delay,
                result.EffectDuration,
            }.Any(value => !double.IsFinite(value) || value < 0)
            || result.Duration <= 0
            || kind == AreaWeaponKind.BurstJammer && result.Range <= 0
            || kind == AreaWeaponKind.Projector
                && (result.Range <= 0 || result.Radius <= 0 || result.Delay <= 0 || result.FuelType <= 0 || result.FuelQuantity <= 0)
        )
        {
            throw new InvalidDataException("Invalid area weapon profile.");
        }
        return result;
    }

    public static SpacePosition? Intercept(SpacePosition origin, AreaWeaponFlight bomb, double speed, long now)
    {
        if (!double.IsFinite(speed) || speed <= 0 || now >= bomb.ResolvesAt)
        {
            return null;
        }
        var offset = bomb.Position(now) - origin;
        double a = bomb.Velocity.Dot(bomb.Velocity) - speed * speed;
        double b = 2 * offset.Dot(bomb.Velocity);
        double c = offset.Dot(offset);
        double seconds;
        if (Math.Abs(a) < 1e-9)
        {
            seconds = b < 0 ? -c / b : double.PositiveInfinity;
        }
        else
        {
            double discriminant = b * b - 4 * a * c;
            if (discriminant < 0)
            {
                return null;
            }
            double first = (-b - Math.Sqrt(discriminant)) / (2 * a);
            double second = (-b + Math.Sqrt(discriminant)) / (2 * a);
            seconds = new[] { first, second }.Where(value => value >= 0).DefaultIfEmpty(double.PositiveInfinity).Min();
        }
        return double.IsFinite(seconds) && seconds < (bomb.ResolvesAt - now) / (double)TimeSpan.TicksPerSecond
            ? bomb.Position(now) + bomb.Velocity * seconds
            : null;
    }

    public static DroneProfile ApplyDroneEffects(DroneProfile profile, IReadOnlyList<ElectronicWarfareContact> incoming)
    {
        if (incoming.Count == 0)
        {
            return profile;
        }
        var attributes = PlayerEwarRules.NpcAttributes(
            new Dictionary<DogmaAttributeId, double>
            {
                [DogmaAttributeId.MaxVelocity] = profile.MaximumSpeed,
                [DogmaAttributeId.MaxRange] = profile.Optimal,
                [DogmaAttributeId.AccuracyFalloff] = profile.Falloff,
                [DogmaAttributeId.TrackingSpeed] = profile.Tracking,
            },
            incoming
        );
        double ratio = profile.MaximumSpeed > 0 ? attributes[DogmaAttributeId.MaxVelocity] / profile.MaximumSpeed : 1;
        return profile with
        {
            MaximumSpeed = (float)attributes[DogmaAttributeId.MaxVelocity],
            OrbitSpeed = (float)(profile.OrbitSpeed * ratio),
            Optimal = attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            Falloff = attributes.GetValueOrDefault(DogmaAttributeId.AccuracyFalloff),
            Tracking = attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed),
        };
    }

    public static double SignatureFactor(double signature, double resolution) =>
        double.IsFinite(signature) && signature >= 0 && double.IsFinite(resolution) && resolution > 0
            ? Math.Min(1, signature / resolution)
            : throw new InvalidDataException("Invalid area weapon signature inputs.");

    public static long Deadline(long now, double milliseconds) =>
        double.IsFinite(milliseconds) && milliseconds >= 0
            ? checked(now + (long)Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond))
            : throw new InvalidDataException("Invalid area weapon deadline.");
}
