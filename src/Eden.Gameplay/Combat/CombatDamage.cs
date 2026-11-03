using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record DamageVector(double Em, double Explosive, double Kinetic, double Thermal)
{
    public static DamageVector None { get; } = new(0, 0, 0, 0);
    public double Total => Em + Explosive + Kinetic + Thermal;

    public DamageVector Scale(double multiplier) =>
        new(Em * multiplier, Explosive * multiplier, Kinetic * multiplier, Thermal * multiplier);

    public double Against(DamageVector resonance) =>
        Em * resonance.Em + Explosive * resonance.Explosive + Kinetic * resonance.Kinetic + Thermal * resonance.Thermal;

    internal bool IsValid => double.IsFinite(Total) && new[] { Em, Explosive, Kinetic, Thermal }.All(v => double.IsFinite(v) && v >= 0);
}

public sealed record HullDefense(
    double Shield,
    double Armor,
    double Structure,
    double ShieldRechargeMilliseconds,
    DamageVector ShieldResonance,
    DamageVector ArmorResonance,
    DamageVector StructureResonance
)
{
    public HullDamage Undamaged => new(Shield, Armor, Structure);
}

public sealed record HullDamage(double Shield, double Armor, double Structure)
{
    public bool Destroyed => Structure <= 0;

    public HullDamage Fractions(HullDefense defense) =>
        new(Fraction(Shield, defense.Shield), Fraction(Armor, defense.Armor), Fraction(Structure, defense.Structure));

    private static double Fraction(double value, double capacity) => capacity > 0 ? Math.Clamp(value / capacity, 0, 1) : 0;
}

public sealed record DamageResult(HullDamage Remaining, double Applied);

public static class CombatDamage
{
    public const double WreckingChance = 0.01;
    public const double WreckingMultiplier = 3;
    public const double HitQualityOffset = 0.49;

    public static double AngularVelocity(SpacePosition separation, SpacePosition relativeVelocity)
    {
        if (
            new[] { separation.X, separation.Y, separation.Z, relativeVelocity.X, relativeVelocity.Y, relativeVelocity.Z }.Any(v =>
                !double.IsFinite(v)
            )
        )
        {
            throw new ArgumentOutOfRangeException(nameof(separation));
        }
        double distance = separation.Length;
        if (distance == 0)
        {
            return 0;
        }
        double radial = relativeVelocity.Dot(separation.Unit);
        return Math.Sqrt(Math.Max(0, relativeVelocity.Dot(relativeVelocity) - radial * radial)) / distance;
    }

    // Tracking uses the catalog's signature reference. Player turrets use 40,000 m
    // in build 3662168; older NPC turrets retain their own reference and rad/s values.
    public static double HitChance(
        double distance,
        double angularVelocity,
        double targetSignature,
        double optimal,
        double falloff,
        double tracking,
        double signatureReference
    )
    {
        if (
            new[] { distance, angularVelocity, optimal, falloff, tracking, signatureReference }.Any(v => !double.IsFinite(v) || v < 0)
            || !double.IsFinite(targetSignature)
            || targetSignature <= 0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(distance),
                "Weapon geometry and attributes must be finite and nonnegative, with a positive target signature."
            );
        }
        double rangePenalty =
            distance <= optimal ? 0
            : falloff > 0 ? (distance - optimal) / falloff
            : double.PositiveInfinity;
        double trackingPenalty =
            angularVelocity == 0 ? 0
            : tracking > 0 ? angularVelocity * signatureReference / (tracking * targetSignature)
            : double.PositiveInfinity;
        return Math.Pow(0.5, trackingPenalty * trackingPenalty + rangePenalty * rangePenalty);
    }

    public static double ShotQuality(double chance, double roll)
    {
        // Eden's initial shot-quality model uses one draw for accuracy and
        // quality. The pinned client does not expose Tranquility's server RNG.
        if (!double.IsFinite(chance) || chance is < 0 or > 1 || !double.IsFinite(roll) || roll is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(roll));
        }
        if (roll >= chance)
        {
            return 0;
        }
        return roll < WreckingChance ? WreckingMultiplier : roll + HitQualityOffset;
    }

    public static DamageResult Apply(HullDamage current, HullDefense defense, DamageVector incoming)
    {
        if (
            !incoming.IsValid
            || !defense.ShieldResonance.IsValid
            || !defense.ArmorResonance.IsValid
            || !defense.StructureResonance.IsValid
            || new[] { current.Shield, current.Armor, current.Structure, defense.Shield, defense.Armor, defense.Structure }.Any(v =>
                !double.IsFinite(v) || v < 0
            )
        )
        {
            throw new ArgumentOutOfRangeException(nameof(incoming));
        }
        double applied = 0;
        var remaining = incoming;
        double Layer(double health, double capacity, DamageVector resonance)
        {
            health = Math.Min(health, capacity);
            if (health <= 0)
            {
                return 0;
            }
            double damage = remaining.Against(resonance);
            if (damage <= 0)
            {
                remaining = DamageVector.None;
                return health;
            }
            double absorbed = Math.Min(health, damage);
            applied += absorbed;
            // Carry the unspent original damage mixture into the next layer. Do
            // not apply the previous layer's resistances to overflow a second time.
            remaining = remaining.Scale(1 - absorbed / damage);
            return Math.Max(0, health - absorbed);
        }
        double shield = Layer(current.Shield, defense.Shield, defense.ShieldResonance);
        double armor = Layer(current.Armor, defense.Armor, defense.ArmorResonance);
        double structure = Layer(current.Structure, defense.Structure, defense.StructureResonance);
        return new(new(shield, armor, structure), applied);
    }
}
