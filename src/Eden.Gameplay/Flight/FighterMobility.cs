using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;

namespace Eden.Gameplay.Flight;

public enum FighterMobilityKind
{
    Microwarpdrive = 4,
    MicroJumpDrive = 5,
    Afterburner = 9,
    EvasiveManeuvers = 13,
}

public sealed record FighterMobility(
    FighterMobilityKind Kind,
    double DurationMilliseconds,
    double CooldownMilliseconds,
    double SpeedMultiplier,
    double SignatureMultiplier,
    DamageVector? ShieldResonance,
    double JumpRange = 0
);

public sealed record FighterJumpDestination(double X, double Y, double Z);

public sealed record FighterMobilityCycle(
    long StartedAt,
    long EndsAt,
    long ReadyAt,
    FighterJumpDestination? Destination = null,
    long JumpedAt = 0
)
{
    public bool Active(long now) => StartedAt > 0 && EndsAt > now;
}

public static class FighterMobilityRules
{
    public const int Slot = 1;

    public static DroneContact Apply(DroneContact contact, long now)
    {
        if (contact.Mobility is null || contact.Profile.Mobility is not { } mobility || contact.ShipId == 0)
        {
            return contact;
        }
        var profile = contact.Profile;
        var defense = profile.Defense;
        bool active = contact.Mobility.Active(now);
        if (active && mobility.ShieldResonance is { } modifier)
        {
            var shield = defense.ShieldResonance;
            defense = defense with
            {
                ShieldResonance = new(
                    shield.Em * modifier.Em,
                    shield.Explosive * modifier.Explosive,
                    shield.Kinetic * modifier.Kinetic,
                    shield.Thermal * modifier.Thermal
                ),
            };
        }
        float multiplier = active ? (float)mobility.SpeedMultiplier : 1;
        return contact with
        {
            Profile = profile with
            {
                MaximumSpeed = profile.MaximumSpeed * multiplier,
                OrbitSpeed = profile.OrbitSpeed * multiplier,
                Defense = defense,
            },
            SpeedLimit = (contact.Orbiting ? profile.OrbitSpeed : profile.MaximumSpeed) * multiplier,
            Health = contact.Health with { Defense = defense },
            MobilityActive = active,
            MobilitySignatureMultiplier = active ? mobility.SignatureMultiplier : 1,
        };
    }
}
