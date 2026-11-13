using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record Smartbomb(
    long ItemId,
    int TypeId,
    double Range,
    double DurationMilliseconds,
    double CapacitorNeed,
    DamageVector Damage
);

public sealed record SmartbombShip(long Id, SpacePosition Position);

public sealed record SmartbombCandidates(
    IReadOnlyList<SmartbombShip> Ships,
    IReadOnlyList<long> Npcs,
    IReadOnlyList<long> Drones,
    IReadOnlyList<long> Missiles,
    IReadOnlyList<StaticSpaceObject> ProtectedObjects
);

public sealed record SmartbombHit(ShipHealth Health, double Applied, CombatReward? Reward = null);

public sealed record SmartbombPulse(
    Smartbomb Module,
    long ReadyAt,
    double Capacitor,
    IReadOnlyList<SmartbombHit> Hits,
    bool InterceptedMissiles
);

public interface IPilotSmartbombs
{
    SmartbombPulse Pulse(long pilotId, long shipId, long moduleId, SmartbombCandidates candidates, long now);
}

public static class SmartbombRules
{
    public const int Group = 72;
    public const int EnergyPulseWeaponsSkill = 3421;
    public const int StargateGroup = 10;
    public const double MinimumProtectedDistance = 5000;

    public static Smartbomb Define(long item, int type, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var damage = new DamageVector(
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        );
        double range = attributes.GetValueOrDefault(DogmaAttributeId.EmpFieldRange);
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.Duration);
        double capacitor = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        if (
            !damage.IsValid
            || damage.Total <= 0
            || !double.IsFinite(range)
            || range <= 0
            || !double.IsFinite(duration)
            || duration <= 0
            || !double.IsFinite(capacitor)
            || capacitor <= 0
        )
        {
            throw new InvalidDataException("Invalid smartbomb profile.");
        }
        return new(item, type, range, duration, capacitor, damage);
    }

    public static bool InRange(SpacePosition origin, double sourceRadius, SpacePosition target, double targetRadius, double range) =>
        origin.DistanceTo(target) <= sourceRadius + targetRadius + range;

    public static void CheckSafety(double security, ShipSafetyLevel safety, bool alpha)
    {
        if (security >= CrimewatchRules.HighSecurity && (safety != ShipSafetyLevel.Disabled || alpha))
        {
            throw new InvalidDataException("Smartbombs require red safety in high-security space.");
        }
    }
}
